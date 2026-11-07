// Watches the language folder so a translator's saved <tag>.json is picked
// up within a second, no SimHub restart. Root files only (the shipped\ copies
// are ours and rewritten at start), *.json only, one debounced Reload per
// burst of events, and one retry when the editor still holds the file.
//
// Everything is best effort: a watcher that cannot start logs once and the
// plugin keeps the strings it loaded at Init. Reload runs on the WPF
// dispatcher when there is one, because the store's PropertyChanged feeds
// live bindings.
//
// Design and phases: docs/localization-plan.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;

namespace TrueforceForAll.Plugin.Localization
{
    internal sealed class LocWatcher : IDisposable
    {
        private const int DebounceMs = 300;
        private const int LockedRetryMs = 200;

        private readonly string _root;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher _watcher;
        private Timer _timer;
        private bool _retried;
        private bool _failureLogged;
        private volatile bool _disposed;

        /// <param name="languagesRoot">The folder to watch; created when missing.</param>
        /// <param name="log">Receives already-prefixed warning text.</param>
        public LocWatcher(string languagesRoot, Action<string> log)
        {
            _root = languagesRoot;
            _log = log;
            try
            {
                Directory.CreateDirectory(languagesRoot);
                _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
                var watcher = new FileSystemWatcher(languagesRoot, "*.json")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                                 | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex)
            {
                FailOnce("could not start watching " + languagesRoot, ex);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                var watcher = _watcher;
                _watcher = null;
                if (watcher != null)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
            }
            catch { }
            try
            {
                var timer = _timer;
                _timer = null;
                timer?.Dispose();
            }
            catch { }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            Schedule(e.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Schedule(e.FullPath);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            FailOnce("stopped watching " + _root, e.GetException());
        }

        // Editors save in bursts (truncate, write, rename); one Reload per
        // burst is plenty, so every event just pushes the timer out again.
        private void Schedule(string path)
        {
            if (_disposed) return;
            try
            {
                lock (_gate)
                {
                    if (path != null) _pending.Add(path);
                    _retried = false;
                    // Dispose can land between the guard above and here.
                    if (_disposed) return;
                    _timer?.Change(DebounceMs, Timeout.Infinite);
                }
            }
            catch (ObjectDisposedException)
            {
                // A file event that raced shutdown; nothing to report.
            }
            catch (Exception ex)
            {
                FailOnce("could not schedule a reload", ex);
            }
        }

        private void OnTimer(object state)
        {
            if (_disposed) return;
            try
            {
                List<string> paths;
                lock (_gate)
                {
                    paths = new List<string>(_pending);
                }

                if (AnyLocked(paths))
                {
                    bool retry;
                    lock (_gate)
                    {
                        retry = !_retried;
                        _retried = true;
                    }
                    if (retry)
                    {
                        // Dispose can land while the lock check above ran.
                        if (_disposed) return;
                        _timer?.Change(LockedRetryMs, Timeout.Infinite);
                        return;
                    }
                    // Still locked after the retry: reload anyway. The store
                    // logs the file it could not read and keeps the others.
                }

                lock (_gate)
                {
                    _pending.Clear();
                    _retried = false;
                }
                Marshal(ReloadStore);
            }
            catch (ObjectDisposedException)
            {
                // The timer fired as the plugin shut down; nothing to report.
            }
            catch (Exception ex)
            {
                FailOnce("reload scheduling failed", ex);
            }
        }

        private static bool AnyLocked(List<string> paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                }
                catch (IOException)
                {
                    return true;
                }
                catch
                {
                    // Access denied and friends: not a lock we can wait out.
                }
            }
            return false;
        }

        private void ReloadStore()
        {
            if (_disposed) return;
            try
            {
                Loc.Instance?.Reload();
            }
            catch (Exception ex)
            {
                FailOnce("reload failed", ex);
            }
        }

        private static void Marshal(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            dispatcher.BeginInvoke(action);
        }

        private void FailOnce(string what, Exception ex)
        {
            lock (_gate)
            {
                if (_failureLogged) return;
                _failureLogged = true;
            }
            try
            {
                _log?.Invoke("[TF4ALL] Language folder watcher " + what + ": "
                    + (ex?.Message ?? "unknown error") + ". Edits to language files need a SimHub restart until then.");
            }
            catch { }
        }
    }
}
