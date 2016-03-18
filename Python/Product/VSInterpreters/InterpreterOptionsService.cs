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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Microsoft.PythonTools.Interpreter {
    [Export(typeof(IInterpreterRegistryService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    sealed class InterpreterRegistryService : IInterpreterRegistryService, IDisposable {
        private Lazy<IPythonInterpreterFactoryProvider, Dictionary<string, object>>[] _providers;
        private readonly object _suppressInterpretersChangedLock = new object();
        IPythonInterpreterFactory _noInterpretersValue;
        private int _suppressInterpretersChanged;
        private bool _raiseInterpretersChanged, _factoryChangesWatched;
        private EventHandler _interpretersChanged;
        internal static Guid NoInterpretersFactoryGuid = new Guid("{15CEBB59-1008-4305-97A9-CF5E2CB04711}");
        internal const string NoInterpretersFactoryProvider = "NoInterpreters";


        private Dictionary<IPythonInterpreterFactory, Dictionary<object, LockInfo>> _locks;
        private readonly object _locksLock = new object();
        [ImportingConstructor]
        public InterpreterRegistryService([ImportMany]params Lazy<IPythonInterpreterFactoryProvider, Dictionary<string, object>>[] providers) {
            _providers = providers;
        }

        public IEnumerable<IPythonInterpreterFactory> Interpreters {
            get {
                return new InterpretersEnumerable(this);
            }
        }

        public IEnumerable<InterpreterConfiguration> Configurations {
            get {
                return _providers.GetConfigurations().Values
                    .OrderBy(config => config.Description)
                    .ThenBy(config => config.Version);
            }
        }

        public IPythonInterpreterFactory FindInterpreter(string id) {
            return _providers.GetInterpreterFactory(id);
        }

        public event EventHandler InterpretersChanged {
            add {
                EnsureFactoryChangesWatched();

                _interpretersChanged += value;
            }
            remove {
                _interpretersChanged -= value;
            }
        }

        private void EnsureFactoryChangesWatched() {
            if (!_factoryChangesWatched) {
                BeginSuppressInterpretersChangedEvent();
                try {
                    foreach (var provider in _providers) {
                        IPythonInterpreterFactoryProvider providerValue;
                        try {
                            providerValue = provider.Value;
                        } catch (CompositionException) {
                            continue;
                        }
                        providerValue.InterpreterFactoriesChanged += Provider_InterpreterFactoriesChanged;
                    }
                } finally {
                    EndSuppressInterpretersChangedEvent();
                }
                _factoryChangesWatched = true;
            }
        }

        public void BeginSuppressInterpretersChangedEvent() {
            lock (_suppressInterpretersChangedLock) {
                _suppressInterpretersChanged += 1;
            }
        }

        public void EndSuppressInterpretersChangedEvent() {
            bool shouldRaiseEvent = false;
            lock (_suppressInterpretersChangedLock) {
                _suppressInterpretersChanged -= 1;

                if (_suppressInterpretersChanged == 0 && _raiseInterpretersChanged) {
                    shouldRaiseEvent = true;
                    _raiseInterpretersChanged = false;
                }
            }

            if (shouldRaiseEvent) {
                OnInterpretersChanged();
            }
        }

        public IEnumerable<IPythonInterpreterFactory> InterpretersOrDefault {
            get {
                bool anyYielded = false;
                foreach (var factory in Interpreters) {
                    Debug.Assert(factory != NoInterpretersValue);
                    yield return factory;
                    anyYielded = true;
                }

                if (!anyYielded) {
                    yield return NoInterpretersValue;
                }
            }
        }

        public IPythonInterpreterFactory NoInterpretersValue {
            get {
                if (_noInterpretersValue == null) {
                    try {
                        _noInterpretersValue = InterpreterFactoryCreator.CreateInterpreterFactory(
                            new InterpreterFactoryCreationOptions {
                                Id = NoInterpretersFactoryProvider,
                                Description = "No Interpreters",
                                LanguageVersion = new Version(2, 7)
                            }
                        );
                    } catch (Exception ex) {
                        Trace.TraceError("Failed to create NoInterpretersValue:\n{0}", ex);
                    }
                }
                return _noInterpretersValue;
            }
        }

        public async Task<object> LockInterpreterAsync(IPythonInterpreterFactory factory, object moniker, TimeSpan timeout) {
            LockInfo info;
            Dictionary<object, LockInfo> locks;

            lock (_locksLock) {
                if (_locks == null) {
                    _locks = new Dictionary<IPythonInterpreterFactory, Dictionary<object, LockInfo>>();
                }

                if (!_locks.TryGetValue(factory, out locks)) {
                    _locks[factory] = locks = new Dictionary<object, LockInfo>();
                }

                if (!locks.TryGetValue(moniker, out info)) {
                    locks[moniker] = info = new LockInfo();
                }
            }

            Interlocked.Increment(ref info._lockCount);
            bool result = false;
            try {
                result = await info._lock.WaitAsync(timeout.TotalDays > 1 ? Timeout.InfiniteTimeSpan : timeout);
                return result ? (object)info : null;
            } finally {
                if (!result) {
                    Interlocked.Decrement(ref info._lockCount);
                }
            }
        }

        public bool IsInterpreterLocked(IPythonInterpreterFactory factory, object moniker) {
            LockInfo info;
            Dictionary<object, LockInfo> locks;

            lock (_locksLock) {
                return _locks != null &&
                    _locks.TryGetValue(factory, out locks) &&
                    locks.TryGetValue(moniker, out info) &&
                    info._lockCount > 0;
            }
        }

        public bool UnlockInterpreter(object cookie) {
            var info = cookie as LockInfo;
            if (info == null) {
                throw new ArgumentException("cookie was not returned from a call to LockInterpreterAsync");
            }

            bool res = Interlocked.Decrement(ref info._lockCount) == 0;
            info._lock.Release();
            return res;
        }

        public static bool IsNoInterpretersFactory(string id) {
            return id.StartsWith(NoInterpretersFactoryProvider + "|");
        }

        private sealed class InterpretersEnumerator : IEnumerator<IPythonInterpreterFactory> {
            private readonly InterpreterRegistryService _owner;
            private readonly IEnumerator<IPythonInterpreterFactory> _e;

            public InterpretersEnumerator(InterpreterRegistryService owner, IEnumerator<IPythonInterpreterFactory> e) {
                _owner = owner;
                _owner.BeginSuppressInterpretersChangedEvent();
                _e = e;
            }

            public void Dispose() {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing) {
                if (disposing) {
                    _e.Dispose();
                }
                _owner.EndSuppressInterpretersChangedEvent();
            }

            ~InterpretersEnumerator() {
                Debug.Fail("Interpreter enumerator should always be disposed");
                Dispose(false);
            }

            public IPythonInterpreterFactory Current { get { return _e.Current; } }
            object IEnumerator.Current { get { return _e.Current; } }
            public bool MoveNext() { return _e.MoveNext(); }
            public void Reset() { _e.Reset(); }
        }

        private sealed class InterpretersEnumerable : IEnumerable<IPythonInterpreterFactory> {
            private readonly InterpreterRegistryService _owner;
            private readonly IEnumerable<IPythonInterpreterFactory> _e;

            private static IList<IPythonInterpreterFactory> GetFactories(IPythonInterpreterFactoryProvider provider) {
                if (provider == null) {
                    return Array.Empty<IPythonInterpreterFactory>();
                }

                while (true) {
                    try {
                        var res = new List<IPythonInterpreterFactory>();
                        foreach (var f in provider.GetInterpreterFactories()) {
                            res.Add(f);
                        }
                        return res;
                    } catch (InvalidOperationException ex) {
                        // Collection changed, so retry
                        Debug.WriteLine("Retrying GetInterpreterFactories because " + ex.Message);
                    }
                }
            }

            public InterpretersEnumerable(InterpreterRegistryService owner) {
                _owner = owner;
                _e = owner._providers
                    .Select(GetFactoryProvider)
                    .SelectMany(GetFactories)
                    .Where(fact => fact != null)
                    .OrderBy(fact => fact.Configuration.Description)
                    .ThenBy(fact => fact.Configuration.Version);
            }

            private IPythonInterpreterFactoryProvider GetFactoryProvider(Lazy<IPythonInterpreterFactoryProvider, Dictionary<string, object>> lazy) {
                try {
                    return lazy.Value;
                } catch (CompositionException) {
                    return null;
                }
            }

            public IEnumerator<IPythonInterpreterFactory> GetEnumerator() {
                return new InterpretersEnumerator(_owner, _e.GetEnumerator());
            }

            IEnumerator IEnumerable.GetEnumerator() {
                return new InterpretersEnumerator(_owner, _e.GetEnumerator());
            }
        }

        private void OnInterpretersChanged() {
            try {
                BeginSuppressInterpretersChangedEvent();
                for (bool repeat = true; repeat; repeat = _raiseInterpretersChanged, _raiseInterpretersChanged = false) {
                    _interpretersChanged?.Invoke(this, EventArgs.Empty);
                }
            } finally {
                EndSuppressInterpretersChangedEvent();
            }
        }


        public void Dispose() {
            lock (_locksLock) {
                if (_locks != null) {
                    foreach (var dict in _locks.Values) {
                        foreach (var li in dict.Values) {
                            li.Dispose();
                        }
                    }
                    _locks = null;
                }
            }

            foreach (var provider in _providers.OfType<IDisposable>()) {
                provider.Dispose();
            }
        }

        // Used for testing.
        internal Lazy<IPythonInterpreterFactoryProvider, Dictionary<string, object>>[] SetProviders(Lazy<IPythonInterpreterFactoryProvider, Dictionary<string, object>>[] providers) {
            var oldProviders = _providers;
            _providers = providers;
            foreach (var p in oldProviders) {
                IPythonInterpreterFactoryProvider provider;
                try {
                    provider = p.Value;
                } catch (CompositionException) {
                    continue;
                }
                provider.InterpreterFactoriesChanged -= Provider_InterpreterFactoriesChanged;
            }
            foreach (var p in providers) {
                IPythonInterpreterFactoryProvider provider;
                try {
                    provider = p.Value;
                } catch (CompositionException) {
                    continue;
                }
                provider.InterpreterFactoriesChanged += Provider_InterpreterFactoriesChanged;
            }
            Provider_InterpreterFactoriesChanged(this, EventArgs.Empty);
            return oldProviders;
        }

        private void Provider_InterpreterFactoriesChanged(object sender, EventArgs e) {
            lock (_suppressInterpretersChangedLock) {
                if (_suppressInterpretersChanged > 0) {
                    _raiseInterpretersChanged = true;
                    return;
                }
            }
        }

        sealed class LockInfo : IDisposable {
            public int _lockCount;
            public readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

            public void Dispose() {
                _lock.Dispose();
            }
        }
    }

    [Export(typeof(IInterpreterOptionsService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    sealed class InterpreterOptionsService : IInterpreterOptionsService {
        private readonly Lazy<IInterpreterRegistryService> _registryService;
        private bool _defaultInterpreterWatched;
        private string _defaultInterpreterId;
        IPythonInterpreterFactory _defaultInterpreter;
        private EventHandler _defaultInterpreterChanged;

        // The second is a static registry entry for the local machine and/or
        // the current user (HKCU takes precedence), intended for being set by
        // other installers.
        private const string FactoryProvidersRegKey = @"Software\Microsoft\PythonTools\" + AssemblyVersionInfo.VSVersion + @"\InterpreterFactories";
        private const string DefaultInterpreterOptionsCollection = @"SOFTWARE\\Microsoft\\PythonTools\\Interpreters";
        private const string DefaultInterpreterSetting = "DefaultInterpreter";
        private const string DefaultInterpreterVersionSetting = "DefaultInterpreterVersion";

        private const string PathKey = "ExecutablePath";
        private const string WindowsPathKey = "WindowedExecutablePath";
        private const string LibraryPathKey = "LibraryPath";
        private const string ArchitectureKey = "Architecture";
        private const string VersionKey = "SysVersion";
        private const string PathEnvVarKey = "PathEnvironmentVariable";
        private const string DescriptionKey = "Description";
        private const string PythonInterpreterKey = "SOFTWARE\\Python\\VisualStudio";

        [ImportingConstructor]
        public InterpreterOptionsService([Import]Lazy<IInterpreterRegistryService> registryService) {
            _registryService = registryService;
        }


        private void InitializeDefaultInterpreterWatcher() {
            _registryService.Value.InterpretersChanged += Provider_InterpreterFactoriesChanged;

            RegistryHive hive = RegistryHive.CurrentUser;
            RegistryView view = RegistryView.Default;
            if (RegistryWatcher.Instance.TryAdd(
                hive, view, DefaultInterpreterOptionsCollection,
                DefaultInterpreterRegistry_Changed,
                recursive: false, notifyValueChange: true, notifyKeyChange: false
            ) == null) {
                // DefaultInterpreterOptions subkey does not exist yet, so
                // create it and then start the watcher.
                SaveDefaultInterpreter(_defaultInterpreter?.Configuration?.Id);

                RegistryWatcher.Instance.Add(
                    hive, view, DefaultInterpreterOptionsCollection,
                    DefaultInterpreterRegistry_Changed,
                    recursive: false, notifyValueChange: true, notifyKeyChange: false
                );
            }
            _defaultInterpreterWatched = true;
        }

        private void Provider_InterpreterFactoriesChanged(object sender, EventArgs e) {
            // reload the default interpreter ID and see if it changed...
            string oldId = _defaultInterpreterId;
            _defaultInterpreterId = null;
            LoadDefaultInterpreterId();

            if (oldId != _defaultInterpreterId) {
                // it changed, invalidate the old interpreter ID.  If no one is watching then
                // we'll just load it on demand next time someone requests it.
                _defaultInterpreter = null;
                if (_defaultInterpreterWatched) {
                    // someone is watching it, so load it now and raise the changed event.
                    LoadDefaultInterpreter();
                }
            }
        }

        private void DefaultInterpreterRegistry_Changed(object sender, RegistryChangedEventArgs e) {
            try {
                LoadDefaultInterpreter();
            } catch (InvalidComObjectException) {
                // Race between VS closing and accessing the settings store.
            } catch (Exception ex) {
                try {
                    //ActivityLog.LogError(
                    //    "Python Tools for Visual Studio",
                    //    string.Format("Exception updating default interpreter: {0}", ex)
                    //);
                } catch (InvalidOperationException) {
                    // Can't get the activity log service either. This probably
                    // means we're being used from outside of VS, but also
                    // occurs during some unit tests. We want to debug this if
                    // possible, but generally avoid crashing.
                    Debug.Fail(ex.ToString());
                }
            }
        }

        private void LoadDefaultInterpreterId() {
            if (_defaultInterpreterId == null) {
                string id = null;
                using (var interpreterOptions = Registry.CurrentUser.OpenSubKey(DefaultInterpreterOptionsCollection)) {
                    if (interpreterOptions != null) {
                        id = interpreterOptions.GetValue(DefaultInterpreterSetting) as string ?? string.Empty;
                    }

                    var newDefault = _registryService.Value.FindInterpreter(id);

                    if (newDefault == null) {
                        var defaultConfig = _registryService.Value.Configurations.LastOrDefault(fact => fact.CanBeAutoDefault());
                        if (defaultConfig != null) {
                            id = defaultConfig.Id;
                        }
                    }

                    _defaultInterpreterId = id;
                }
            }
        }

        private void LoadDefaultInterpreter(bool suppressChangeEvent = false) {
            if (_defaultInterpreter == null) {
                LoadDefaultInterpreterId();
                if (_defaultInterpreterId != null) {
                    var newDefault = _registryService.Value.FindInterpreter(_defaultInterpreterId);

                    if (suppressChangeEvent) {
                        _defaultInterpreter = newDefault;
                    } else {
                        DefaultInterpreter = newDefault;
                    }
                }
            }
        }

        private void SaveDefaultInterpreter(string id) {
            using (var interpreterOptions = Registry.CurrentUser.CreateSubKey(DefaultInterpreterOptionsCollection, true)) {
                if (id == null) {
                    interpreterOptions.SetValue(DefaultInterpreterSetting, "");
                } else {
                    Debug.Assert(!InterpreterRegistryService.IsNoInterpretersFactory(id));

                    interpreterOptions.SetValue(DefaultInterpreterSetting, id);
                }
            }
        }

        public string DefaultInterpreterId {
            get {
                LoadDefaultInterpreterId();
                return _defaultInterpreterId;
            }
            set {
                _defaultInterpreterId = value;
                _defaultInterpreter = null; // cleared so we'll re-initialize if anyone cares about it.
                SaveDefaultInterpreter(value);

                _defaultInterpreterChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IPythonInterpreterFactory DefaultInterpreter {
            get {
                LoadDefaultInterpreter(true);
                return _defaultInterpreter ?? _registryService.Value.NoInterpretersValue;
            }
            set {
                var newDefault = value;
                if (_defaultInterpreter == null && (newDefault == _registryService.Value.NoInterpretersValue || value == null)) {
                    // we may have not loaded the default interpreter yet.  Do so 
                    // now so we know if we need to raise the change event.
                    LoadDefaultInterpreter();
                }

                if (newDefault == _registryService.Value.NoInterpretersValue) {
                    newDefault = null;
                }
                if (newDefault != _defaultInterpreter) {
                    _defaultInterpreter = newDefault;
                    SaveDefaultInterpreter(_defaultInterpreter?.Configuration?.Id);

                    _defaultInterpreterChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler DefaultInterpreterChanged {
            add {
                if (!_defaultInterpreterWatched) {
                    InitializeDefaultInterpreterWatcher();
                }
                _defaultInterpreterChanged += value;
            }
            remove {
                _defaultInterpreterChanged -= value;
            }
        }

        public string AddConfigurableInterpreter(InterpreterFactoryCreationOptions options) {
            var collection = PythonInterpreterKey + "\\" + options.Id;
            using (var key = Registry.CurrentUser.CreateSubKey(collection, true)) {
                key.SetValue(LibraryPathKey, options.LibraryPath ?? string.Empty);
                key.SetValue(ArchitectureKey, options.ArchitectureString);
                key.SetValue(VersionKey, options.LanguageVersionString);
                key.SetValue(PathEnvVarKey, options.PathEnvironmentVariableName ?? string.Empty);
                key.SetValue(DescriptionKey, options.Description ?? string.Empty);
                using (var installPath = key.CreateSubKey("InstallPath")) {
                    key.SetValue("", Path.GetDirectoryName(options.InterpreterPath ?? options.WindowInterpreterPath ?? ""));
                    key.SetValue(WindowsPathKey, options.WindowInterpreterPath ?? string.Empty);
                    key.SetValue(PathKey, options.InterpreterPath ?? string.Empty);
                }
            }

            return CPythonInterpreterFactoryConstants.GetIntepreterId(
                "VisualStudio",
                options.Architecture,
                options.Id
            );
        }

        public void RemoveConfigurableInterpreter(string id) {
            var collection = PythonInterpreterKey + "\\" + id;
            Registry.CurrentUser.DeleteSubKeyTree(collection);
        }

        public bool IsConfigurable(string id) {
            return id.StartsWith("Global;VisualStudio;");
        }
    }
}
