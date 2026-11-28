// Field test: switch an Xbox-mode G923 (046D:C26D) into PC mode (046D:C26E)
// with G HUB installed but closed, and record what G HUB sends when it does it.
//
// Background. The Xbox G923 sometimes powers up in Xbox mode, where it exposes
// no HID++ or Trueforce interface, so the plugin cannot drive it. G HUB
// switches it to C26E. The Linux recipe (usb_modeswitch -v 046d -p c26d
// -M 0f00010142 -m 01) writes one Xbox accessory (GIP) frame to the wheel's
// OUT endpoint 0x01: command 0x0F, flags 0x00, sequence 0x01, length 0x01,
// payload 0x42.
//
// Why WinUSB. The first version of this tool went through Windows' Xbox
// accessory driver (Windows.Gaming.Input.Custom). A field log (2026-10-01)
// showed that on a machine with G HUB installed the C26D is not on that driver
// at all: it is on WinUSB under G HUB's own device class (LGHUBWinUSB), which
// stays bound with G HUB closed. WinUSB lets a normal program open the device
// and write an endpoint directly, so this sends the Linux frame as-is.
//
// Two modes:
//   (default)  send the switch over WinUSB, then watch for C26E. On failure,
//              offer the capture.
//   capture    with the wheel in Xbox mode, record all USB traffic with USBPcap
//              while the tester opens G HUB, stop once C26E arrives, and zip the
//              captures plus the log to the Desktop. Runs without administrator
//              rights when USBPcap allows it (as the plugin does); only if no
//              recording can start does it ask Windows for them, in a new window.
// Other arguments: list (report and exit), log=<path> and elevated (internal,
// used when the tool relaunches itself as administrator). Developer checks for
// a machine with no G923: probe=XXXX finds and opens 046D:XXXX through the
// WinUSB path code (sends nothing), capturetest records 5 s and zips it,
// skipping the G HUB and wheel checks.
// The log is written to the Desktop (or next to the exe, then %TEMP%).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;

namespace TrueforceForAll.Tools.XboxG923ModeSwitch
{
    internal static class Program
    {
        internal const ushort LogitechVid = 0x046D;
        internal const ushort XboxModePid = 0xC26D;
        internal const ushort PcModePid = 0xC26E;

        private static StreamWriter _log;
        private static readonly object LogGate = new object();
        internal static string LogPath;

        private static int Main(string[] args)
        {
            bool listOnly = Has(args, "list");
            bool capture = Has(args, "capture");
            bool elevatedChild = Has(args, "elevated");
            bool captureTest = Has(args, "capturetest");
            string probe = args.FirstOrDefault(a => a.StartsWith("probe=", StringComparison.OrdinalIgnoreCase))?.Substring(6);
            string logArg = args.FirstOrDefault(a => a.StartsWith("log=", StringComparison.OrdinalIgnoreCase))?.Substring(4);

            OpenLog(logArg);
            if (elevatedChild)
            {
                Log("");
                Log("---- continuing as administrator ----");
            }

            int code;
            bool waitForEnter = true;
            try
            {
                if (probe != null) code = WinUsbSwitch.Probe(probe) ? 0 : 1;
                else if (captureTest) code = GHubCapture.Run(dryRun: true);
                else if (capture) code = RunCapture(ref waitForEnter);
                else
                {
                    code = RunSwitch(listOnly);
                    if (code != 0 && code != 10 && !listOnly && OfferCapture())
                        code = RunCapture(ref waitForEnter);
                }
            }
            catch (Exception ex)
            {
                Log("UNEXPECTED ERROR: " + ex);
                code = 99;
            }

            if (waitForEnter)
            {
                Log("");
                Log("Done. Log file: " + (LogPath ?? "(console only)"));
            }
            _log?.Dispose();

            if (waitForEnter && !Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }
            return code;
        }

        // ---- default mode ----

        // Exit codes: 0 switched (or already PC mode / list), 10 no wheel,
        // 2 not openable, 3 write refused, 6 sent but no C26E.
        private static int RunSwitch(bool listOnly)
        {
            Log("Xbox G923 mode switch test (WinUSB), " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Log("Windows build " + WindowsBuild() + ", " + (Environment.Is64BitProcess ? "64" : "32")
                + "-bit process, administrator: " + (IsAdmin() ? "yes" : "no"));
            Log("Mode: " + (listOnly ? "list only, nothing is sent" : "send the switch over WinUSB"));
            Log("");
            ReportProcesses();
            Log("");

            Log("Step 1: what Windows has for the wheel");
            var before = ReportPnp();
            Log("");
            if (listOnly) return 0;

            var xbox = before.FirstOrDefault(d => d.Pid == XboxModePid);
            if (xbox == null)
            {
                if (before.Any(d => d.Pid == PcModePid))
                {
                    Log("RESULT: the wheel is already in PC mode (C26E). Nothing to switch.");
                    Log("Run this again after a start where the wheel comes up as C26D.");
                    return 0;
                }
                Log("RESULT: no G923 found. Check the wheel is plugged in and powered.");
                return 10;
            }
            if (!string.Equals(xbox.Service, "WINUSB", StringComparison.OrdinalIgnoreCase))
                Log($"  Note: the C26D is on driver '{xbox.Service ?? "(none)"}', not WinUSB. Trying anyway.");

            Log("Step 2: open the wheel through WinUSB and send the switch");
            var sent = WinUsbSwitch.Send(xbox.Id);
            Log("");
            if (sent == WinUsbSwitch.Outcome.NotOpened)
            {
                Log("RESULT: could not open the wheel through WinUSB, so nothing was sent.");
                return 2;
            }
            if (sent == WinUsbSwitch.Outcome.WriteRefused)
            {
                Log("RESULT: the wheel opened but refused the switch message.");
                return 3;
            }

            Log("Step 3: watch for the wheel to come back as C26E (up to 20 s)");
            if (WatchForPcMode(TimeSpan.FromSeconds(20)))
            {
                Log("");
                ReportPnp();
                Log("");
                Log("RESULT: SUCCESS. The wheel switched to PC mode with no G HUB.");
                return 0;
            }
            Log("");
            ReportPnp();
            Log("");
            Log("RESULT: the switch was sent, but the wheel did not come back as C26E.");
            return 6;
        }

        /// <summary>Poll until C26E is present and C26D is gone. Logs the
        /// moments each happens, with local time, so they can be lined up
        /// against a capture.</summary>
        internal static bool WatchForPcMode(TimeSpan limit)
        {
            var sw = Stopwatch.StartNew();
            bool sawXboxLeave = false;
            while (sw.Elapsed < limit)
            {
                System.Threading.Thread.Sleep(500);
                var now = QueryPnp();
                bool xbox = now.Any(d => d.Pid == XboxModePid);
                bool pc = now.Any(d => d.Pid == PcModePid);
                if (!xbox && !sawXboxLeave)
                {
                    sawXboxLeave = true;
                    Log($"  {Stamp()} ({sw.Elapsed.TotalSeconds:0.0} s): C26D is gone.");
                }
                if (pc && !xbox)
                {
                    Log($"  {Stamp()} ({sw.Elapsed.TotalSeconds:0.0} s): C26E has arrived.");
                    return true;
                }
            }
            if (!sawXboxLeave) Log("  The wheel stayed in Xbox mode (C26D) the whole time.");
            else Log("  The wheel dropped off but never came back as C26E.");
            return false;
        }

        private static bool OfferCapture()
        {
            if (Console.IsInputRedirected) return false;
            Log("");
            Log("The next step is to record what G HUB sends when it switches the wheel.");
            Log("Type C and press Enter to start the recording, or just press Enter to stop here.");
            Console.Write("> ");
            string answer = Console.ReadLine() ?? "";
            Log("  Answer: " + (answer.Trim().Length == 0 ? "(none)" : answer.Trim()));
            return answer.Trim().Equals("c", StringComparison.OrdinalIgnoreCase);
        }

        private static int RunCapture(ref bool waitForEnter)
        {
            int code = GHubCapture.Run();
            if (code == GHubCapture.NoRecording && !IsAdmin())
            {
                code = RelaunchElevated("capture");
                waitForEnter = code != 0;
            }
            return code;
        }

        // ---- elevation ----

        internal static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        /// <summary>Run this exe again as administrator, appending to the same
        /// log, and wait for it. Returns 0 when the elevated copy ran (it
        /// reports its own result in its own window).</summary>
        private static int RelaunchElevated(string mode)
        {
            Log("");
            Log("Recording USB traffic needs administrator rights on this PC. Windows will ask for permission;");
            Log("the test then continues in a new window.");
            _log?.Flush();
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo(exe, $"{mode} elevated \"log={LogPath}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
                // The child appends to the same file, so let go of it first.
                lock (LogGate) { _log?.Dispose(); _log = null; }
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit();
                    Console.WriteLine("The administrator window has finished. This window can be closed.");
                }
                return 0;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                OpenLog(LogPath);
                Log("Permission was declined, so nothing was recorded.");
                return 7;
            }
            catch (Exception ex)
            {
                OpenLog(LogPath);
                Log("Could not start as administrator: " + Describe(ex));
                return 7;
            }
        }

        // ---- diagnostics ----

        internal sealed class PnpDevice
        {
            public ushort Pid;
            public string Name, Id, Service, Status, PnpClass;
            public int ErrorCode;
        }

        internal static List<PnpDevice> QueryPnp()
        {
            var list = new List<PnpDevice>();
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID, Service, Status, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity "
                    + "WHERE PNPDeviceID LIKE '%VID_046D&PID_C26D%' OR PNPDeviceID LIKE '%VID_046D&PID_C26E%'"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string id = o["PNPDeviceID"] as string ?? "";
                        list.Add(new PnpDevice
                        {
                            Pid = id.IndexOf("PID_C26E", StringComparison.OrdinalIgnoreCase) >= 0 ? PcModePid : XboxModePid,
                            Name = o["Name"] as string,
                            Id = id,
                            Service = o["Service"] as string,
                            Status = o["Status"] as string,
                            PnpClass = o["PNPClass"] as string,
                            ErrorCode = o["ConfigManagerErrorCode"] is uint e ? (int)e : -1,
                        });
                    }
                }
            }
            catch (Exception ex) { Log("  Device query failed: " + Describe(ex)); }
            return list;
        }

        internal static List<PnpDevice> ReportPnp()
        {
            var list = QueryPnp();
            if (list.Count == 0)
            {
                Log("  No 046D:C26D or 046D:C26E device is present.");
                return list;
            }
            foreach (var d in list.OrderBy(d => d.Id))
                Log($"  {(d.Pid == XboxModePid ? "C26D Xbox mode" : "C26E PC mode  ")} | {d.Name} | driver={d.Service ?? "(none)"} "
                    + $"| class={d.PnpClass ?? "?"} | status={d.Status ?? "?"} code={d.ErrorCode} | {d.Id}");
            return list;
        }

        internal static readonly string[] GHubProcessNames = { "lghub", "lghub_agent", "lghub_updater", "lghub_system_tray" };

        internal static List<string> RunningGHub() =>
            GHubProcessNames.Where(n => { try { return Process.GetProcessesByName(n).Length > 0; } catch { return false; } }).ToList();

        internal static void ReportProcesses()
        {
            // lghub_updater is a background service that keeps running after
            // G HUB is quit; only the others mean the app is open.
            var ghub = RunningGHub();
            var app = ghub.Where(n => n != "lghub_updater").ToList();
            if (app.Count == 0)
                Log("G HUB: closed" + (ghub.Count > 0 ? " (only its background updater is running, which is fine)." : "."));
            else
                Log("G HUB: OPEN (" + string.Join(", ", app) + "). Quit it from its tray icon first, "
                    + "or it may hold the wheel or switch it itself.");
            bool simhub;
            try { simhub = Process.GetProcessesByName("SimHubWPF").Length > 0; } catch { simhub = false; }
            if (simhub) Log("SimHub: running. Close it for this test.");
        }

        private static string WindowsBuild()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return $"{k?.GetValue("CurrentBuild")}.{k?.GetValue("UBR")}";
            }
            catch { return "?"; }
        }

        internal static string Describe(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Trim()}";

        internal static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

        private static bool Has(string[] args, string flag) =>
            args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        // ---- log ----

        private static void OpenLog(string path)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(path)) candidates.Add(path);
            string name = "XboxG923ModeSwitch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                         AppDomain.CurrentDomain.BaseDirectory, Path.GetTempPath() })
                if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, name));

            foreach (var p in candidates)
            {
                try
                {
                    var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _log = new StreamWriter(fs) { AutoFlush = true };
                    LogPath = p;
                    return;
                }
                catch { }
            }
        }

        internal static void Log(string line)
        {
            lock (LogGate)
            {
                Console.WriteLine(line);
                try { _log?.WriteLine(line); } catch { }
            }
        }
    }
}
