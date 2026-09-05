// Manages the lifetime of memscan.exe, the read-only memory scanner that
// learns where a game keeps its live values (revs first, then the rest of the
// map).
//
// Why a child process at all, and not a reference: SimHub runs 32-bit on .NET
// Framework 4.8 and memscan is a 64-bit .NET 8 program that must read a 64-bit
// game's address space. A 32-bit host cannot load it, and cannot read those
// addresses either. So the split is forced, not chosen, and this class is
// modelled directly on HelperHost, which manages the audio loopback child for
// exactly the same reason.
//
// Communication:
//   - memscan is spawned with --events, so its STDOUT carries one JSON object
//     per line and every human-readable line goes to STDERR instead.
//   - A background thread reads stdout line by line, parses each line and
//     raises ScanEventReceived. Nothing here touches the UI: the caller
//     marshals.
//   - STDERR is logged, which is what makes a failed run readable in
//     SimHub.txt without asking the operator to run the scanner by hand.
//   - The REVERSE CHANNEL runs the same shape the other way: one JSON object
//     per line, UTF-8, on memscan's STDIN, flushed per line. It carries the
//     operator's own ground truth, which is stronger evidence than anything
//     behaviour can prove:
//         {"cmd":"shift","dir":"up"}      a gearshift just happened
//         {"cmd":"mark","name":"race-start"}
//         {"cmd":"gear","value":3}        the current gear, declared
//         {"cmd":"abort"}                 stop cleanly, keeping the run
//     Every command is echoed back as an {"ev":"ack"} line, so the host knows
//     it landed and on which tick.
//   - The WATCH LIST rides the same wire, and it is the thing that turns a
//     verdict into something anyone can check. The host names addresses; the
//     scanner reads them and reports all of them together, one line per
//     refresh:
//         {"cmd":"watch","addr":"0x01C02750","kind":"int32","label":"redline?"}
//         {"cmd":"watch","addr":"game.exe+0x4A2C10","kind":"utf16","len":32,"label":"car name"}
//         {"cmd":"unwatch","addr":"0x01C02750"}
//         {"cmd":"watch-clear"}
//         {"cmd":"watch-rate","hz":5}
//     ONE line per refresh carrying EVERY row, not one line per row, so the
//     host can update in place instead of rebuilding a list twenty times a
//     second. A row that cannot be read keeps its place with ok:false and a
//     reason, because a value that stops being readable is itself information:
//     the region was freed, or the car was unloaded.
//
// Writes go through a queue and a writer thread of their own. A shift arrives
// on SimHub's input thread and Stop arrives on the UI thread, and neither may
// sit on a pipe that a busy child has not drained yet; the queue also keeps
// the commands in the order they were pressed, which a thread-pool write would
// not.
//
// Forward compatibility is a protocol rule, not politeness: a line whose "ev"
// value this build does not know is IGNORED, so the scanner half can add event
// kinds without this half needing a matching release. The same rule binds the
// other way: memscan ignores a "cmd" it does not know, so this half can add
// command kinds without waiting for a matching scanner.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>One watched address, as of one refresh. Values arrive as
    /// STRINGS on purpose: the scanner has already decoded the bytes with the
    /// kind it was told, so this half never has to guess a numeric format back
    /// out of a double.</summary>
    public sealed class ScanWatchRow
    {
        /// <summary>The address exactly as the host asked for it, which is what
        /// identifies the row and what an unwatch names.</summary>
        public string Addr;
        /// <summary>Where that landed this launch. Null when it could not be
        /// resolved, which for a module-relative row means the module is not
        /// loaded.</summary>
        public string Resolved;
        public string Kind;
        /// <summary>The decoded value, or null when the read failed.</summary>
        public string Value;
        /// <summary>False when the read failed. The row still has a place: see
        /// <see cref="Why"/>.</summary>
        public bool Ok;
        /// <summary>Why the read failed, in the scanner's words.</summary>
        public string Why;
    }

    /// <summary>One decoded line of memscan's event stream. Every field is
    /// optional: which ones are filled depends on <see cref="Ev"/>, and a
    /// reader must not assume any of them are present.</summary>
    public sealed class ScanEvent
    {
        public string Ev;          // stage | ceiling | phase | countdown | prompt | predicate | verdict | finding | ack | watch | done

        // stage
        public string Name;        // seed | narrow | record | rank, or the phase name
        public string Detail;      // free text for a status line, or a predicate's detail

        // ceiling / verdict / finding
        public string Level;       // CONFIRMED | CONTESTED | NOT_FOUND | UNPROVEN
        public string Why;
        public string Headline;
        public string OneLine;

        // phase
        public int Index;
        public int Of;
        public string Cue;         // what the driver is being asked to do, right now
        public double Seconds;

        // countdown / prompt
        public int SecondsLeft;
        public int N;

        // predicate
        public string Id;          // T1..T6
        public string Result;      // PASS | FAIL | NOT-RUN

        // finding
        public string Address;
        public string Type;
        public string Label;
        public string Confidence;
        public string Evidence;
        /// <summary>The kind a watch command should use for this address, in
        /// the scanner's own words. Authoritative: it knows which encoding
        /// actually matched, which the Type column only describes.</summary>
        public string WatchKind;
        /// <summary>The length that was matched, in CHARACTERS for utf16 and
        /// bytes otherwise. A floor rather than a target: it is the length of
        /// the text that was FOUND, so a longer replacement name would arrive
        /// truncated if it were used as written.</summary>
        public int WatchLen;
        /// <summary>The same address as "module+0xOFFSET", when it is inside a
        /// loaded image. THIS is what makes a finding a map entry: it resolves
        /// again after a relaunch, and a plain address does not.</summary>
        public string ModuleAddr;

        // survey: the census result. A survey names no address and earns no
        // verdict, so without these the one mode whose entire product is a
        // count has nothing to show for itself on this end.
        /// <summary>The tier the recording budget allowed, in the scanner's own
        /// words.</summary>
        public string Chosen;
        /// <summary>Candidates recorded as a full series.</summary>
        public long Recorded;
        /// <summary>Never-moving values carried as one census line each. These
        /// cost the recording nothing and are the reason a survey is run.</summary>
        public long Census;
        /// <summary>Never-moving values that did not fit the census cap.</summary>
        public long Dropped;
        /// <summary>The tier ladder as the scanner printed it, one line per row,
        /// so it can be shown as the table it is rather than re-derived.</summary>
        public List<string> Lines;

        // ack: a command this host sent came back, applied. AtTick is the
        // sample it landed on, which is what lets the analysis segment on it.
        public string Cmd;
        public int AtTick;

        // watch: every watched row, together, once per refresh. Null (not
        // empty) when the line carried no rows array at all, so "the scanner is
        // watching nothing" stays distinguishable from "this was not a watch
        // line".
        public int Tick;
        public double AtMs;
        public List<ScanWatchRow> Rows;

        // path: one route to an address through pointers, which is what turns a
        // heap address into a map entry. The values we can actually find on this
        // target are NOT in the executable image, so a bare address is good for
        // one launch; a path from a root that cannot move is good for every one.
        //
        // Every field here is optional and read defensively, because the scanner
        // half is written alongside this one: a path that arrives with only its
        // text is still usable, and one that arrives with only a root and its
        // offsets is composed back into text on this side.
        /// <summary>The whole path, in the notation every memory tool uses:
        /// "[[game.exe+0x4A2C10]+0x18]+0x4". The scanner calls this the SPEC and
        /// it is the string a watch row takes as written.
        ///
        /// Null on a path event whose answer is "there is none", which is a real
        /// answer rather than a failure and must not be shown as one.</summary>
        public string PathText;
        /// <summary>The absolute address this path was found to land on, this
        /// launch only. It is what the host asked about, so it is what a pending
        /// promotion is matched against.</summary>
        public string PathTarget;
        /// <summary>Where the walk starts, as "module+0xOFFSET" or an absolute
        /// address.</summary>
        public string PathRoot;
        /// <summary>The offsets, in walk order, when the scanner sent the parts
        /// as well as the text.</summary>
        public List<long> PathOffsets;
        /// <summary>How many dereferences the path takes. A shorter path is a
        /// stronger one.</summary>
        public int PathDepth;
        /// <summary>The widest offset the chain needed, which is the window the
        /// search had to allow. A tight window is stronger evidence than a wide
        /// one, so it is carried and shown rather than summarised away.</summary>
        public int PathWindow;
        /// <summary>The module the root lives in, when it is in one.</summary>
        public string PathModule;
        /// <summary>"main-exe", "dll" or "none". The game's own executable is
        /// the durable root on this target: an image that cannot rebase is at
        /// the same address every launch.</summary>
        public string PathRootKind;
        /// <summary>MEASURED from the module's PE header, not assumed: true means
        /// Windows cannot move that image.</summary>
        public bool PathStatic;
        /// <summary>The scanner's own ordering, 1 being the most durable path it
        /// found for this address. An ordering, never a confidence.</summary>
        public int PathRank;

        // path-check: one stored path, resolved and read against the game
        // running NOW. This is the relaunch test, and its vocabulary is wider
        // than pass or fail on purpose: a chain that did not resolve, one that
        // resolved onto an unreadable page, and one that read back something
        // that cannot be the field have three different fixes.
        /// <summary>BROKEN, UNREADABLE, IMPLAUSIBLE, OK or RESOLVED.</summary>
        public string Outcome;
        /// <summary>Where the chain landed this launch, when it resolved.</summary>
        public string Resolved;
        /// <summary>What it read back, when it read.</summary>
        public string PathValue;

        // round: the scanner's own measurement of one elimination round. The
        // tool measures, the host decides what the measurement means, so this
        // arrives as two lists of addresses rather than as a verdict.
        public string RoundName;
        /// <summary>"change" or "hold", the host's own declaration echoed back.</summary>
        public string RoundMust;
        public List<string> Changed;
        public List<string> Unchanged;
        public int ChangedCount;
        public int UnchangedCount;

        // done
        public int ExitCode;
        public string OutDir;

        /// <summary>The line as parsed, so a field added to the protocol later
        /// is still reachable without a change here.</summary>
        public JObject Raw;
    }

    public sealed class ScanHost : IDisposable
    {
        /// <summary>One decoded stdout line. Raised on the reader thread.</summary>
        public event EventHandler<ScanEvent> ScanEventReceived;

        /// <summary>The child exited, for any reason including Stop. Raised on
        /// the reader thread or on the process exit thread, whichever notices
        /// first, and only ever once.</summary>
        public event EventHandler ScanExited;

        private readonly string _exePath;
        private readonly Action<string> _log;
        private Process _child;
        private Thread _stdoutThread;
        private Thread _stderrThread;
        private volatile bool _stopping;
        private int _exitFired;

        // ---- the reverse channel's outbox ----------------------------------
        //
        // A caller enqueues and returns. One thread owns the pipe, so commands
        // reach the scanner in the order they were pressed and no caller ever
        // waits on a child that has not drained its end yet.
        //
        // The two wait handles are deliberately never disposed. The writer
        // thread is joined with a timeout, so it can still be inside a Wait or
        // a Set when the join gives up, and an ObjectDisposedException thrown
        // there is on a background thread with nobody to catch it, which on
        // this framework takes the whole of SimHub down. One handle pair per
        // scan run, collected with the object, is the cheaper mistake.
        private readonly object _txLock = new object();
        private readonly Queue<string> _txQueue = new Queue<string>();
        private readonly AutoResetEvent _txSignal = new AutoResetEvent(false);
        // Set only while the queue is empty AND the last line has been
        // flushed, which is what makes "the abort is actually on the wire" a
        // question with an answer.
        private readonly ManualResetEventSlim _txIdle = new ManualResetEventSlim(true);
        private StreamWriter _stdin;
        private Thread _stdinThread;
        private volatile bool _txClosed;

        /// <summary>The last command line handed to the scanner, for the tab's
        /// own status line. Not an acknowledgement: that arrives back as an
        /// "ack" event, which is the only proof it was applied.</summary>
        public string LastCommandSent { get; private set; }

        public string ExePath => _exePath;

        /// <summary>The child's process id, or 0 when nothing is running. Worth
        /// having in the log: a watch host lives for minutes with the operator
        /// away from the machine, and "which process is that" is the first
        /// question anyone asks about it.</summary>
        public int ChildPid
        {
            get
            {
                var c = _child;
                try { return c == null ? 0 : c.Id; }
                catch { return 0; }
            }
        }

        public bool IsRunning
        {
            get
            {
                var c = _child;
                try { return c != null && !c.HasExited; }
                catch { return false; }
            }
        }

        public ScanHost(string exePath, Action<string> log = null)
        {
            _exePath = exePath;
            _log = log;
        }

        /// <summary>Launch memscan with the given arguments. Returns null on
        /// success, or a short human reason. Never throws: a missing exe or a
        /// refused launch is something the tab says out loud, not a crash out
        /// of a click handler.</summary>
        public string Start(string arguments)
        {
            if (_child != null) return "a scan is already running";
            if (string.IsNullOrEmpty(_exePath) || !File.Exists(_exePath))
                return "memscan.exe was not found at " + (_exePath ?? "(no path)");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName  = _exePath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty,
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding  = new UTF8Encoding(false),
                };
                _stopping = false;
                _exitFired = 0;
                _txClosed = false;
                LastCommandSent = null;
                lock (_txLock) { _txQueue.Clear(); _txIdle.Set(); }
                _child = Process.Start(psi);
                // Our own writer over the raw pipe, deliberately, and not the
                // Process.StandardInput this framework hands out. On .NET
                // Framework that writer takes the console's input encoding or
                // the machine's ANSI codepage, and there is no
                // StandardInputEncoding to set as there is on .NET 8. The wire
                // is UTF-8 in both directions or it is not a wire.
                _stdin = new StreamWriter(_child.StandardInput.BaseStream, new UTF8Encoding(false), 512)
                {
                    AutoFlush = false,
                };
            }
            catch (Exception ex)
            {
                _child = null;
                _stdin = null;
                return ex.Message;
            }

            // Kill-on-close job: the scanner dies with SimHub even on a
            // taskkill /F, the same orphan protection the loopback helper and
            // the USBPcap capture children get. A scanner left reading another
            // process after SimHub is gone is invisible and unkillable from
            // here, which is the worst of the failure modes.
            TrueforceForAll.Core.ChildProcessJob.TryAssign(_child, _log);

            // Order matters, exactly as in HelperHost: subscribe before
            // EnableRaisingEvents so a child that has already died dispatches
            // synchronously, then a HasExited check covers the window between
            // Process.Start returning and the flag being set.
            _child.Exited += (_, __) => RaiseExitedOnce();
            _child.EnableRaisingEvents = true;
            try { if (_child.HasExited) RaiseExitedOnce(); } catch { }

            _stdoutThread = new Thread(StdoutPumpLoop)
            {
                IsBackground = true,
                Name = "TrueforceScanStdout",
            };
            _stdoutThread.Start();

            _stderrThread = new Thread(StderrLogLoop)
            {
                IsBackground = true,
                Name = "TrueforceScanStderr",
            };
            _stderrThread.Start();

            _stdinThread = new Thread(StdinPumpLoop)
            {
                IsBackground = true,
                Name = "TrueforceScanStdin",
            };
            _stdinThread.Start();
            return null;
        }

        // ---- the reverse channel -------------------------------------------

        /// <summary>A gearshift just happened, direction known. "up" or
        /// "down". Returns null when it was queued, or a short reason.</summary>
        public string SendShift(string dir)
        {
            string d = (dir ?? "").Trim().ToLowerInvariant();
            if (d != "up" && d != "down") return "a shift is either up or down";
            var o = new JObject { ["cmd"] = "shift", ["dir"] = d };
            return SendCommand(o);
        }

        /// <summary>An operator or detector mark on the current tick:
        /// race-start, race-end, or note.</summary>
        public string SendMark(string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) n = "note";
            var o = new JObject { ["cmd"] = "mark", ["name"] = n };
            return SendCommand(o);
        }

        /// <summary>The gear the operator says they are in RIGHT NOW. Ground
        /// truth read off the screen, which is stronger than anything the
        /// behaviour of a value can prove about it.</summary>
        public string SendGear(int value)
        {
            var o = new JObject { ["cmd"] = "gear", ["value"] = value };
            return SendCommand(o);
        }

        /// <summary>Stop, but keep the run: the scanner writes the recording,
        /// the CSVs and the summary, emits its verdict and findings, and then
        /// exits on its own.</summary>
        public string SendAbort() => SendCommand(new JObject { ["cmd"] = "abort" });

        // ---- the watch list ------------------------------------------------
        //
        // A read, never a poke. The scanner reports these addresses back on
        // every refresh and nothing here can write into the target, which is a
        // hard line for this project rather than a detail of this feature.

        /// <summary>Watch one address. "addr" is either absolute hex or
        /// "module+0xOFFSET"; the second survives a relaunch and is what makes a
        /// row a map entry. "len" is sent only for the string and byte kinds,
        /// in characters for utf16 and bytes otherwise.</summary>
        public string SendWatch(string addr, string kind, int len, string label)
        {
            string a = (addr ?? "").Trim();
            if (a.Length == 0) return "a watch needs an address";
            string k = (kind ?? "").Trim();
            if (k.Length == 0) return "a watch needs a kind";
            var o = new JObject { ["cmd"] = "watch", ["addr"] = a, ["kind"] = k };
            if (len > 0) o["len"] = len;
            if (!string.IsNullOrEmpty(label)) o["label"] = label;
            return SendCommand(o);
        }

        /// <summary>Stop watching one address. It is named exactly as it was
        /// given, which is why the host stores that form rather than a
        /// prettier one.</summary>
        public string SendUnwatch(string addr)
        {
            string a = (addr ?? "").Trim();
            if (a.Length == 0) return "an unwatch needs an address";
            return SendCommand(new JObject { ["cmd"] = "unwatch", ["addr"] = a });
        }

        /// <summary>Forget every watched address.</summary>
        public string SendWatchClear() => SendCommand(new JObject { ["cmd"] = "watch-clear" });

        /// <summary>Declare the start of an elimination round: the operator
        /// has said what they are about to do and has not done it yet.
        ///
        /// The scanner measures the round as well as this host, over EVERY
        /// refresh rather than only the ones a settings panel happened to see,
        /// and reports which rows differed and which did not. It measures; this
        /// side decides what the measurement means for a field. Sending the
        /// declaration also puts it in the scanner's own report, which is where
        /// the evidence for a map entry has to be able to be found afterwards.
        ///
        /// "must" is "change" or "hold" and nothing else: a word the scanner does
        /// not know is REFUSED rather than ignored, because a round reported with
        /// no declaration reads as "you never said".</summary>
        public string SendRoundStart(string name, string must, string rows)
        {
            string m = (must ?? "").Trim().ToLowerInvariant();
            if (m != "change" && m != "hold") return "a round is either change or hold";
            var o = new JObject { ["cmd"] = "round-start", ["must"] = m };
            string n = (name ?? "").Trim();
            if (n.Length > 0) o["name"] = n;
            if (!string.IsNullOrWhiteSpace(rows)) o["rows"] = rows.Trim();
            return SendCommand(o);
        }

        /// <summary>End the open round and have the scanner report what moved.</summary>
        public string SendRoundEnd() => SendCommand(new JObject { ["cmd"] = "round-end" });

        /// <summary>Refreshes per second for the whole watch list.</summary>
        public string SendWatchRate(int hz)
        {
            if (hz < 1) return "a watch rate is at least 1 per second";
            return SendCommand(new JObject { ["cmd"] = "watch-rate", ["hz"] = hz });
        }

        /// <summary>Queue one command line. Never blocks: the caller may be
        /// the UI thread or SimHub's input dispatch, and neither can afford to
        /// sit on a pipe. Returns null when it was queued, or the reason it was
        /// not, which is also logged so a press outside a run is explainable
        /// afterwards rather than merely silent.</summary>
        public string SendCommand(JObject command)
        {
            if (command == null) return "nothing to send";
            var w = _stdin;
            if (w == null || !IsRunning || _txClosed)
            {
                string why = "no scan is running, so the command was dropped: "
                           + command.ToString(Newtonsoft.Json.Formatting.None);
                _log?.Invoke("[TF4ALL] " + why);
                return "no scan is running";
            }
            string line;
            try { line = command.ToString(Newtonsoft.Json.Formatting.None); }
            catch (Exception ex) { return ex.Message; }

            lock (_txLock)
            {
                _txQueue.Enqueue(line);
                _txIdle.Reset();
            }
            _txSignal.Set();
            LastCommandSent = line;
            return null;
        }

        /// <summary>Wait until everything queued has been written and flushed.
        /// Only the abort path needs this, and only so the wait for a clean
        /// exit starts after the abort is actually on the wire.</summary>
        public bool WaitForCommandsWritten(int millis)
        {
            try { return _txIdle.Wait(Math.Max(0, millis)); }
            catch { return false; }
        }

        private void StdinPumpLoop()
        {
            while (true)
            {
                string line = null;
                lock (_txLock)
                {
                    if (_txQueue.Count > 0) line = _txQueue.Dequeue();
                    else _txIdle.Set();
                }
                if (line == null)
                {
                    if (_txClosed) return;
                    // A timeout as well as the signal: a wakeup lost to the
                    // close race would otherwise park this thread for good.
                    try { _txSignal.WaitOne(200); } catch { _txClosed = true; return; }
                    continue;
                }
                var w = _stdin;
                // The pump is about to give up, either way below. Say so first,
                // or SendCommand keeps queueing onto a queue nothing drains and
                // every press after this one reports success while going
                // nowhere. IsRunning catches it once the child has been OBSERVED
                // to exit, which is later than the pipe breaking.
                if (w == null) { _txClosed = true; lock (_txLock) { _txQueue.Clear(); _txIdle.Set(); } return; }
                try
                {
                    // '\n' rather than WriteLine: Environment.NewLine would put
                    // a carriage return on the wire that the far side has to
                    // remember to trim.
                    w.Write(line);
                    w.Write('\n');
                    w.Flush();
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                        _log?.Invoke("[TF4ALL] scan command write failed: " + ex.Message);
                    _txClosed = true;
                    lock (_txLock) { _txQueue.Clear(); _txIdle.Set(); }
                    return;
                }
            }
        }

        private void CloseStdin()
        {
            _txClosed = true;
            try { _txSignal.Set(); } catch { }
            try { _stdinThread?.Join(300); } catch { }
            _stdinThread = null;
            var w = _stdin;
            _stdin = null;
            // Closing our writer closes the pipe, which is the EOF on stdin
            // memscan sees. Dispose covers both halves.
            try { w?.Dispose(); } catch { }
            lock (_txLock) { _txQueue.Clear(); _txIdle.Set(); }
        }

        /// <summary>Stop the scan the hard way, promptly. Closing stdin gives
        /// memscan a moment to end on its own, but only a moment: this is the
        /// teardown path, called from Dispose and from plugin shutdown, and it
        /// must not sit on its caller for seconds.
        ///
        /// It is NOT what a Stop button should call. Killing a run throws away
        /// everything it had, and a run stopped at 80 percent is still worth
        /// analysing: that is what AbortAndStop is for.</summary>
        public void Stop()
        {
            _stopping = true;
            var c = _child;
            _child = null;
            CloseStdin();
            if (c == null) return;
            try { c.WaitForExit(300); } catch { }
            try { if (!c.HasExited) c.Kill(); } catch { }
            try { c.WaitForExit(700); } catch { }
            try { c.Dispose(); } catch { }
            RaiseExitedOnce();
        }

        /// <summary>Ask the scanner to stop cleanly, give it a moment, and only
        /// kill it if it does not take the offer. Returns true when it ended on
        /// its own, which is the difference between a run that wrote its
        /// recording, its CSVs and its verdict and one that wrote nothing.
        ///
        /// Blocks for up to graceMs, so it belongs on a worker thread and not
        /// on a click handler. Nothing is torn down before the wait: the stdout
        /// pump has to stay live through it or the verdict and findings the
        /// abort was asked for would be dropped on the floor.</summary>
        public bool AbortAndStop(int graceMs, out string detail)
        {
            var c = _child;
            if (c == null) { detail = "Nothing was running."; return false; }
            bool already = false;
            try { already = c.HasExited; } catch { }
            if (already)
            {
                detail = "The scanner had already finished.";
                Stop();
                return true;
            }

            string sendErr = SendAbort();
            bool exited = false;
            if (sendErr == null)
            {
                // The wait for the exit starts once the abort is on the wire,
                // not when it was queued.
                WaitForCommandsWritten(500);
                try { exited = c.WaitForExit(Math.Max(0, graceMs)); } catch { }
                // WaitForExit(int) says nothing about the output pipe being
                // drained, and the events worth having are the LAST ones the
                // run writes. Let the reader finish before Stop sets the flag
                // that breaks it.
                if (exited) { try { _stdoutThread?.Join(1500); } catch { } }
            }

            double secs = Math.Max(0, graceMs) / 1000.0;
            detail = sendErr != null
                ? "The scanner could not be told to stop (" + sendErr + "), so it was stopped the hard way and this run has no verdict."
                : exited
                    ? "Asked the scanner to stop; it wrote what it had and exited on its own."
                    : "The scanner did not answer within "
                      + secs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                      + " seconds, so it was stopped the hard way.";
            Stop();
            return sendErr == null && exited;
        }

        public void Dispose() => Stop();

        private void RaiseExitedOnce()
        {
            if (Interlocked.Exchange(ref _exitFired, 1) != 0) return;
            try { ScanExited?.Invoke(this, EventArgs.Empty); } catch { }
        }

        // ---------- pump loops ----------

        private void StdoutPumpLoop()
        {
            var child = _child;
            if (child == null) return;
            try
            {
                string line;
                while ((line = child.StandardOutput.ReadLine()) != null)
                {
                    if (_stopping) break;
                    var ev = Parse(line);
                    if (ev == null) continue;
                    try { ScanEventReceived?.Invoke(this, ev); }
                    catch (Exception ex) { _log?.Invoke("[TF4ALL] scan event handler failed: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) _log?.Invoke("[TF4ALL] scan stdout read error: " + ex.Message);
            }
            finally { RaiseExitedOnce(); }
        }

        private void StderrLogLoop()
        {
            var child = _child;
            if (child == null) return;
            try
            {
                string line;
                while ((line = child.StandardError.ReadLine()) != null)
                    _log?.Invoke("[memscan] " + line);
            }
            catch { }
        }

        /// <summary>One line to one event, or null when the line is not an
        /// event we can use. Unknown "ev" values, blank lines and anything that
        /// is not a JSON object are dropped silently, which is what lets the
        /// scanner add kinds without breaking this reader.</summary>
        internal static ScanEvent Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            line = line.Trim();
            if (line.Length == 0 || line[0] != '{') return null;
            JObject o;
            try { o = JObject.Parse(line); }
            catch { return null; }

            string ev = (string)o["ev"];
            if (string.IsNullOrEmpty(ev)) return null;

            return new ScanEvent
            {
                Ev          = ev,
                Name        = (string)o["name"],
                Detail      = (string)o["detail"],
                Level       = (string)o["level"],
                Why         = (string)o["why"],
                Headline    = (string)o["headline"],
                OneLine     = (string)o["oneLine"],
                Cue         = (string)o["cue"],
                Index       = Int(o["index"]),
                Of          = Int(o["of"]),
                Seconds     = Dbl(o["seconds"]),
                SecondsLeft = Int(o["secondsLeft"]),
                N           = Int(o["n"]),
                Id          = (string)o["id"],
                Result      = (string)o["result"],
                Address     = (string)o["address"],
                Type        = (string)o["type"],
                Label       = (string)o["label"],
                Confidence  = (string)o["confidence"],
                Evidence    = (string)o["evidence"],
                WatchKind   = (string)o["watchKind"],
                WatchLen    = Int(o["watchLen"]),
                ModuleAddr  = (string)o["moduleAddr"],
                Cmd         = (string)o["cmd"],
                AtTick      = Int(o["atTick"]),
                Tick        = Int(o["tick"]),
                AtMs        = Dbl(o["atMs"]),
                Rows        = WatchRows(o["rows"]),
                Chosen      = (string)o["chosen"],
                Recorded    = Lng(o["recorded"]),
                Census      = Lng(o["census"]),
                Dropped     = Lng(o["dropped"]),
                Lines       = StringList(o["lines"]),
                PathText     = FirstString(o, "spec", "path", "pathText", "chain"),
                PathTarget   = FirstString(o, "target", "pathTarget"),
                PathRoot     = FirstString(o, "root", "pathRoot", "base"),
                PathOffsets  = LongList(FirstToken(o, "offsets", "pathOffsets")),
                PathDepth    = Int(FirstToken(o, "depth", "pathDepth", "levels")),
                PathWindow   = Int(FirstToken(o, "window", "pathWindow")),
                PathModule   = FirstString(o, "rootModule", "module", "pathModule"),
                PathRootKind = FirstString(o, "rootKind", "pathRootKind"),
                PathStatic   = Bool(FirstToken(o, "rootFixed", "static", "isStatic", "rootStatic")),
                PathRank     = Int(FirstToken(o, "rank", "pathRank")),
                Outcome      = FirstString(o, "outcome"),
                Resolved     = FirstString(o, "resolved"),
                PathValue    = FirstString(o, "value"),
                RoundName    = FirstString(o, "roundName", "name"),
                RoundMust    = FirstString(o, "must"),
                Changed      = StringList(o["changed"]),
                Unchanged    = StringList(o["unchanged"]),
                ChangedCount = Int(o["changedCount"]),
                UnchangedCount = Int(o["unchangedCount"]),
                ExitCode    = Int(o["exitCode"]),
                OutDir      = (string)o["outDir"],
                Raw         = o,
            };
        }

        /// <summary>The first of these keys that is present. The path fields are
        /// the newest thing on this wire and the scanner half is being written
        /// alongside this one, so a spelling that differs by a synonym costs a
        /// missing column rather than a feature that silently does nothing. Raw
        /// is always there for anything none of these caught.</summary>
        private static JToken FirstToken(JObject o, params string[] keys)
        {
            foreach (string k in keys)
            {
                var t = o[k];
                if (t != null && t.Type != JTokenType.Null) return t;
            }
            return null;
        }

        private static string FirstString(JObject o, params string[] keys)
        {
            var t = FirstToken(o, keys);
            if (t == null) return null;
            try { return t.Type == JTokenType.String ? (string)t : t.ToString(); }
            catch { return null; }
        }

        /// <summary>An array of offsets. Hex strings are accepted as well as
        /// numbers: an offset printed as "0x18" is the commoner shape in this
        /// corner of the world, and reading it as decimal would land somewhere
        /// else entirely.</summary>
        private static List<long> LongList(JToken token)
        {
            var arr = token as JArray;
            if (arr == null) return null;
            var list = new List<long>(arr.Count);
            foreach (var item in arr)
            {
                if (item == null || item.Type == JTokenType.Null) continue;
                if (item.Type == JTokenType.String)
                {
                    string s = ((string)item ?? "").Trim();
                    bool neg = s.StartsWith("-", StringComparison.Ordinal);
                    if (neg) s = s.Substring(1).Trim();
                    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                    if (long.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                                      System.Globalization.CultureInfo.InvariantCulture, out long hex))
                        list.Add(neg ? -hex : hex);
                    continue;
                }
                try { list.Add(item.Value<long>()); } catch { }
            }
            return list;
        }

        /// <summary>A plain array of strings, or null when the field was absent.
        /// Anything in it that is not a string is skipped rather than taking the
        /// whole array down.</summary>
        private static List<string> StringList(JToken token)
        {
            var arr = token as JArray;
            if (arr == null) return null;
            var list = new List<string>(arr.Count);
            foreach (var item in arr)
            {
                if (item == null || item.Type == JTokenType.Null) continue;
                try { list.Add(item.Type == JTokenType.String ? (string)item : item.ToString()); }
                catch { }
            }
            return list;
        }

        /// <summary>The rows of one watch refresh, or null when the line had no
        /// rows array. An entry that is not an object is skipped rather than
        /// taking the whole refresh down with it: losing one row is a gap, and
        /// losing the line is every row freezing at once with no explanation.</summary>
        private static List<ScanWatchRow> WatchRows(JToken token)
        {
            var arr = token as JArray;
            if (arr == null) return null;
            var rows = new List<ScanWatchRow>(arr.Count);
            foreach (var item in arr)
            {
                var o = item as JObject;
                if (o == null) continue;
                string addr = (string)o["addr"];
                if (string.IsNullOrEmpty(addr)) continue;
                var value = o["value"];
                bool hasValue = value != null && value.Type != JTokenType.Null;
                rows.Add(new ScanWatchRow
                {
                    Addr     = addr,
                    Resolved = (string)o["resolved"],
                    Kind     = (string)o["kind"],
                    // Values are strings on the wire, but a scanner that sends a
                    // bare number for a numeric kind is being helpful rather than
                    // wrong, so both are accepted. Invariant on the way out: a
                    // number rendered with this machine's decimal comma would not
                    // match the same number rendered as text.
                    Value    = WatchValueText(value, hasValue),
                    // An absent "ok" means the read worked if a value came with
                    // it. Defaulting the other way would print every row as
                    // broken the first time a field is renamed.
                    Ok       = o["ok"] != null && o["ok"].Type != JTokenType.Null
                                   ? Bool(o["ok"]) : hasValue,
                    Why      = (string)o["why"],
                });
            }
            return rows;
        }

        /// <summary>One watched value as text. A JSON string arrives verbatim;
        /// anything else is rendered invariantly, because a value shown one way
        /// and compared another is a change the operator never made.</summary>
        private static string WatchValueText(JToken value, bool hasValue)
        {
            if (!hasValue) return null;
            var jv = value as JValue;
            if (jv == null) return value.ToString(Newtonsoft.Json.Formatting.None);
            if (jv.Value == null) return null;
            if (jv.Type == JTokenType.String) return (string)jv.Value;
            try
            {
                return Convert.ToString(jv.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return jv.ToString(); }
        }

        private static bool Bool(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return false;
            try { return t.Value<bool>(); } catch { }
            try
            {
                string s = t.ToString();
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
            }
            catch { return false; }
        }

        private static int Int(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return t.Value<int>(); } catch { return 0; }
        }

        private static long Lng(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return t.Value<long>(); } catch { return 0; }
        }

        private static double Dbl(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return t.Value<double>(); } catch { return 0; }
        }
    }

    /// <summary>What the Memory Map tab asks for when it starts a scan. Kept as
    /// a type rather than a pile of arguments because the scripts and the
    /// target both grow.</summary>
    public sealed class MemoryScanRequest
    {
        /// <summary>The process to read. A pid is exact and survives two
        /// processes sharing an executable name, so it wins when we have one.</summary>
        public int Pid;
        public string ProcessName;
        /// <summary>rpm-v1 | rpm-fast | rpm-inmotion. Ignored when Survey is set,
        /// because a survey performs no script at all.</summary>
        public string Script = "rpm-v1";

        /// <summary>A survey rather than a hunt: census the whole of memory,
        /// name nothing, and report the tier ladder.
        ///
        /// It exists to answer one question before any driving is planned, which
        /// is how many candidates each keep threshold catches on this target,
        /// what recording each of those would cost, and where they live. The
        /// ordinary run seeds on a magnitude window and applies the stillness
        /// rules BEFORE recording, so it can only ever yield candidates for the
        /// one field it was aimed at. A map needs to know the cost of not
        /// aiming.
        ///
        /// This asks for the scanner's own survey mode rather than talking the
        /// ordinary hunt down filter by filter, which is what this end used to
        /// do and what failed: a hunt tightens ITSELF when the survivor count
        /// runs away, and the auto-tightening plus the survivor cap between them
        /// threw away 15.1 million of 15.3 million candidates on the run that
        /// found this out. Survey mode is the flag that says do not do that.</summary>
        public bool Survey;

        /// <summary>Recording length for a survey. Ignored otherwise: a script
        /// sets its own length and refuses a conflicting one.</summary>
        public int SurveySeconds = 120;

        /// <summary>How many NEVER-MOVING values the census lists, or 0 for the
        /// scanner's own limit.
        ///
        /// A limit on the FILE, not on the drive. Those values are written from
        /// what the narrowing already watched rather than from the recording, so
        /// they cost the recorder nothing: a value that holds one number needs
        /// its address and its number, not a time series. They are also the ones
        /// a survey is run for, being the redline, the car id and the gear that
        /// sat still, so a cap that bites is worth being able to raise from
        /// here.</summary>
        public long CensusStaticCap;

        /// <summary>Keep the scanner alive for the watch list and nothing else.
        ///
        /// The watch list is the only part of this tab that wants a scanner
        /// running while the operator is doing something in the GAME rather
        /// than at the wheel: find the car name, watch the address, go and pick
        /// a different car, and see whether the row followed. Every other run
        /// ends and takes the scanner with it, and a watch cannot outlive the
        /// process that is doing the reading.
        ///
        /// It performs no script and declares nothing, so it names nothing and
        /// ends UNPROVEN, which is correct.</summary>
        public bool WatchHost;

        /// <summary>How long the watch host stays up before ending on its own.
        /// Stop ends it sooner.</summary>
        public int WatchSeconds = 600;

        // ---- known values: what the operator can read off the screen --------
        //
        // The strongest evidence in this whole tool, and the cheapest. A
        // behavioural predicate describes the PEDAL, and a racing game is full
        // of pedal-followers; the car's name and the gear you are in are exact.
        // Each of these is passed ONLY when it has been filled in, so a blank
        // box means that search does not run rather than running on a guess.

        /// <summary>The car's displayed name, searched for in ASCII, UTF-8,
        /// UTF-16LE and Shift-JIS. Needs no driving at all.</summary>
        public string KnownString;

        /// <summary>Find the gear from the SHIFT PRESSES, with nothing declared.
        /// The normal path, and the stronger one.
        ///
        /// The plugin has the shift buttons bound, so every press reaches the
        /// scanner with its direction and the tick it landed on. From an unknown
        /// starting gear G, "up, up, down" is G, G+1, G+2, G+1, and that unknown
        /// G is exactly the constant offset the search already solves for, so
        /// nothing needs declaring. The constraint is also two-sided and much
        /// tighter than an order alone: the value has to move AT a press, by a
        /// constant step, in the direction of the button, and NOT move in
        /// between.
        ///
        /// Mutually exclusive with <see cref="KnownGear"/>, which is the
        /// override for a session where the buttons cannot be bound.</summary>
        public bool KnownGearLive;

        /// <summary>The gears about to be driven, in order, as "1,2,3,4,3,2".
        /// The OVERRIDE, not the main path: a declared order can be got wrong,
        /// and a search that finds nothing because the operator drove something
        /// else is indistinguishable from the gear not being there.</summary>
        public string KnownGear;

        /// <summary>How many candidate slots the gear seed may hold, or 0 for
        /// the scanner's own limit. Worth having on the outside because when the
        /// cap bites, the HIGHEST addresses are the ones dropped, and the answer
        /// may be among them; a truncated seed has to be re-runnable wider
        /// without editing anything.</summary>
        public long GearSeedCap;

        /// <summary>The car's redline in rpm, searched as a fixed value near a
        /// known anchor. Zero or less means it was not given.</summary>
        public int KnownRedline;

        // ---- pointer paths -------------------------------------------------
        //
        // A paths run is its OWN run, not a command on the wire. The scanner
        // refuses to combine one with a search, a survey, a script or a
        // watch-only host, and it is right to: a paths run walks backwards from
        // an address somebody already has, and a search is how you get one.
        //
        // It is the half that makes this a map. Every value the real target has
        // given up so far is heap or anonymous, so an absolute address is true
        // for one launch; a chain from a root in an image that cannot rebase is
        // true for every launch.

        /// <summary>Absolute addresses to find pointer paths to. Any entry makes
        /// this a paths run and nothing else.</summary>
        public List<string> PathsTo = new List<string>();

        /// <summary>Dereferences allowed, 1 to 8.</summary>
        public int PathDepth = 3;

        /// <summary>How far before the value a pointer may land and still count.
        /// A tight window is stronger evidence than a wide one.</summary>
        public int PathWindow = 512;

        /// <summary>Paths reported per address.</summary>
        public int PathMax = 100;

        /// <summary>How the value at the target is read, carried into the
        /// scanner's own paths file so it goes straight back into a validation
        /// run without being retyped.</summary>
        public string PathKind;
        public int PathLen;
        public string PathLabel;

        /// <summary>The plausibility range stored with the row, so a later
        /// validation run can tell "still the field" from "still resolves, reads
        /// nonsense". NaN for a field with no numeric range.</summary>
        public double PathExpectMin = double.NaN;
        public double PathExpectMax = double.NaN;

        /// <summary>Keep the watch list alive through the paths run, so the map
        /// does not go dark while a path is being looked for. The scanner allows
        /// this one combination explicitly.</summary>
        public bool PathWatchHold;

        public bool AnyPathSearch => PathsTo != null && PathsTo.Count > 0;

        /// <summary>True when at least one known value was declared, which makes
        /// this a known-value run rather than a driving one. The two cannot
        /// share a run: the scanner's known-value search returns before the
        /// recorder starts, so a script asked for alongside it would be accepted
        /// and never performed.</summary>
        public bool AnyKnownValue =>
            !string.IsNullOrWhiteSpace(KnownString)
            || KnownGearLive
            || !string.IsNullOrWhiteSpace(KnownGear)
            || KnownRedline > 0;

        /// <summary>True when this run wants the gear, either way round.</summary>
        public bool AnyGearSearch => KnownGearLive || !string.IsNullOrWhiteSpace(KnownGear);

        /// <summary>How many shift presses the press-driven search waits for.
        ///
        /// The scanner counts them through and ends the search on the last one,
        /// so this is the length of the session as well as the strength of the
        /// evidence: six presses left four survivors out of twenty million on
        /// the harness, and two is the fewest that constrains anything at all,
        /// because one press with an unknown starting gear constrains nothing.
        /// Only read when <see cref="KnownGearLive"/> is set.</summary>
        public int GearPresses = 8;
    }

    /// <summary>
    /// The scanner's command line, built from a request and nothing else.
    ///
    /// Out here, with no SimHub types anywhere near it, because this is the half
    /// of the pair that keeps getting the spelling wrong and the wire gate can
    /// only test what it can compile. Every argument SimHub actually sends is
    /// built by this method, so the gate drives the real shapes rather than a
    /// copy of them that ages quietly beside them.
    /// </summary>
    public static class MemoryScanArgs
    {
        /// <summary>The scanner refuses fewer than this, because one press with
        /// an unknown starting gear constrains nothing at all.</summary>
        public const int MinGearPresses = 2;

        /// <summary>The scanner's own ceiling on the press count.</summary>
        public const int MaxGearPresses = 200;

        /// <summary>Wrap an argument that may contain spaces. A trailing
        /// backslash is trimmed rather than escaped: left in place it would
        /// escape the closing quote and swallow the next argument.</summary>
        public static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            string v = s.TrimEnd('\\');
            if (v.Length == 0) v = s;
            return v.IndexOf(' ') >= 0 ? "\"" + v.Replace("\"", "\\\"") + "\"" : v;
        }

        /// <summary>Build the whole command line. Returns null and sets
        /// <paramref name="error"/> when the request cannot describe a run.
        ///
        /// watchHz and watchRows are the plugin's own watch list, passed in
        /// rather than reached for, so this stays a pure function of its
        /// arguments and the gate can drive it.</summary>
        public static string Build(MemoryScanRequest req, string outDir, int watchHz, int watchRows,
                                   out string error)
        {
            error = null;
            if (req == null) { error = "nothing to scan"; return null; }

            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // Which process, first: a pid is exact and survives two profiles
            // sharing an executable name, so it wins whenever we have one.
            string targetArg;
            if (req.Pid > 0)
                targetArg = " --pid " + req.Pid.ToString(inv);
            else if (!string.IsNullOrWhiteSpace(req.ProcessName))
                targetArg = " --process " + Quote(req.ProcessName.Trim());
            else { error = "no target process"; return null; }

            var args = new StringBuilder();
            // --events puts one JSON object per line on stdout and every human
            // readable line on stderr. --no-console-cues stops memscan counting
            // the operator as well: this tab and the wheel are showing the cues,
            // and two prompters counting the same script slightly differently is
            // worse than one. It also silences the 20 Hz status line, which
            // would otherwise reach SimHub.txt as several hundred lines a run.
            // The two flags are independent, so both are needed.
            args.Append("--events --no-console-cues");
            // Always explicit, never the scanner's relative default: its working
            // directory is the SimHub install under Program Files, where creating
            // a folder needs admin, so the run would die on its first line.
            args.Append(" --out ").Append(Quote(outDir ?? ""));
            args.Append(targetArg);

            if (req.AnyPathSearch)
            {
                // FIRST in this chain, because a paths run excludes every other
                // mode below it and the scanner refuses the combination rather
                // than picking one. It performs no script, so none is passed:
                // naming one is a usage error and exit code 2, which reads on
                // the tab as "your memscan is too old".
                var addrs = new List<string>();
                foreach (string one in req.PathsTo)
                {
                    string t = (one ?? "").Trim();
                    if (t.Length == 0) continue;
                    // Absolute only. A module-relative address or a path is
                    // already durable and has nothing to gain from this.
                    if (t.IndexOf('+') >= 0 || t.IndexOf('[') >= 0)
                    {
                        error = "a pointer path is only worth looking for from an absolute address, and \""
                              + t + "\" is not one";
                        return null;
                    }
                    addrs.Add(t);
                }
                if (addrs.Count == 0) { error = "no address to find a path to"; return null; }
                args.Append(" --paths-to ").Append(Quote(string.Join(",", addrs.ToArray())));
                int depth = req.PathDepth < 1 ? 1 : req.PathDepth > 8 ? 8 : req.PathDepth;
                args.Append(" --path-depth ").Append(depth.ToString(inv));
                int window = req.PathWindow < 0 ? 0 : req.PathWindow > 1048576 ? 1048576 : req.PathWindow;
                args.Append(" --path-window ").Append(window.ToString(inv));
                if (req.PathMax > 0) args.Append(" --path-max ").Append(req.PathMax.ToString(inv));
                if (!string.IsNullOrWhiteSpace(req.PathKind))
                    args.Append(" --paths-kind ").Append(req.PathKind.Trim());
                if (req.PathLen > 0) args.Append(" --paths-len ").Append(req.PathLen.ToString(inv));
                if (!string.IsNullOrWhiteSpace(req.PathLabel))
                    args.Append(" --paths-label ").Append(Quote(req.PathLabel.Trim()));
                // The range travels with the row so the RELAUNCH test can tell a
                // path that still resolves from one that still reads the field.
                if (!double.IsNaN(req.PathExpectMin) && !double.IsNaN(req.PathExpectMax))
                    args.Append(" --paths-expect ")
                        .Append(req.PathExpectMin.ToString("0.####", inv))
                        .Append(',')
                        .Append(req.PathExpectMax.ToString("0.####", inv));
                // The one combination the scanner allows alongside a paths run,
                // and it matters: without it the map goes dark for the length of
                // the search, and a candidate nobody is reading cannot be ruled
                // in or out either way.
                if (req.PathWatchHold)
                    args.Append(" --watch-hold")
                        .Append(" --watch-hz ").Append(watchHz.ToString(inv))
                        .Append(" --watch-seconds ")
                        .Append((req.WatchSeconds > 0 ? req.WatchSeconds : 600).ToString(inv));
            }
            else if (req.WatchHost)
            {
                // A scanner that reads the watch list and does nothing else.
                // --watch-only is the scanner's own mode for this: no region
                // walk, no narrowing, no recording, no ranking, so it starts in
                // a fraction of a second and costs the target almost nothing
                // while the operator is away in the game changing the car.
                //
                // The rows go over the WIRE rather than in the flag, even though
                // --watch would take them: a label is operator-typed free text,
                // and a comma or a colon in one would cut a --watch spec in half
                // and mean a different address.
                int wsecs = req.WatchSeconds > 0 ? req.WatchSeconds : 600;
                args.Append(" --watch-only")
                    .Append(" --watch-hz ").Append(watchHz.ToString(inv))
                    .Append(" --watch-seconds ").Append(wsecs.ToString(inv));
            }
            else if (req.Survey)
            {
                // ONE flag, and the scanner owns the policy behind it.
                //
                // This end used to spell a survey out as a pile of opened-up
                // hunt filters. It did not work, and could not: a hunt TIGHTENS
                // ITSELF when the survivor count runs away, and no combination
                // of flags from out here talks it out of that. On the run that
                // measured it, the change rule auto-tightened from 0.35 to 0.70
                // and rejected 15,146,590 of 15,345,713 survivors, the survivor
                // cap dropped 178,014 more that had already passed every rule,
                // and 19,889 of the 20,000 recorded were dropped before scoring
                // because pages had gone unreadable. 111 candidates scored out
                // of 229 million dwords, in the one mode whose whole purpose is
                // breadth.
                //
                // --survey is the mode that switches all of that off, and it
                // owns the magnitude window and the representation set as well:
                // a survey run through the hunt's DEFAULT window would be
                // narrower than the broken run it replaces, because a gear of 1
                // and a lap of 2 both sit below an rpm floor of 300.
                //
                // --script none stays: a survey performs no script, and saying
                // so costs nothing next to a default that would.
                int secs = req.SurveySeconds > 0 ? req.SurveySeconds : 120;
                args.Append(" --survey --script none")
                    .Append(" --seconds ").Append(secs.ToString(inv))
                    .Append(" --hz 30");
                // The one number a survey has that is worth setting from out
                // here. It governs how many of the never-moving values reach
                // survey.csv, and those are the point of the exercise: a
                // redline, a car id, a gear held all run. It is not a recording
                // budget and does not lengthen the run.
                if (req.CensusStaticCap > 0)
                    args.Append(" --census-static-cap ").Append(req.CensusStaticCap.ToString(inv));
            }
            else if (req.AnyKnownValue)
            {
                // A known-value run and a driving script are TWO DIFFERENT RUNS,
                // and the scanner settles it: a known-value search returns before
                // the recorder ever starts. Named explicitly it is a usage error,
                // so the script is not passed at all here.
                args.Append(" --script none");
                // The rate and the time limit go on EVERY known-value run, not
                // only the ones that ask to hold. The scanner also arms a hold
                // off the first watch command it receives, so an operator who
                // presses Watch this while the search is still running turns
                // that run into a watch; unbounded, that is a child nobody ever
                // told to stop and a tab still waiting for a done event.
                int hsecs = req.WatchSeconds > 0 ? req.WatchSeconds : 600;
                args.Append(" --watch-hz ").Append(watchHz.ToString(inv))
                    .Append(" --watch-seconds ").Append(hsecs.ToString(inv));
                // When there is already a map to keep live, the search holds on
                // it afterwards instead of exiting, so the operator can change
                // the car and watch the answer it just gave.
                if (watchRows > 0) args.Append(" --watch-hold");
            }
            else if (!string.IsNullOrWhiteSpace(req.Script))
            {
                args.Append(" --script ").Append(req.Script.Trim());
            }

            // Known values, each passed ONLY when it was filled in. An empty box
            // means that search does not run, and the tab says so before anyone
            // drives a session believing otherwise.
            //
            // Never on a paths run: the scanner refuses the pair outright, and
            // its reasoning is that a search is how you GET the address a paths
            // run starts from, so asking for both is a statement about the run
            // that cannot be true.
            if (req.AnyPathSearch) return args.ToString();
            if (!string.IsNullOrWhiteSpace(req.KnownString))
                args.Append(" --known-string ").Append(Quote(req.KnownString.Trim()));
            // The gear, one way or the other and never both. Live is the normal
            // path: the shift bindings are already sending every press with its
            // direction, so the sequence is DERIVED from what was actually
            // pressed and there is nothing to declare and nothing to get wrong.
            // The typed sequence stays as the override for a session where the
            // buttons cannot be bound.
            //
            // The press COUNT goes with it, because the scanner counts the
            // presses through and ends the search on the last one: this number is
            // the length of the session as well as the strength of the evidence.
            if (req.KnownGearLive)
            {
                int presses = req.GearPresses;
                if (presses < MinGearPresses) presses = MinGearPresses;
                if (presses > MaxGearPresses) presses = MaxGearPresses;
                args.Append(" --known-gear-presses ").Append(presses.ToString(inv));
            }
            else if (!string.IsNullOrWhiteSpace(req.KnownGear))
            {
                args.Append(" --known-gear ").Append(Quote(req.KnownGear.Trim()));
            }
            // Only with a gear search, and only when it was asked for: the
            // scanner's own limit is a function of the memory ceiling and is the
            // right default. This exists so a run that warned it had truncated
            // can be re-run wider without editing anything.
            if (req.GearSeedCap > 0 && req.AnyGearSearch)
                args.Append(" --gear-seed-cap ").Append(req.GearSeedCap.ToString(inv));
            if (req.KnownRedline > 0)
                args.Append(" --known-redline ").Append(req.KnownRedline.ToString(inv));

            return args.ToString();
        }
    }

    /// <summary>
    /// Which of several pointer paths to the same address is the one worth keeping.
    ///
    /// Out here rather than on the settings tab for one reason: the wire gate compiles this file and
    /// drives it against the real scanner's real output, and an ordering only a WPF control can run
    /// can only be checked by reading it. Reading it is how it came to disagree with the scanner.
    /// </summary>
    public static class ScanPathOrder
    {
        /// <summary>The path as one string. The scanner sends the rendered spec; the root and the
        /// offsets are the fallback for a build that sends those instead, and both forms have to
        /// produce the same text.</summary>
        public static string TextOf(ScanEvent ev)
        {
            if (ev == null) return null;
            if (!string.IsNullOrWhiteSpace(ev.PathText)) return ev.PathText.Trim();
            if (string.IsNullOrWhiteSpace(ev.PathRoot)) return null;
            var spec = new MemoryPathSpec { Root = ev.PathRoot.Trim() };
            if (ev.PathOffsets != null) spec.Offsets = new List<long>(ev.PathOffsets);
            return spec.Format();
        }

        /// <summary>
        /// Best first, where "best" is THE SCANNER'S ordering and not a second opinion formed here.
        ///
        /// The scanner ranks by root durability first, and it knows two things this end does not:
        /// whether the root is in the GAME'S OWN EXECUTABLE rather than a DLL, and what that image's
        /// PE header says about whether Windows may move it. Both are measured over there, during the
        /// walk, against bytes only that process read.
        ///
        /// This used to re-sort by "cannot rebase, then shallowest", which agrees with the scanner
        /// only when the exe happens to be the fixed-base image. On a target whose executable is
        /// ASLR-enabled every root reports rebasing, the tie-break becomes depth alone, and a
        /// one-dereference coincidence through a framework DLL beats the game's own three-deep chain
        /// and is written into the map as the durable route. The known-answer harness is exactly that
        /// shape, and it picked the decoy.
        ///
        /// So rank leads. The rest are tie-breaks for a scanner too old to send one, and they are
        /// kept in the scanner's own order for that case.
        /// </summary>
        public static int Compare(ScanEvent a, ScanEvent b)
        {
            int ra = a.PathRank > 0 ? a.PathRank : int.MaxValue;
            int rb = b.PathRank > 0 ? b.PathRank : int.MaxValue;
            if (ra != rb) return ra.CompareTo(rb);
            // A root in the game's own executable beats one in a DLL: a DLL can be unloaded and
            // reloaded somewhere else, and the executable cannot.
            bool ea = string.Equals(a.PathRootKind, "main-exe", StringComparison.OrdinalIgnoreCase);
            bool eb = string.Equals(b.PathRootKind, "main-exe", StringComparison.OrdinalIgnoreCase);
            if (ea != eb) return ea ? -1 : 1;
            if (a.PathStatic != b.PathStatic) return a.PathStatic ? -1 : 1;
            int da = a.PathDepth > 0 ? a.PathDepth : (a.PathOffsets?.Count ?? 0);
            int db = b.PathDepth > 0 ? b.PathDepth : (b.PathOffsets?.Count ?? 0);
            if (da != db) return da.CompareTo(db);
            int wa = a.PathWindow > 0 ? a.PathWindow : int.MaxValue;
            int wb = b.PathWindow > 0 ? b.PathWindow : int.MaxValue;
            if (wa != wb) return wa.CompareTo(wb);
            return string.Compare(TextOf(a) ?? "", TextOf(b) ?? "", StringComparison.Ordinal);
        }
    }
}
