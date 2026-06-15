// Cooperates with the installer's "open SimHub visibly after an install/update"
// trick.
//
// Background: SimHub honors a user "Start minimized" preference
// (GlobalSimhubSettings.json -> StartMinimized). Someone who just ran our
// installer wants to SEE SimHub, not have it drop straight into the system
// tray, so the installer flips StartMinimized to false for that one
// post-install launch (PrepareVisibleLaunch.ps1) and drops a one-shot marker
// file for us.
//
// The catch: SimHub loads StartMinimized into its in-memory MainModel once at
// startup and writes that value back to disk on every clean exit
// (MainWindow.Shutdown -> SaveSettings). If we did nothing, the installer's
// temporary "false" would be persisted on the next exit and the user's
// preference would be silently turned off for good.
//
// So on startup, when the marker is present, we set SimHub's in-memory
// MainModel.StartMinimized back to true AFTER the window's start-minimized
// decision has already been made (so it has no visible effect this launch).
// SimHub then re-persists the user's real preference on exit. The on-disk
// "false" only affects the single post-install launch.
//
// SimHub's MainWindow / MainModel aren't reference-able types for a plugin, so
// we reach them by reflection. Everything here is best-effort: any failure
// just leaves SimHub behaving as it did before (worst case the user's "Start
// minimized" reverts to off, which they can re-enable). See the installer's
// PrepareVisibleLaunch.ps1 and TrueforceForAll.iss.

using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    internal static class StartMinimizedRestore
    {
        // Dropped by the installer (PrepareVisibleLaunch.ps1) under the plugin's
        // PluginsData\Common\TrueforceForAll root when it flips StartMinimized
        // off for a post-install launch. One-shot: we delete it once consumed.
        private const string MarkerFileName = "restore-startminimized.flag";

        public static void ConsumeMarkerAndRestore()
        {
            try
            {
                string markerPath = MarkerPath();
                if (string.IsNullOrEmpty(markerPath) || !File.Exists(markerPath)) return;

                var app = Application.Current;
                if (app == null)
                {
                    // No WPF application yet (not expected under SimHub). Leave the
                    // marker in place so a later launch can retry rather than
                    // dropping it on the floor.
                    SimHub.Logging.Current.Warn(
                        "[Trueforce] StartMinimized restore deferred: no WPF application available yet.");
                    return;
                }

                // Consume now: even if the restore below can't reach SimHub's
                // model, we must not re-run on every future launch.
                try { File.Delete(markerPath); } catch { }

                SimHub.Logging.Current.Info(
                    "[Trueforce] Opened SimHub visibly after install/update; restoring your 'Start minimized' preference.");

                ScheduleInMemoryRestore(app);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[Trueforce] StartMinimized restore skipped: " + ex.Message);
            }
        }

        private static string MarkerPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (baseDir.Length == 0) return "";
            return Path.Combine(baseDir, "PluginsData", "Common",
                                BuiltinPresets.RootFolderName, MarkerFileName);
        }

        private static void ScheduleInMemoryRestore(Application app)
        {
            // Defer to application-idle. SimHub copies StartMinimized from disk
            // into MainModel and makes its start-minimized decision early in
            // MainWindow init; running after idle guarantees the window/model
            // exist and that decision is already made, so writing the value back
            // changes only what SimHub saves, never the current window state.
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    Window mainWin = FindMainWindow(app);
                    if (mainWin == null)
                    {
                        SimHub.Logging.Current.Warn(
                            "[Trueforce] StartMinimized restore: SimHub main window not found.");
                        return;
                    }

                    object model = GetProperty(mainWin, "Model");
                    if (model == null)
                    {
                        SimHub.Logging.Current.Warn(
                            "[Trueforce] StartMinimized restore: SimHub model not reachable.");
                        return;
                    }

                    var prop = model.GetType().GetProperty("StartMinimized",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop == null || !prop.CanWrite)
                    {
                        SimHub.Logging.Current.Warn(
                            "[Trueforce] StartMinimized restore: StartMinimized not settable on this SimHub build.");
                        return;
                    }

                    prop.SetValue(model, true, null);
                    SimHub.Logging.Current.Info(
                        "[Trueforce] 'Start minimized' preference restored; SimHub will keep it on exit.");
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "[Trueforce] StartMinimized restore (deferred) failed: " + ex.Message);
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        private static Window FindMainWindow(Application app)
        {
            // Match by type name so we don't depend on Application.MainWindow
            // being assigned, but fall back to it if the search comes up empty.
            foreach (Window w in app.Windows)
            {
                if (w != null && w.GetType().FullName == "SimHubWPF.MainWindow")
                    return w;
            }
            return app.MainWindow;
        }

        private static object GetProperty(object target, string name)
        {
            var p = target.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p != null ? p.GetValue(target, null) : null;
        }
    }
}
