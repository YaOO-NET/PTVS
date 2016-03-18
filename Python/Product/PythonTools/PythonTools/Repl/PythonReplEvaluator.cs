// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Options;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Project;
using Microsoft.VisualStudio.InteractiveWindow.Commands;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudioTools;

namespace Microsoft.PythonTools.Repl {
    [InteractiveWindowRole("Execution")]
    [InteractiveWindowRole("Reset")]
    [ContentType(PythonCoreConstants.ContentType)]
    [ContentType(PredefinedInteractiveCommandsContentTypes.InteractiveCommandContentTypeName)]
    internal class PythonReplEvaluator : BasePythonReplEvaluator {
        private IPythonInterpreterFactory _interpreter;
        private readonly IInterpreterRegistryService _interpreterService;
        private VsProjectAnalyzer _replAnalyzer;
        private bool _ownsAnalyzer, _enableAttach, _supportsMultipleCompleteStatementInputs;

        public PythonReplEvaluator(IPythonInterpreterFactory interpreter, IServiceProvider serviceProvider, IInterpreterRegistryService interpreterService = null)
            : this(interpreter, serviceProvider, new DefaultPythonReplEvaluatorOptions(serviceProvider, () => serviceProvider.GetPythonToolsService().GetInteractiveOptions(interpreter.Configuration)), interpreterService) {
        }

        public PythonReplEvaluator(IPythonInterpreterFactory interpreter, IServiceProvider serviceProvider, PythonReplEvaluatorOptions options, IInterpreterRegistryService interpreterService = null)
            : base(serviceProvider, serviceProvider.GetPythonToolsService(), options) {
            _interpreter = interpreter;
            _interpreterService = interpreterService;
            if (_interpreterService != null) {
                _interpreterService.InterpretersChanged += InterpretersChanged;
            }
        }

        private class UnavailableFactory : IPythonInterpreterFactory {
            public UnavailableFactory(string id) {
                Configuration = new InterpreterConfiguration(id, "Unavailable", new Version(0, 0));
            }
            public string Description { get { return Configuration.Id.ToString(); } }
            public InterpreterConfiguration Configuration { get; private set; }
            public Guid Id { get { return Guid.Empty; } }
            public IPythonInterpreter CreateInterpreter() { return null; }
        }

        public static IPythonReplEvaluator Create(
            IServiceProvider serviceProvider,
            string id,
            IInterpreterRegistryService interpreterService
        ) {
            var factory = serviceProvider.GetComponentModel().DefaultExportProvider.GetInterpreterFactory(id);
            if (factory == null) {
                try {
                    factory = new UnavailableFactory(id);
                } catch (FormatException) {
                    return null;
                }
            }
            return new PythonReplEvaluator(factory, serviceProvider, interpreterService);
        }

        async void InterpretersChanged(object sender, EventArgs e) {
            var current = _interpreter;
            if (current == null) {
                return;
            }

            var interpreter = _serviceProvider.GetComponentModel().DefaultExportProvider.GetInterpreterFactory(current.Configuration.Id);
            if (interpreter != null && interpreter != current) {
                // the interpreter has been reconfigured, we want the new settings
                _interpreter = interpreter;
                if (_replAnalyzer != null) {
                    var oldAnalyser = _replAnalyzer;
                    bool disposeOld = _ownsAnalyzer && oldAnalyser != null;
                    
                    _replAnalyzer = null;
                    var newAnalyzer = ReplAnalyzer;
                    if (newAnalyzer != null && oldAnalyser != null) {
                        newAnalyzer.SwitchAnalyzers(oldAnalyser);
                    }
                    if (disposeOld) {
                        oldAnalyser.Dispose();
                    }
                }

                // if the previous interpreter was not available, we will want
                // to reset afterwards
                if (current is UnavailableFactory) {
                    await Reset();
                }
            }
        }

        public IPythonInterpreterFactory Interpreter {
            get {
                return _interpreter;
            }
        }

        internal VsProjectAnalyzer ReplAnalyzer {
            get {
                if (_replAnalyzer == null && Interpreter != null && _interpreterService != null) {
                    _replAnalyzer = new VsProjectAnalyzer(_serviceProvider, Interpreter);
                    _ownsAnalyzer = true;
                }
                return _replAnalyzer;
            }
        }

        protected override PythonLanguageVersion AnalyzerProjectLanguageVersion {
            get {
                if (_replAnalyzer != null ) {
                    return _replAnalyzer.LanguageVersion;
                }
                return LanguageVersion;
            }
        }

        protected override PythonLanguageVersion LanguageVersion {
            get {
                return Interpreter != null ? Interpreter.GetLanguageVersion() : PythonLanguageVersion.None;
            }
        }

        internal override string DisplayName {
            get {
                return Interpreter != null ? Interpreter.Configuration.Description : string.Empty;
            }
        }

        public bool AttachEnabled {
            get {
                return _enableAttach && !(Interpreter is UnavailableFactory);
            }
        }

        public override void Dispose() {
            if (_ownsAnalyzer && _replAnalyzer != null) {
                _replAnalyzer.Dispose();
                _replAnalyzer = null;
            }
            base.Dispose();
        }

        public override void Close() {
            base.Close();
            if (_interpreterService != null) {
                _interpreterService.InterpretersChanged -= InterpretersChanged;
            }
        }

        public override bool SupportsMultipleCompleteStatementInputs {
            get {
                return _supportsMultipleCompleteStatementInputs;
            }
        }

        protected override void WriteInitializationMessage() {
            if (Interpreter is UnavailableFactory) {
                Window.WriteError(Strings.ReplEvaluatorInterpreterNotFound);
            } else {
                base.WriteInitializationMessage();
            }
        }

        protected override void Connect() {
            _serviceProvider.GetUIThread().MustBeCalledFromUIThread();
            
            var configurableOptions = CurrentOptions as ConfigurablePythonReplOptions;
            if (configurableOptions != null) {
                _interpreter = configurableOptions.InterpreterFactory ?? _interpreter;
            }

            if (Interpreter == null || Interpreter is UnavailableFactory) {
                Window.WriteError(Strings.ReplEvaluatorInterpreterNotFound);
                return;
            } else if (String.IsNullOrWhiteSpace(Interpreter.Configuration.InterpreterPath)) {
                Window.WriteError(Strings.ReplEvaluatorInterpreterNotConfigured.FormatUI(Interpreter.Configuration.Description));
                return;
            }
            var processInfo = new ProcessStartInfo(Interpreter.Configuration.InterpreterPath);

#if DEBUG
            bool debugMode = Environment.GetEnvironmentVariable("DEBUG_REPL") != null;
            processInfo.CreateNoWindow = !debugMode;
            processInfo.UseShellExecute = debugMode;
            processInfo.RedirectStandardOutput = !debugMode;
            processInfo.RedirectStandardError = !debugMode;
            processInfo.RedirectStandardInput = !debugMode;
#else
            processInfo.CreateNoWindow = true;
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardOutput = true;
            processInfo.RedirectStandardError = true;
            processInfo.RedirectStandardInput = true;
#endif

            Socket conn;
            int portNum;
            CreateConnection(out conn, out portNum);

            
            List<string> args = new List<string>();

            if (!String.IsNullOrWhiteSpace(CurrentOptions.InterpreterOptions)) {
                args.Add(CurrentOptions.InterpreterOptions);
            }

            var workingDir = CurrentOptions.WorkingDirectory;
            if (!string.IsNullOrEmpty(workingDir)) {
                processInfo.WorkingDirectory = workingDir;
            } else {
                processInfo.WorkingDirectory = Interpreter.Configuration.PrefixPath;
            }

#if DEBUG
            if (!debugMode) {
#endif
                var envVars = CurrentOptions.EnvironmentVariables;
                if (envVars != null) {
                    foreach (var keyValue in envVars) {
                        processInfo.EnvironmentVariables[keyValue.Key] = keyValue.Value;
                    }
                }

                string pathEnvVar = Interpreter.Configuration.PathEnvironmentVariable ?? "";

                if (!string.IsNullOrWhiteSpace(pathEnvVar)) {
                    var searchPaths = CurrentOptions.SearchPaths;

                    if (string.IsNullOrEmpty(searchPaths)) {
                        if (_serviceProvider.GetPythonToolsService().GeneralOptions.ClearGlobalPythonPath) {
                            processInfo.EnvironmentVariables[pathEnvVar] = "";
                        }
                    } else if (_serviceProvider.GetPythonToolsService().GeneralOptions.ClearGlobalPythonPath) {
                        processInfo.EnvironmentVariables[pathEnvVar] = searchPaths;
                    } else {
                        processInfo.EnvironmentVariables[pathEnvVar] = searchPaths + ";" + Environment.GetEnvironmentVariable(pathEnvVar);
                    }
                }
#if DEBUG
            }
#endif
            var interpreterArgs = CurrentOptions.InterpreterArguments;
            if (!String.IsNullOrWhiteSpace(interpreterArgs)) {
                args.Add(interpreterArgs);
            }

            var analyzer = CurrentOptions.ProjectAnalyzer;
            if (analyzer != null && analyzer.InterpreterFactory == _interpreter) {
                if (_replAnalyzer != null && _replAnalyzer != analyzer) {
                    analyzer.SwitchAnalyzers(_replAnalyzer);
                }
                _replAnalyzer = analyzer;
                _ownsAnalyzer = false;
            }

            args.Add(ProcessOutput.QuoteSingleArgument(PythonToolsInstallPath.GetFile("visualstudio_py_repl.py")));
            args.Add("--port");
            args.Add(portNum.ToString());

            if (!String.IsNullOrWhiteSpace(CurrentOptions.StartupScript)) {
                args.Add("--launch_file");
                args.Add(ProcessOutput.QuoteSingleArgument(CurrentOptions.StartupScript));
            }

            _enableAttach = CurrentOptions.EnableAttach;
            if (CurrentOptions.EnableAttach) {
                args.Add("--enable-attach");
            }

            bool multipleScopes = true;
            if (!String.IsNullOrWhiteSpace(CurrentOptions.ExecutionMode)) {
                // change ID to module name if we have a registered mode
                var modes = Microsoft.PythonTools.Options.ExecutionMode.GetRegisteredModes(_serviceProvider);
                string modeValue = CurrentOptions.ExecutionMode;
                foreach (var mode in modes) {
                    if (mode.Id == CurrentOptions.ExecutionMode) {
                        modeValue = mode.Type;
                        multipleScopes = mode.SupportsMultipleScopes;
                        _supportsMultipleCompleteStatementInputs = mode.SupportsMultipleCompleteStatementInputs;
                        break;
                    }
                }
                args.Add("--execution_mode");
                args.Add(modeValue);
            }

            SetMultipleScopes(multipleScopes);

            processInfo.Arguments = String.Join(" ", args);

            var process = new Process();
            process.StartInfo = processInfo;
            try {
                if (!File.Exists(processInfo.FileName)) {
                    throw new Win32Exception(Microsoft.VisualStudioTools.Project.NativeMethods.ERROR_FILE_NOT_FOUND);
                }
                process.Start();
            } catch (Exception e) {
                if (e.IsCriticalException()) {
                    throw;
                }

                Win32Exception wex = e as Win32Exception;
                if (wex != null && wex.NativeErrorCode == Microsoft.VisualStudioTools.Project.NativeMethods.ERROR_FILE_NOT_FOUND) {
                    Window.WriteError(Strings.ReplEvaluatorInterpreterNotFound);
                } else {
                    Window.WriteError(Strings.ErrorStartingInteractiveProcess.FormatUI(e.ToString()));
                }
                return;
            }

            CreateCommandProcessor(conn, processInfo.RedirectStandardOutput, process);
        }

        const int ERROR_FILE_NOT_FOUND = 2;
    }


    [InteractiveWindowRole("DontPersist")]
    class PythonReplEvaluatorDontPersist : PythonReplEvaluator {
        public PythonReplEvaluatorDontPersist(IPythonInterpreterFactory interpreter, IServiceProvider serviceProvider, PythonReplEvaluatorOptions options, IInterpreterRegistryService interpreterService) :
            base(interpreter, serviceProvider, options, interpreterService) {
        }
    }

    /// <summary>
    /// Base class used for providing REPL options
    /// </summary>
    abstract class PythonReplEvaluatorOptions {
        public abstract string InterpreterOptions {
            get;
        }

        public abstract string WorkingDirectory {
            get;
        }

        public abstract IDictionary<string, string> EnvironmentVariables {
            get;
        }

        public abstract string StartupScript {
            get;
        }

        public abstract string SearchPaths {
            get;
        }

        public abstract string InterpreterArguments {
            get;
        }

        public abstract VsProjectAnalyzer ProjectAnalyzer {
            get;
        }

        public abstract bool UseInterpreterPrompts {
            get;
        }

        public abstract string ExecutionMode {
            get;
        }

        public abstract bool EnableAttach {
            get;
        }

        public abstract bool ReplSmartHistory {
            get;
        }

        public abstract bool LiveCompletionsOnly {
            get;
        }

        public abstract string PrimaryPrompt {
            get;
        }

        public abstract string SecondaryPrompt {
            get;
        }
    }

    class ConfigurablePythonReplOptions : PythonReplEvaluatorOptions {
        private IPythonInterpreterFactory _factory;
        private PythonProjectNode _project;

        internal string _interpreterOptions;
        internal string _workingDir;
        internal IDictionary<string, string> _envVars;
        internal string _startupScript;
        internal string _searchPaths;
        internal string _interpreterArguments;
        internal VsProjectAnalyzer _projectAnalyzer;
        internal bool _useInterpreterPrompts;
        internal string _executionMode;
        internal bool _liveCompletionsOnly;
        internal bool _replSmartHistory;
        internal bool _enableAttach;
        internal string _primaryPrompt;
        internal string _secondaryPrompt;

        public ConfigurablePythonReplOptions() {
            _replSmartHistory = true;
            _primaryPrompt = ">>> ";
            _secondaryPrompt = "... ";
        }

        internal ConfigurablePythonReplOptions Clone() {
            var newOptions = (ConfigurablePythonReplOptions)MemberwiseClone();
            if (_envVars != null) {
                newOptions._envVars = new Dictionary<string, string>();
                foreach (var kv in _envVars) {
                    newOptions._envVars[kv.Key] = kv.Value;
                }
            }
            return newOptions;
        }

        public IPythonInterpreterFactory InterpreterFactory {
            get { return _factory; }
            set { _factory = value; }
        }

        public PythonProjectNode Project {
            get { return _project; }
            set { _project = value; }
        }

        public override string InterpreterOptions {
            get { return _interpreterOptions ?? ""; }
        }

        public override string WorkingDirectory {
            get {
                if (_project != null && string.IsNullOrEmpty(_workingDir)) {
                    return _project.GetWorkingDirectory();
                }
                return _workingDir ?? "";
            }
        }

        public override IDictionary<string, string> EnvironmentVariables {
            get { return _envVars; }
        }

        public override string StartupScript {
            get { return _startupScript ?? ""; }
        }

        public override string SearchPaths {
            get {
                if (_project != null && string.IsNullOrEmpty(_searchPaths)) {
                    return string.Join(new string(Path.PathSeparator, 1), _project.GetSearchPaths());
                }
                return _searchPaths ?? "";
            }
        }

        public override string InterpreterArguments {
            get { return _interpreterArguments ?? ""; }
        }

        public override VsProjectAnalyzer ProjectAnalyzer {
            get { return _projectAnalyzer; }
        }

        public override bool UseInterpreterPrompts {
            get { return _useInterpreterPrompts; }
        }

        public override string ExecutionMode {
            get { return _executionMode; }
        }

        public override bool EnableAttach {
            get { return _enableAttach; }
        }

        public override bool ReplSmartHistory {
            get { return _replSmartHistory; }
        }

        public override bool LiveCompletionsOnly {
            get { return _liveCompletionsOnly; }
        }

        public override string PrimaryPrompt {
            get { return _primaryPrompt; }
        }

        public override string SecondaryPrompt {
            get { return _secondaryPrompt; }
        }
    }

    /// <summary>
    /// Provides REPL options based upon options stored in our UI.
    /// </summary>
    class DefaultPythonReplEvaluatorOptions : PythonReplEvaluatorOptions {
        private readonly Func<PythonInteractiveCommonOptions> _options;
        private readonly IServiceProvider _serviceProvider;

        public DefaultPythonReplEvaluatorOptions(IServiceProvider serviceProvider, Func<PythonInteractiveCommonOptions> options) {
            _serviceProvider = serviceProvider;
            _options = options;
        }

        public override string InterpreterOptions {
            get {
                return ((PythonInteractiveOptions)_options()).InterpreterOptions;
            }
        }

        public override bool EnableAttach {
            get {
                return ((PythonInteractiveOptions)_options()).EnableAttach;
            }
        }

        public override string StartupScript {
            get {
                return ((PythonInteractiveOptions)_options()).StartupScript;
            }
        }

        public override string ExecutionMode {
            get {
                return ((PythonInteractiveOptions)_options()).ExecutionMode;
            }
        }

        public override string WorkingDirectory {
            get {
                var startupProj = PythonToolsPackage.GetStartupProject(_serviceProvider);
                if (startupProj != null) {
                    return startupProj.GetWorkingDirectory();
                }

                var textView = CommonPackage.GetActiveTextView(_serviceProvider);
                if (textView != null) {
                    return Path.GetDirectoryName(textView.GetFilePath());
                }

                return null;
            }
        }

        public override IDictionary<string, string> EnvironmentVariables {
            get {
                return null;
            }
        }

        public override string SearchPaths {
            get {
                var startupProj = PythonToolsPackage.GetStartupProject(_serviceProvider) as IPythonProject;
                if (startupProj != null) {
                    return string.Join(";", startupProj.GetSearchPaths());
                }

                return null;
            }
        }

        public override string InterpreterArguments {
            get {
                var startupProj = PythonToolsPackage.GetStartupProject(_serviceProvider);
                if (startupProj != null) {
                    return startupProj.GetProjectProperty(PythonConstants.InterpreterArgumentsSetting, true);
                }
                return null;
            }
        }

        public override VsProjectAnalyzer ProjectAnalyzer {
            get {
                var startupProj = PythonToolsPackage.GetStartupProject(_serviceProvider);
                if (startupProj != null) {
                    return ((PythonProjectNode)startupProj).GetAnalyzer();
                }
                return null;
            }
        }

        public override bool UseInterpreterPrompts {
            get { return _options().UseInterpreterPrompts; }
        }

        public override bool ReplSmartHistory {
            get { return _options().ReplSmartHistory; }
        }

        public override bool LiveCompletionsOnly {
            get { return _options().LiveCompletionsOnly; }
        }

        public override string PrimaryPrompt {
            get { return _options().PrimaryPrompt; }
        }

        public override string SecondaryPrompt {
            get { return _options().SecondaryPrompt;  }
        }
    }
}