// Record what G HUB sends when it switches an Xbox-mode G923 to PC mode.
//
// With the wheel in Xbox mode (C26D) and G HUB closed, this starts USBPcapCMD
// on every USB controller (whole bus, -A, so the wheel is caught whichever
// port it is on and again after it re-enumerates), asks the tester to open
// G HUB, and stops a few seconds after the C26E arrives. The captures and the
// log go into one zip on the Desktop. The log carries local timestamps for
// "G HUB opened", "C26D gone" and "C26E arrived" to line up against the pcap.
//
// USBPcap is already installed for anyone running the plugin, and records
// without administrator rights there (the plugin relies on that). If nothing
// can start, Program relaunches this mode as administrator.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using TrueforceForAll.Core;
using static TrueforceForAll.Tools.XboxG923ModeSwitch.Program;

namespace TrueforceForAll.Tools.XboxG923ModeSwitch
{
    internal static class GHubCapture
    {
        private static readonly string[] UsbPcapPaths =
        {
            @"C:\Program Files\USBPcap\USBPcapCMD.exe",
            @"C:\Program Files (x86)\USBPcap\USBPcapCMD.exe",
        };

        // The updater runs as a background service even with G HUB closed;
        // only these mean the app (or its tray icon) is open.
        private static readonly string[] GHubAppNames = { "lghub", "lghub_agent", "lghub_system_tray" };

        private sealed class Session
        {
            public string Interface;
            public string File;
            public Process Proc;
            public Thread Copier;
            public readonly List<string> Errors = new List<string>();
        }

        internal const int NoRecording = 22;

        // Exit codes: 0 recorded a switch, 20 no USBPcap, 21 wheel not in Xbox
        // mode, 22 (NoRecording) no capture started, 23 recorded but no switch seen.
        internal static int Run(bool dryRun = false)
        {
            Log("");
            Log("Recording what G HUB sends to switch the wheel, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            string cmd = UsbPcapPaths.FirstOrDefault(File.Exists);
            if (cmd == null)
            {
                Log("RESULT: USBPcap is not installed, so nothing can be recorded. It comes with the");
                Log("Trueforce For All installer.");
                return 20;
            }
            Log("USBPcap: " + cmd);

            if (!dryRun && !WaitForGHubClosed()) return 21;
            if (!dryRun && !WaitForXboxMode()) return 21;

            var interfaces = ListInterfaces(cmd);
            Log("USB controllers to record: " + (interfaces.Count == 0 ? "none found" : string.Join(", ", interfaces)));
            if (interfaces.Count == 0) return NoRecording;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folder = Path.Combine(Path.GetTempPath(), "XboxG923-GHub-capture-" + stamp);
            Directory.CreateDirectory(folder);

            var sessions = new List<Session>();
            foreach (var itf in interfaces)
            {
                var s = Start(cmd, itf, folder);
                if (s != null) sessions.Add(s);
            }
            Thread.Sleep(2000);
            foreach (var s in sessions)
            {
                bool alive = !SafeHasExited(s.Proc);
                Log($"  {s.Interface}: {(alive ? "recording" : "stopped straight away")}"
                    + (s.Errors.Count > 0 ? " | " + string.Join(" / ", s.Errors) : ""));
            }
            if (!sessions.Any(s => !SafeHasExited(s.Proc)))
            {
                Log("RESULT: no recording could start.");
                Stop(sessions);
                try { Directory.Delete(folder, true); } catch { }
                return NoRecording;
            }

            bool switched;
            if (dryRun)
            {
                Log("  Developer check: recording 5 s.");
                Thread.Sleep(5000);
                switched = false;
            }
            else
            {
                Log("");
                Log(">>> Now open G HUB and leave it open. This window will say when the wheel has switched. <<<");
                Log("    Don't type anything until then: the recording includes every USB device, keyboard too.");
                Log($"  {Stamp()}: recording started, waiting for G HUB (up to 3 minutes).");
                switched = WaitForSwitchNoting(TimeSpan.FromMinutes(3));
            }
            Thread.Sleep(3000);
            Stop(sessions);
            Log($"  {Stamp()}: recording stopped.");

            foreach (var s in sessions)
            {
                long size = 0;
                try { size = new FileInfo(s.File).Length; } catch { }
                Log($"  {Path.GetFileName(s.File)}: {size / 1024} KB");
            }

            Log("");
            Log(switched
                ? "RESULT: RECORDED. G HUB switched the wheel and the traffic was captured."
                : "RESULT: recorded, but the wheel never switched to C26E while recording.");
            Log("You can close G HUB now.");

            string zip = Path.Combine(Directory.Exists(desktop) ? desktop : Path.GetTempPath(),
                                      "XboxG923-GHub-capture-" + stamp + ".zip");
            try
            {
                if (LogPath != null) File.Copy(LogPath, Path.Combine(folder, Path.GetFileName(LogPath)), true);
                ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, false);
                Directory.Delete(folder, true);
                Log("");
                Log("Please send this file: " + zip);
            }
            catch (Exception ex)
            {
                Log("Could not make the zip (" + Describe(ex) + "). Please send the files in: " + folder);
            }
            return switched ? 0 : 23;
        }

        private static bool WaitForGHubClosed()
        {
            for (int i = 0; ; i++)
            {
                var open = GHubAppNames.Where(n => { try { return Process.GetProcessesByName(n).Length > 0; } catch { return false; } }).ToList();
                if (open.Count == 0) { Log("G HUB: closed."); return true; }
                Log("G HUB is open (" + string.Join(", ", open) + "). Quit it from its tray icon (right-click, Quit).");
                if (Console.IsInputRedirected || i >= 5) { Log("RESULT: G HUB stayed open, so nothing was recorded."); return false; }
                Log("Then press Enter.");
                Console.ReadLine();
            }
        }

        private static bool WaitForXboxMode()
        {
            for (int i = 0; ; i++)
            {
                var now = ReportPnp();
                if (now.Any(d => d.Pid == XboxModePid)) return true;
                if (now.Any(d => d.Pid == PcModePid))
                    Log("The wheel is in PC mode already, so there is no switch to record. With G HUB closed, "
                        + "unplug the wheel (USB and power), plug it back in,");
                else
                    Log("No G923 found. Plug the wheel in (USB and power),");
                if (Console.IsInputRedirected || i >= 5) { Log("RESULT: the wheel never showed up in Xbox mode."); return false; }
                Log("then press Enter.");
                Console.ReadLine();
            }
        }

        // WatchForPcMode, plus a note of when G HUB starts, so the log marks
        // the moment its traffic should begin.
        private static bool WaitForSwitchNoting(TimeSpan limit)
        {
            bool ghubSeen = false;
            var watcher = new Thread(() =>
            {
                while (!ghubSeen)
                {
                    try
                    {
                        if (GHubAppNames.Any(n => Process.GetProcessesByName(n).Length > 0))
                        {
                            ghubSeen = true;
                            Log($"  {Stamp()}: G HUB has started.");
                        }
                    }
                    catch { }
                    Thread.Sleep(250);
                }
            }) { IsBackground = true };
            watcher.Start();
            bool ok = WatchForPcMode(limit);
            ghubSeen = true;
            return ok;
        }

        private static List<string> ListInterfaces(string cmd)
        {
            var list = new List<string>();
            try
            {
                var psi = new ProcessStartInfo(cmd, "--extcap-interfaces")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (Match m in Regex.Matches(output, @"\{value=(\\\\\.\\USBPcap\d+)\}"))
                        list.Add(m.Groups[1].Value);
                }
            }
            catch (Exception ex) { Log("  Could not list USB controllers: " + Describe(ex)); }
            if (list.Count == 0)
                for (int i = 1; i <= 8; i++) list.Add($@"\\.\USBPcap{i}");
            return list;
        }

        private static Session Start(string cmd, string itf, string folder)
        {
            var s = new Session
            {
                Interface = itf,
                File = Path.Combine(folder, itf.Replace(@"\\.\", "") + ".pcap"),
            };
            try
            {
                var psi = new ProcessStartInfo(cmd, $"-d {itf} -A -o -")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                s.Proc = Process.Start(psi);
                ChildProcessJob.TryAssign(s.Proc);
                s.Proc.ErrorDataReceived += (o, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (s.Errors) s.Errors.Add(e.Data.Trim()); };
                s.Proc.BeginErrorReadLine();
                s.Copier = new Thread(() =>
                {
                    try
                    {
                        using (var fs = new FileStream(s.File, FileMode.Create, FileAccess.Write, FileShare.Read))
                            s.Proc.StandardOutput.BaseStream.CopyTo(fs);
                    }
                    catch { }
                }) { IsBackground = true };
                s.Copier.Start();
                return s;
            }
            catch (Exception ex)
            {
                Log($"  {itf}: could not start USBPcap: {Describe(ex)}");
                return null;
            }
        }

        private static void Stop(List<Session> sessions)
        {
            foreach (var s in sessions) { try { if (!s.Proc.HasExited) s.Proc.Kill(); } catch { } }
            foreach (var s in sessions) { try { s.Copier?.Join(5000); } catch { } }
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }
    }
}
