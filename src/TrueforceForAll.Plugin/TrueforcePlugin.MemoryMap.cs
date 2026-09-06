// The plugin half of the Memory Mapping tab.
//
// What this is for. An emulated arcade cabinet publishes force feedback and
// nothing else: no revs, no speed, no gear. memscan is a read-only memory
// scanner that finds where the game keeps those values, so the map grows one
// confirmed row at a time and the effects that need real telemetry become
// possible on titles that never offered any.
//
// Why the plugin runs it rather than a console window. memscan counts the
// operator through a timed script (idle, full throttle, idle, three throttle
// blips on a count, idle). The operator is AT THE WHEEL, not at the keyboard,
// so a console on another monitor is the wrong place for those cues. This
// plugin already owns the wheel's screen and its rev lights, and that is where
// someone driving is actually looking.
//
// Read only, and that is a hard line: memscan never writes into another
// process, and nothing here asks it to.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    public sealed partial class TrueforcePlugin
    {
        // ---- locating memscan.exe -----------------------------------------
        //
        // memscan is NOT built by this solution: it is a 64-bit .NET 8 program
        // living beside the plugin, deployed by hand. So it is found the same
        // way TrueforceForAll.LoopbackHelper.exe is, from the assembly's own
        // folder, and when it is absent the tab says so with the paths it
        // looked in rather than throwing out of a click.

        private const string MemoryScanExeName = "memscan.exe";

        /// <summary>Every place the scanner is looked for, in order. Handed to
        /// the tab verbatim when none of them exist, because "not found" with
        /// no path is an unanswerable message.</summary>
        public IReadOnlyList<string> MemoryScanSearchPaths()
        {
            var list = new List<string>();
            try
            {
                string pluginDir = Path.GetDirectoryName(typeof(TrueforcePlugin).Assembly.Location);
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    list.Add(Path.Combine(pluginDir, MemoryScanExeName));
                    // A dotnet publish drops the exe in its own folder, and
                    // copying that folder whole is the easier deploy of the
                    // two, so both shapes are accepted.
                    list.Add(Path.Combine(pluginDir, "memscan", MemoryScanExeName));
                }
            }
            catch { }
            return list;
        }

        /// <summary>The scanner's path, or null when it is not deployed.</summary>
        public string MemoryScanExePath()
        {
            foreach (string p in MemoryScanSearchPaths())
            {
                try { if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        // ---- which cabinet, and its pid ------------------------------------

        /// <summary>A process worth scanning: what to pass memscan and what to
        /// call it on screen.</summary>
        public sealed class ScanTarget
        {
            public string ProcessName;   // without .exe, as Process.ProcessName gives it
            public int    Pid;
            public string DisplayName;   // the cabinet as a person would say it
        }

        /// <summary>The arcade cabinet running right now, or null. Uses the
        /// detection the plugin already does for the arcade panel: the active
        /// game's own TeknoParrot profile first, then any configured cabinet
        /// whose executable is running, so a scan can be set up before SimHub
        /// has switched games.
        ///
        /// Deliberately reads ProcessName and Id only. Asking another process
        /// for its MainModule throws for most of the table on a 32-bit host,
        /// and SimHub logs a stack trace per first-chance throw; that cost this
        /// plugin tens of megabytes of log a minute once already.</summary>
        public ScanTarget DetectArcadeScanTarget()
        {
            try
            {
                // exe name -> display name. The active cabinet is added last so
                // it overwrites, then it is preferred outright below.
                //
                // Built from ONE ArcadeModTargets() sweep. That call parses every
                // TeknoParrot user profile XML off disk, and TeknoParrot ships
                // hundreds; calling it again through ActiveArcadeTarget() to
                // learn the same thing doubled the cost of a keystroke in the
                // process box. The active game is matched against this list
                // instead.
                var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string activeExe = null;
                string activeGame = _activeGame;
                try
                {
                    foreach (var t in ArcadeModTargets())
                    {
                        if (string.IsNullOrEmpty(t.ExeName)) continue;
                        byExe[t.ExeName] = t.DisplayName ?? t.ExeName;
                        if (!string.IsNullOrEmpty(activeGame)
                            && string.Equals(ArcadeGameName(t.ProfileName), activeGame, StringComparison.Ordinal))
                            activeExe = t.ExeName;
                    }
                }
                catch { }
                if (byExe.Count == 0) return null;

                ScanTarget best = null;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (!byExe.TryGetValue(name, out string display)) continue;
                        var hit = new ScanTarget { ProcessName = name, Pid = p.Id, DisplayName = display };
                        // The active game's own executable wins immediately;
                        // anything else is a fallback we keep looking past.
                        if (activeExe != null && string.Equals(name, activeExe, StringComparison.OrdinalIgnoreCase))
                            return hit;
                        if (best == null) best = hit;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>Resolve a typed process name to a running pid, for the
        /// manual fallback. Accepts "game" or "game.exe".</summary>
        public ScanTarget FindScanTargetByName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return null;
            string name = processName.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { return new ScanTarget { ProcessName = name, Pid = p.Id, DisplayName = name }; }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return null;
        }

        // ---- running the scan ----------------------------------------------

        private ScanHost _scanHost;

        /// <summary>Where the last scan was told to write. Kept so the tab can
        /// offer the results folder even when the run was killed before it got
        /// to say where it had put them.</summary>
        public string LastMemoryScanOutDir { get; private set; }

        /// <summary>A fresh results folder for one run, under the same
        /// PluginsData\Common root everything else the plugin writes lives in.
        ///
        /// This has to be passed explicitly. memscan's own default is
        /// ".\memscan-out", relative to its working directory, and its working
        /// directory here is the folder it was deployed into: the SimHub install
        /// under Program Files. Creating a folder there needs admin, so the run
        /// would die on its first line with an exit code and no explanation.</summary>
        private static string NewMemoryScanOutDir()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture);
            return Path.Combine(TfPaths.CommonRoot, BuiltinPresets.RootFolderName, "memscan", stamp);
        }

        /// <summary>One decoded memscan event. Raised on the scanner's reader
        /// thread: every subscriber marshals for itself.</summary>
        public event EventHandler<ScanEvent> MemoryScanEvent;

        /// <summary>The scanner exited, for any reason including Stop.</summary>
        public event EventHandler MemoryScanExited;

        public bool MemoryScanRunning => _scanHost != null && _scanHost.IsRunning;

        /// <summary>Start a scan. Returns null on success, or a short human
        /// reason. Never throws.</summary>
        public string StartMemoryScan(MemoryScanRequest req)
        {
            if (req == null) return "nothing to scan";
            if (MemoryScanRunning) return "a scan is already running";
            StopMemoryScan();

            string exe = MemoryScanExePath();
            if (exe == null)
                return "memscan.exe is not deployed beside the plugin";

            string outDir = NewMemoryScanOutDir();
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { return "the results folder could not be created (" + ex.Message + ")"; }

            // The command line itself is built in ScanHost.cs, out of reach of
            // every SimHub type, for one reason: the wire gate can compile that
            // file and drive the real scanner with it. Built in here it could
            // only ever be tested by a copy of itself living in the gate, and
            // that copy is exactly what let this half spend a round asking for a
            // flag the scanner has never had.
            string args = MemoryScanArgs.Build(req, outDir, MemoryWatch.Hz, MemoryWatch.Count,
                                               out string argError);
            if (args == null) return argError ?? "the scan could not be described";

            var host = new ScanHost(exe, m => SimHub.Logging.Current.Info(m));
            host.ScanEventReceived += (s, ev) =>
            {
                try { MemoryScanEvent?.Invoke(this, ev); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] scan event fanout failed: " + ex.Message); }
            };
            host.ScanExited += (s, e) =>
            {
                try { MemoryScanExited?.Invoke(this, EventArgs.Empty); } catch { }
            };
            string err = host.Start(args);
            if (err != null) { try { host.Dispose(); } catch { } return err; }
            // Before the host is visible, not after: a press can only be sent
            // once _scanHost is set, so resetting here cannot throw one away.
            ResetMemoryScanPressCounts();
            _scanHost = host;
            LastMemoryScanOutDir = outDir;
            SimHub.Logging.Current.Info($"[TF4ALL] memscan started ({exe} {args}).");
            // The watch list belongs to the plugin and outlives every run, so a
            // new scanner is handed the whole of it before anything else
            // happens. That is what lets the operator search, watch, go and
            // change the car, and come back to the same rows.
            ArmMemoryWatch();
            return null;
        }

        /// <summary>Kill the scanner. Safe to call when nothing is running, and
        /// the teardown path: plugin shutdown, and anywhere a partial run is
        /// not worth waiting for. A Stop button should call
        /// StopMemoryScanPolitely instead, which keeps the run.</summary>
        public void StopMemoryScan()
        {
            var h = _scanHost;
            _scanHost = null;
            if (h == null) return;
            try { h.Dispose(); } catch { }
            SimHub.Logging.Current.Info("[TF4ALL] memscan stopped.");
        }

        /// <summary>Stop the scanner by asking first: send abort, give it about
        /// three seconds to write its recording, its CSVs and its verdict, and
        /// only kill it if it does not take the offer.
        ///
        /// Returns immediately. The wait happens on a worker thread, because a
        /// three second block on the UI thread is a frozen SimHub, and the
        /// callback says which of the two actually happened; it is raised on
        /// that worker thread, so a UI caller marshals for itself.</summary>
        public void StopMemoryScanPolitely(Action<bool, string> done)
        {
            var h = _scanHost;
            _scanHost = null;
            if (h == null)
            {
                try { done?.Invoke(false, "Nothing was running."); } catch { }
                return;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool clean = false;
                string detail;
                try { clean = h.AbortAndStop(3000, out detail); }
                catch (Exception ex) { detail = "The scanner could not be stopped cleanly: " + ex.Message; }
                try { h.Dispose(); } catch { }
                SimHub.Logging.Current.Info("[TF4ALL] memscan stopped. " + detail);
                try { done?.Invoke(clean, detail); } catch { }
            });
        }

        // ---- the reverse channel -------------------------------------------
        //
        // What the operator knows and the scanner cannot: the gear they are in,
        // the shift they just made, the moment the race started. Ground truth
        // read off the screen is the strongest evidence in this tool, and the
        // bound buttons are what make it cost nothing to give.
        //
        // Every one of these is a no-op with a logged reason when no scan is
        // running, so a stray press outside a run is harmless.
        //
        // Every one of them is also COUNTED, running or not. A gear hunt now
        // derives the whole sequence from these presses, so a hunt that quietly
        // receives none is the likeliest way the feature fails, and it fails
        // invisibly: the operator is at the wheel, pulling a paddle that is not
        // bound or that is bound while nothing is listening, and the tab would
        // otherwise sit there looking busy. Counted on the input thread, so the
        // counters are interlocked and the tab only ever reads them.

        private int _memShiftUpSent;
        private int _memShiftDownSent;
        private int _memMarkSent;
        private int _memPressesDropped;

        /// <summary>Shift presses sent to the scanner since the run started.</summary>
        public int MemoryScanShiftUpSent   => System.Threading.Volatile.Read(ref _memShiftUpSent);
        public int MemoryScanShiftDownSent => System.Threading.Volatile.Read(ref _memShiftDownSent);
        /// <summary>Marks sent since the run started.</summary>
        public int MemoryScanMarksSent     => System.Threading.Volatile.Read(ref _memMarkSent);
        /// <summary>Presses that arrived with no scan running, since the run
        /// started. Not an error, and worth showing: it is the difference
        /// between "your buttons are not bound" and "your buttons are bound and
        /// you are pressing them at the wrong time".</summary>
        public int MemoryScanPressesDropped => System.Threading.Volatile.Read(ref _memPressesDropped);

        /// <summary>Forget the press counts. Called when a run starts, so the
        /// tally the tab shows belongs to the run in front of the operator and
        /// not to the last three.</summary>
        private void ResetMemoryScanPressCounts()
        {
            System.Threading.Interlocked.Exchange(ref _memShiftUpSent, 0);
            System.Threading.Interlocked.Exchange(ref _memShiftDownSent, 0);
            System.Threading.Interlocked.Exchange(ref _memMarkSent, 0);
            System.Threading.Interlocked.Exchange(ref _memPressesDropped, 0);
            MemoryScanLastMark = null;
        }

        /// <summary>A gearshift just happened: "up" or "down".</summary>
        public string SendMemoryScanShift(string dir)
        {
            var h = _scanHost;
            if (h == null)
            {
                System.Threading.Interlocked.Increment(ref _memPressesDropped);
                SimHub.Logging.Current.Info("[TF4ALL] memscan shift ignored: no scan is running.");
                return "no scan is running";
            }
            string err = h.SendShift(dir);
            if (err == null)
            {
                if (string.Equals(dir, "down", StringComparison.OrdinalIgnoreCase))
                    System.Threading.Interlocked.Increment(ref _memShiftDownSent);
                else
                    System.Threading.Interlocked.Increment(ref _memShiftUpSent);
            }
            return err;
        }

        /// <summary>The name the bound mark button sends. Chosen on the tab
        /// before the run, from the session's own list (lap, stop, car-change,
        /// crash, drift) or left as "note". Read on SimHub's input thread and
        /// written on the UI thread; a string reference swap is atomic, and a
        /// mark that lands one press before or after a change of name is the
        /// operator's own timing rather than a race worth locking for.</summary>
        public string MemoryScanMarkName
        {
            get { return _memMarkName ?? MemoryMarks.DefaultName; }
            set { _memMarkName = MemoryMarks.Normalize(value); }
        }
        private string _memMarkName = MemoryMarks.DefaultName;

        /// <summary>The name of the last mark that went down the wire, for the
        /// tab's session line and the wheel.</summary>
        public string MemoryScanLastMark { get; private set; }

        /// <summary>Mark the current tick: race-start, lap, stop, car-change,
        /// crash, drift, or a note.
        ///
        /// Every mark carries its lookback window, worked out from the name:
        /// the tap is the END of the window, and the analysis looks back from it
        /// for the change the mark is about. The scanner may know better and
        /// widen it; it is never sent a window of zero, which would ask it to
        /// look for a change AT the tap, where the change has already happened.</summary>
        public string SendMemoryScanMark(string name)
        {
            var h = _scanHost;
            if (h == null)
            {
                System.Threading.Interlocked.Increment(ref _memPressesDropped);
                SimHub.Logging.Current.Info("[TF4ALL] memscan mark ignored: no scan is running.");
                return "no scan is running";
            }
            string n = MemoryMarks.Normalize(name);
            string err = h.SendMark(n, MemoryMarks.LookbackSecsFor(n));
            if (err == null)
            {
                System.Threading.Interlocked.Increment(ref _memMarkSent);
                MemoryScanLastMark = n;
            }
            return err;
        }

        /// <summary>Declare the gear the car is in right now.</summary>
        public string SendMemoryScanGear(int gear)
        {
            var h = _scanHost;
            if (h == null)
            {
                SimHub.Logging.Current.Info("[TF4ALL] memscan gear ignored: no scan is running.");
                return "no scan is running";
            }
            return h.SendGear(gear);
        }

        // ---- the watch list ------------------------------------------------
        //
        // Addresses the operator is watching live, with a name each. This is
        // the map, and it is the only thing here that answers "how do we know
        // the answer was right": you watch a row and then deliberately change
        // the thing it should be tracking. A pressure sensor that passed every
        // test as a tachometer does not follow the car name when you pick a
        // different car.
        //
        // Owned by the plugin rather than the tab because it has to outlive
        // both: a run ends and takes its scanner with it, and the settings
        // panel is created and destroyed as SimHub feels like it. It is saved
        // to disk for the same reason a module-relative row exists at all,
        // which is that a confirmed offset is still good after a relaunch.
        //
        // Nothing here is in TrueforceSettings, so there is no BackupProjection
        // line to add: this is a developer tool's scratch map for one machine's
        // copy of one game, not a setting worth carrying to another PC.

        private MemoryWatchList _memWatch;
        private string _memWatchSaveProblem;

        /// <summary>The saved watch list, loaded on first use.</summary>
        public MemoryWatchList MemoryWatch
        {
            get
            {
                if (_memWatch == null)
                {
                    _memWatch = new MemoryWatchList();
                    string why = _memWatch.Load(MemoryWatchFilePath());
                    if (why != null)
                        SimHub.Logging.Current.Info("[TF4ALL] the saved watch list could not be read: " + why);
                }
                return _memWatch;
            }
        }

        /// <summary>Beside the scan results, under the same root everything else
        /// the plugin writes lives in.</summary>
        private static string MemoryWatchFilePath()
            => Path.Combine(TfPaths.CommonRoot, BuiltinPresets.RootFolderName, "memscan", "watch.json");

        /// <summary>Why the last save failed, or null. Shown rather than
        /// swallowed: a map that quietly did not save is worse than no map.</summary>
        public string MemoryWatchSaveProblem => _memWatchSaveProblem;

        private void SaveMemoryWatch()
        {
            _memWatchSaveProblem = MemoryWatch.Save(MemoryWatchFilePath());
            if (_memWatchSaveProblem != null)
                SimHub.Logging.Current.Info("[TF4ALL] the watch list could not be saved: " + _memWatchSaveProblem);
        }

        /// <summary>Hand a freshly started scanner the whole list. Cheap, and it
        /// is the only way a watch survives from one run to the next: the
        /// scanner is a child process and each run gets a new one.</summary>
        public void ArmMemoryWatch()
        {
            // First, and before every early return below. A new scanner is
            // watching nothing whatever the last one held, and a stale record of
            // what is armed would make the incremental arm skip the very
            // candidates a fresh run is about to find.
            _memArmed.Clear();
            var h = _scanHost;
            if (h == null) return;
            var list = MemoryWatch;
            var store = MemoryFields;
            var arm = store.ArmList(out _);
            // Nothing at all when there is nothing to read. Not even a clear:
            // the scanner reads a watch command as "keep this run alive on the
            // rows afterwards", and a host that armed an empty list on every run
            // was telling every search to hold forever on nothing.
            //
            // The map counts as something to read. Its entries have to be
            // re-resolved and sanity checked in every launch, and its candidates
            // are what an elimination round judges, so a run with an empty watch
            // list and a map full of candidates still has work to do.
            if (list.Count == 0 && arm.Count == 0) return;
            // A new scanner has read none of these yet, so the first value each
            // row comes back with is the row arriving, not a change. Carrying
            // the last run's readings over would light half the map up and count
            // a change nobody made.
            list.ForgetReadings();
            store.ForgetReadings();
            try
            {
                // Cleared first so the scanner's list is exactly ours, whatever
                // it thought it was watching.
                h.SendWatchClear();
                h.SendWatchRate(list.Hz);
                foreach (var r in list.Rows)
                    h.SendWatch(r.Addr, r.Kind, r.Len, r.Label);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] the watch list could not be sent: " + ex.Message);
            }
            // After the clear, never before it, or the map's rows would be the
            // ones the clear threw away.
            ArmMemoryFields();
        }

        /// <summary>Add or correct one row. Returns null when it is in the list,
        /// or the reason it is not.</summary>
        public string AddMemoryWatchRow(string addr, string kind, int len, string label,
                                        out MemoryWatchRow row)
        {
            string problem = MemoryWatch.Add(addr, kind, len, label, out row, out _);
            if (problem != null) return problem;
            SaveMemoryWatch();
            // Sent whether it was new or a correction. Re-watching an address
            // the scanner already holds is the same instruction twice, which
            // costs one line and cannot lose a row.
            try { _scanHost?.SendWatch(row.Addr, row.Kind, row.Len, row.Label); } catch { }
            return null;
        }

        public bool RemoveMemoryWatchRow(string addr)
        {
            var row = MemoryWatch.Find(addr);
            if (row == null) return false;
            string exact = row.Addr;
            if (!MemoryWatch.Remove(exact)) return false;
            SaveMemoryWatch();
            // Not unwatched when the map still wants it read. The two lists
            // share one wire, and taking a row off the workbench must not
            // silently stop a candidate or a confirmed entry being read: that
            // looks exactly like a value that stopped moving, which is the one
            // reading this whole feature exists to make trustworthy.
            if (_memFields == null || !_memFields.Knows(exact))
                try { _scanHost?.SendUnwatch(exact); } catch { }
            return true;
        }

        public void ClearMemoryWatchRows()
        {
            MemoryWatch.Clear();
            SaveMemoryWatch();
            try { _scanHost?.SendWatchClear(); } catch { }
            // The clear is the whole wire, not just this list, so the map's own
            // rows go with it. Put back at once: clearing the workbench must not
            // silently stop the map being read, which would look exactly like a
            // set of candidates that had all stopped moving.
            ArmMemoryFields();
        }

        /// <summary>Rename a row. True when the name actually changed, so a
        /// caller can skip the save on a keystroke that did nothing.</summary>
        public bool RelabelMemoryWatchRow(string addr, string label)
        {
            if (!MemoryWatch.Relabel(addr, label)) return false;
            SaveMemoryWatch();
            var row = MemoryWatch.Find(addr);
            if (row != null)
                try { _scanHost?.SendWatch(row.Addr, row.Kind, row.Len, row.Label); } catch { }
            return true;
        }

        /// <summary>Refreshes per second for the whole list.</summary>
        public int MemoryWatchHz
        {
            get { return MemoryWatch.Hz; }
            set
            {
                var list = MemoryWatch;
                if (list.Hz == value) return;
                list.Hz = value;
                SaveMemoryWatch();
                try { _scanHost?.SendWatchRate(list.Hz); } catch { }
            }
        }

        // ---- the map -------------------------------------------------------
        //
        // The field frame and the file behind it. The watch list above is the
        // workbench: addresses somebody is looking at right now. This is the
        // product: car name, redline, gear, revs, speed, lap time and time
        // remaining, each either empty, holding candidates, or confirmed at an
        // address with a name and a record of how it was confirmed.
        //
        // One file per game, keyed on the PROCESS rather than on the name SimHub
        // is showing, because a memory map describes an executable's layout and
        // not a title. The game name is written inside the file, where it can be
        // read without being a file name.
        //
        // Owned by the plugin for the same reasons the watch list is: it has to
        // outlive a run, which takes its scanner with it, and the settings panel,
        // which SimHub creates and destroys as it likes.
        //
        // Nothing here is in TrueforceSettings, so there is no BackupProjection
        // line to add: this is one machine's map of one copy of one game, not a
        // setting worth carrying to another PC.

        private MemoryFieldStore _memFields;
        private string _memFieldsKey;
        private string _memFieldsSaveProblem;

        /// <summary>The map for whichever game the tab is pointed at, loaded on
        /// first use.</summary>
        public MemoryFieldStore MemoryFields
        {
            get
            {
                if (_memFields == null) LoadMemoryFields(_memFieldsKey, null);
                return _memFields;
            }
        }

        /// <summary>Which map is open, as a file name stem. Shown on the tab so
        /// it is never a mystery which game's map is being edited.</summary>
        public string MemoryFieldsKey => _memFieldsKey;

        public string MemoryFieldsSaveProblem => _memFieldsSaveProblem;

        private static string MemoryFieldsFilePath(string key)
            => Path.Combine(TfPaths.CommonRoot, BuiltinPresets.RootFolderName, "memscan", "maps",
                            MemoryFieldStore.FileNameFor(key));

        /// <summary>The map file this key opens, so the tab can show where the
        /// map actually lives rather than describing it.</summary>
        public string MemoryFieldsPath(string key) => MemoryFieldsFilePath(key);

        /// <summary>Open the map for one process, saving whatever was open
        /// first. Returns null, or why the file could not be read: a map that
        /// failed to load in silence would look exactly like a game nobody has
        /// mapped yet.
        ///
        /// Every entry comes back marked "not checked", never "good". It becomes
        /// live only once this launch has read it and the value made sense for
        /// its field, which is the one rule that stops a map quietly feeding a
        /// wrong number onward after a game update.</summary>
        public string LoadMemoryFields(string processKey, string gameName)
        {
            string key = string.IsNullOrWhiteSpace(processKey) ? "unknown" : processKey.Trim();
            if (_memFields != null && string.Equals(_memFieldsKey, key, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(gameName)) _memFields.Game = gameName;
                return null;
            }
            // The outgoing map is written only if it holds anything. The tab
            // touches this before a target has been detected, so the first thing
            // that happens on every launch is an "unknown" map being opened and
            // put down again; saving that would leave an empty unknown.json in
            // the folder on a machine that had never mapped anything.
            if (_memFields != null && MemoryFieldsWorthSaving(_memFields)) SaveMemoryFields();

            _memFields = new MemoryFieldStore();
            _memFieldsKey = key;
            string why = _memFields.Load(MemoryFieldsFilePath(key));
            _memFields.Process = key;
            if (!string.IsNullOrWhiteSpace(gameName)) _memFields.Game = gameName;
            if (why != null)
                // Not "could not be read". Load returns a note for a file it read
                // and partly dropped as well as for one it could not open, and a
                // log line that called the first case the second would send
                // anybody reading it after the wrong thing.
                SimHub.Logging.Current.Info("[TF4ALL] the memory map for " + key + ": " + why);
            return why;
        }

        /// <summary>A new launch of the game: every entry has to earn "live"
        /// again by resolving and reading plausibly in THIS process. Called when
        /// the pid the tab is pointed at changes.</summary>
        public void BeginMemoryFieldsLaunch() => MemoryFields.BeginLaunch();

        /// <summary>True when a map holds anything a person put there. A fresh
        /// store is seven empty catalog rows, which is not a map.</summary>
        private static bool MemoryFieldsWorthSaving(MemoryFieldStore store)
        {
            if (store == null) return false;
            foreach (var f in store.Fields)
                if (f.Entry != null || (f.Candidates != null && f.Candidates.Count > 0) || f.Custom)
                    return true;
            return false;
        }

        public void SaveMemoryFields()
        {
            if (_memFields == null) return;
            _memFieldsSaveProblem = _memFields.Save(MemoryFieldsFilePath(_memFieldsKey));
            if (_memFieldsSaveProblem != null)
                SimHub.Logging.Current.Info("[TF4ALL] the memory map could not be saved: " + _memFieldsSaveProblem);
        }

        /// <summary>Hand the running scanner every address the map wants read:
        /// each confirmed entry, so it can be re-resolved and sanity checked, and
        /// every candidate, so an elimination round has something to judge.
        ///
        /// Sent alongside the watch list rather than instead of it. The two are
        /// different jobs on the same wire: the watch list is what somebody is
        /// looking at, and this is what the map needs to stay honest.</summary>
        /// <summary>What the map currently has on the wire. Kept so a change to
        /// the map can be sent as the difference rather than as a clear and a
        /// rebuild: a clear takes the WATCH LIST down with it and throws away
        /// every value on screen, and the values on screen are the evidence the
        /// operator is in the middle of reading.</summary>
        private readonly HashSet<string> _memArmed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many of the scanner's watch rows the map may take.
        ///
        /// The scanner's list is ONE list with one ceiling, and the rows the
        /// operator curated by hand are on it too. Every row past that ceiling is
        /// refused, and a refused row reads from here exactly like a candidate
        /// that failed a round honestly, so the map is held to what is actually
        /// free rather than to a number that was never checked against the
        /// scanner.</summary>
        private int MemoryFieldArmBudget
        {
            get
            {
                int left = MemoryFieldStore.ScannerWatchRows - MemoryWatch.Count;
                return left < 0 ? 0 : left;
            }
        }

        public void ArmMemoryFields()
        {
            var h = _scanHost;
            _memArmed.Clear();
            if (h == null) return;
            var store = MemoryFields;
            var arm = store.ArmList(MemoryFieldArmBudget, out int dropped);
            if (arm.Count == 0) return;
            try
            {
                foreach (var c in arm)
                {
                    h.SendWatch(c.Addr, c.Kind, c.Len, c.Note);
                    _memArmed.Add(c.Addr);
                }
                // The rate has to be set even when the saved watch list is empty,
                // because the map's own rows are the only thing being read in
                // that case and a scanner with no rate set reads at whatever it
                // defaults to.
                h.SendWatchRate(MemoryWatch.Hz);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] the map's addresses could not be sent: " + ex.Message);
            }
            if (dropped > 0)
                SimHub.Logging.Current.Info("[TF4ALL] " + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " map address(es) were not sent: the scanner's watch list holds "
                    + MemoryFieldStore.ScannerWatchRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " rows at once and " + MemoryWatch.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " of them are the watch list's own.");
            MemoryFieldsNotArmed = dropped;
        }

        /// <summary>Map addresses that did not fit on the scanner's watch list.
        /// Read by the tab so the shortfall is a sentence on screen rather than a
        /// line in SimHub.txt: a candidate nobody is reading can never be ruled
        /// in or out, and it is indistinguishable from one that was.</summary>
        public int MemoryFieldsNotArmed { get; private set; }

        /// <summary>Put one new candidate on the wire straight away, while the
        /// run that found it is still alive.
        ///
        /// This is not an optimisation. A search emits its findings and then
        /// exits, so a candidate armed after the run has ended is armed at a
        /// process that is going away, and the operator would have to start a
        /// second scanner before they could rule anything out. Armed here, the
        /// scanner holds the run open on the rows afterwards, which is the same
        /// behaviour the watch list has always had: search, then go and change
        /// the car, with nothing to press in between.
        ///
        /// Returns false when the wire is full, which is said out loud rather
        /// than swallowed: an address nobody is reading cannot be eliminated
        /// either way.</summary>
        public bool ArmOneMemoryFieldCandidate(MemoryFieldCandidate candidate)
        {
            var h = _scanHost;
            if (h == null || candidate == null || string.IsNullOrWhiteSpace(candidate.Addr)) return false;
            if (_memArmed.Contains(candidate.Addr)) return true;
            if (_memArmed.Count >= MemoryFieldArmBudget) { MemoryFieldsNotArmed++; return false; }
            bool first = _memArmed.Count == 0;
            _memArmed.Add(candidate.Addr);
            try
            {
                h.SendWatch(candidate.Addr, candidate.Kind, candidate.Len, candidate.Note);
                // A run that started with nothing to watch never set a rate, and
                // this may be the first row it has ever been given.
                if (first) h.SendWatchRate(MemoryWatch.Hz);
            }
            catch { return false; }
            return true;
        }

        /// <summary>Send the map's changes as a difference: unwatch what it no
        /// longer wants, watch what it now does, and leave everything else
        /// exactly where it is.
        ///
        /// Used after an elimination round, an undo, a promotion and a demotion,
        /// which are all mid-session and all times when re-arming from scratch
        /// would clear the wire and wipe every value on screen. A round that
        /// ends by blanking the evidence it just produced is not a round anybody
        /// can check.</summary>
        public void ResyncMemoryFieldWatch()
        {
            var h = _scanHost;
            if (h == null) { _memArmed.Clear(); return; }

            var want = new Dictionary<string, MemoryFieldCandidate>(StringComparer.OrdinalIgnoreCase);
            int dropped;
            var wanted = MemoryFields.ArmList(MemoryFieldArmBudget, out dropped);
            foreach (var c in wanted)
                if (!string.IsNullOrWhiteSpace(c.Addr)) want[c.Addr] = c;
            MemoryFieldsNotArmed = dropped;

            // The hand-curated watch list owns its own rows. An address on both
            // stays on the wire when the map lets go of it.
            var onTheList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in MemoryWatch.Rows) onTheList.Add(r.Addr);

            try
            {
                foreach (string a in new List<string>(_memArmed))
                {
                    if (want.ContainsKey(a)) continue;
                    _memArmed.Remove(a);
                    if (!onTheList.Contains(a)) h.SendUnwatch(a);
                }
                bool first = _memArmed.Count == 0;
                foreach (var kv in want)
                {
                    if (!_memArmed.Add(kv.Key)) continue;
                    if (_memArmed.Count > MemoryFieldArmBudget)
                    { _memArmed.Remove(kv.Key); MemoryFieldsNotArmed++; continue; }
                    h.SendWatch(kv.Value.Addr, kv.Value.Kind, kv.Value.Len, kv.Value.Note);
                }
                if (first && _memArmed.Count > 0) h.SendWatchRate(MemoryWatch.Hz);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] the map's addresses could not be resynced: " + ex.Message);
            }
        }

        /// <summary>The pointer-path search settings this plugin asks for.
        ///
        /// A path search is its own RUN, not a command on the wire: the scanner
        /// refuses to combine one with a search, a survey, a script or a
        /// watch-only host, on the grounds that a search is how you get the
        /// address a path search starts from. So the tab starts a paths run the
        /// same way it starts every other one, and these are the numbers it
        /// starts it with.
        ///
        /// They are the ones the costing was done against: three dereferences
        /// and a 512 byte window, on a 32 bit target where pointers are four
        /// bytes and aligned in a 2 GB space, which is expected to give single
        /// digits to low hundreds of paths rather than thousands.
        ///
        /// A search, and only a search. It reads memory looking for four byte
        /// values that land near the target and walks back from them. It never
        /// writes, never injects and never pokes.</summary>
        public const int MemoryPathDepth = 3;
        public const int MemoryPathWindow = 512;
        public const int MemoryPathMax = 100;

        // ---- elimination rounds, on the wire --------------------------------
        //
        // The scanner measures a round as well as this plugin does, over EVERY
        // refresh rather than only the ones a settings panel happened to see,
        // and it reports which rows differed and which did not. It measures;
        // this side decides what the measurement means for a field.
        //
        // Sent for two reasons beyond the cross-check. The declaration lands in
        // the scanner's own report, which is where the evidence behind a map
        // entry has to be findable afterwards, and a round that this half
        // judged one way and the scanner judged the other is a bug in one of
        // them that would otherwise never be noticed.

        /// <summary>Tell the scanner a round has begun. "must" is "change" or
        /// "hold". Returns null when it went, or a short reason.
        ///
        /// THE ROWS ARE NAMED, and that is not a refinement. A round with no
        /// selector covers every row on the scanner's watch list, which is this
        /// field's candidates plus every other field's, plus every confirmed
        /// entry, plus whatever the operator has on the hand-curated watch list.
        /// This half judges one field. Comparing those two counts and calling a
        /// difference a bug reports almost every honest round as unproved, so the
        /// scanner is given the same set to measure.</summary>
        public string SendMemoryRoundStart(string name, string must, IEnumerable<string> rows)
        {
            var h = _scanHost;
            if (h == null) return "no scan is running";
            string selector = null;
            if (rows != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (string a in rows)
                {
                    if (string.IsNullOrWhiteSpace(a)) continue;
                    // A comma would cut the selector in half and name two rows
                    // that do not exist. No address form this plugin produces
                    // contains one, so a row that somehow does is left out of the
                    // selector rather than allowed to corrupt it.
                    if (a.IndexOf(',') >= 0) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(a.Trim());
                }
                selector = sb.Length > 0 ? sb.ToString() : null;
            }
            return h.SendRoundStart(name, must, selector);
        }

        /// <summary>End the open round and have the scanner report what
        /// moved.</summary>
        public string SendMemoryRoundEnd()
        {
            var h = _scanHost;
            if (h == null) return "no scan is running";
            return h.SendRoundEnd();
        }

        // ---- the cues, on the wheel ----------------------------------------
        //
        // The whole reason this lives in the plugin. The same readout path
        // DAMPCAL drives during its calibration: DashReadout sets a label and a
        // value that the OLED renderer picks up, in a running game and at idle
        // alike. The readout expires after about 1.6 s by design (it is normally
        // a "you just nudged the gain" flash), so a cue that must stay up for a
        // five-second hold is re-pushed by the caller roughly once a second.

        /// <summary>Whether a cue put on the wheel right now would actually be
        /// seen. False means the tab keeps the cues to itself and says so,
        /// rather than pretending the screen has them.</summary>
        public bool ScanCueScreenAvailable
        {
            get
            {
                try
                {
                    return _oledDash != null
                        && (Settings?.ModeBOledEnabled ?? false)
                        && OledWritesSafeNow;
                }
                catch { return false; }
            }
        }

        /// <summary>Put one scan cue on the wheel's screen. Two halves because
        /// the firmware draws the label and the value on separate rows in
        /// different sizes.</summary>
        public void ShowScanCue(string label, string value)
        {
            if (!ScanCueScreenAvailable) return;
            try { DashReadout(label ?? "MEMORY MAP", value ?? ""); } catch { }
        }

        /// <summary>Count the last seconds of a phase down on the rev lights:
        /// one LED per second left, falling to dark. The caller only calls this
        /// once the number fits the strip, so a long phase lights nothing until
        /// its end is close. Cheap by construction, one level write per second on
        /// a bar nothing else is driving during an arcade run, and it re-arms its
        /// own fade so a stopped scan leaves the strip dark on its own.</summary>
        public void ShowScanCountdownLights(int secondsLeft)
        {
            try
            {
                if (MasterMode != TrueforceMasterMode.Normal) return;
                if (_rpmLeds == null || !PreviewSweepAllowed) return;
                if (secondsLeft <= 0) return;
                EnsureLedChannelOpen();
                int level = Math.Max(0, Math.Min(secondsLeft, WheelLedChannel.LedCount));
                _rpmLeds.HoldLit(level, 1400);
            }
            catch { }
        }
    }
}
