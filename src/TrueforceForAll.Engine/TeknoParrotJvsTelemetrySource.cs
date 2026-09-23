// Reads the arcade force-feedback command an emulated arcade game emits, from
// the named shared memory block TeknoParrot publishes for its JVS IO board.
//
// WHY THIS EXISTS. Arcade racers under TeknoParrot are invisible to SimHub: no
// game name, no GameRunning, no telemetry. But the game itself still computes
// force and writes it to the cabinet's IO board, and TeknoParrot emulates that
// board in a 64-byte named mapping. So the force is already published; we only
// have to read it. That is strictly better than reverse-engineering one, and it
// means we never read or write the game's memory and inject nothing.
//
// WHERE THE LAYOUT COMES FROM. FFBArcadePlugin (Boomslangnz, open source) reads
// the same block: a DWORD at int index 2 for most titles, at int index 6 for a
// handful. Which slot, and how the DWORD decodes, is per game, which is why this
// class holds no protocol knowledge at all: it reads bytes and hands them to an
// IArcadeFfbDecoder. See ArcadeFfb.cs for the vocabulary every decoder emits.
//
// LIVENESS WITHOUT A SEQUENCE COUNTER. AC has packetId and R3E has SimTicks;
// this block has neither, so "the command did not change" cannot be told from
// "the game stopped" by looking at the command alone.
//
// This comment used to claim the whole 64-byte block was a liveness signal
// because it "carries live cabinet IO (wheel, pedals, buttons), which never sits
// perfectly still while someone is playing". That is FALSE, and measuring it was
// the whole story behind a wheel that clicked. A read-only probe of the live
// mapping with Initial D 8 running found four nonzero bytes in the entire 4096
// (offsets 0, 8, 9 and 10) and not one byte changing over 496 samples: the only
// thing here that ever moves is the FFB command dword. So "every byte is
// unchanged" means no more than "the command is unchanged", which is exactly the
// held force that is legitimate and common.
//
// The cabinet says how long it means a command to last, and we take it at its
// word: Initial D 6, 7 and 8 all ship FeedbackLength=5000 in FFBArcadePlugin's
// own ini. A 400 ms gate was 12.5x shorter than the game's own intended effect
// duration, so it cut the force mid-effect and restored it on the next command,
// 78 times in one session with 18 of the gaps under 100 ms. Every one of those
// edges is a step in commanded torque, which a wheel renders as a click.
//
// So the force ends on EXPIRY, per effect, at the duration the cabinet asked
// for, and the block gate is now only a coarse backstop at the same horizon. The
// issue-13 property is kept: force still always reaches zero on its own, it just
// does so when the effect was meant to end rather than a fifth of the way in.
//
// Threading matches the other sources: one background poll thread owns every
// read and every decode; the pump thread only reads what this publishes, which
// is either a single Interlocked long or one immutable reference swap, so
// nothing can tear across the two.

using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class TeknoParrotJvsTelemetrySource : TelemetrySourceBase, IArcadeForceSource
    {
        // TeknoParrot creates this with a bare name, which resolves in the
        // creator's session namespace. SimHub usually runs elevated (USBPcap
        // wants it) while TeknoParrot may not; same session still means the same
        // Local namespace, so this normally works, but a different-user launch
        // will simply never open. We try the qualified forms too rather than
        // leave that as a silent nothing.
        private static readonly string[] MapNames =
        {
            "TeknoParrot_JvsState",
            "Local\\TeknoParrot_JvsState",
            "Global\\TeknoParrot_JvsState",
        };

        public const int BlockBytes = 64;

        // 500 Hz poll against a block the cabinet IO updates at roughly frame
        // rate. Cheap (a 64-byte copy) and keeps command latency under 2 ms.
        private const int TickPeriodMs  = 2;
        private const int RetryPeriodMs = 500;   // slow cadence while waiting for the game

        // Emit a frame every Nth poll, so MeasuredHz reads roughly the cabinet's
        // own rate (~60 Hz) rather than our poll rate. Force and effects still
        // update every poll: both are published through latches the pump reads
        // directly, never through a frame, so slowing the frames costs nothing.
        // Emitting all 500 would run the plugin's whole frame-enrichment chain
        // 500 times a second over a frame that carries nothing.
        private const int EmitEveryNPolls = 8;

        // Coarse backstop only, now that each effect expires on its own. Set to
        // the cabinet's own declared effect duration so it can never fire before
        // the effect it would be cutting was due to end anyway.
        private const int StaleBlockMs = CommandHoldMs;

        /// <summary>How long one decoded effect stays live without being restated.
        ///
        /// This is the cabinet's own number, not ours: FFBArcadePlugin ships
        /// FeedbackLength=5000 for Initial D 6, 7 and 8, which is how long the
        /// game intends an effect to run when it sends it once. The protocol
        /// carries ONE command at a time, so a spring command does not mean "stop
        /// the constant", it means "also do this"; treating each new command as a
        /// full replacement made the two mutually exclusive and stepped the wheel
        /// between them at the cabinet's command rate.</summary>
        private const int CommandHoldMs = 5000;

        private const long FfbTimestampMask = 0xffff_ffff_ffffL;

        /// <summary>FFBArcadePlugin's game id, which is how these protocols are
        /// identified everywhere. Selects the decoder. Default 18 is Initial D
        /// Arcade Stage 8.</summary>
        public int GameId
        {
            get { return _gameId; }
            set
            {
                _gameId = value;
                _decoder = ArcadeDecoders.ForGameId(value);
            }
        }
        private int _gameId = 18;
        private IArcadeFfbDecoder _decoder = ArcadeDecoders.ForGameId(18);

        /// <summary>Overrides the decoder's own slot choice. 0 means "use the
        /// decoder's". Only set this if a capture shows the command elsewhere.</summary>
        public int SlotOverride { get; set; }

        /// <summary>Scales every decoded strength into device force. Whether a
        /// cabinet's level is linear in torque is unverified for any of these
        /// games, so this exists to be turned down on a rig rather than trusted.</summary>
        public double ForceScale { get; set; } = 1.0;

        /// <summary>Flips the decoded constant-force direction wholesale, for the
        /// case that a rig test has the wheel fighting the driver. Note this is
        /// BEFORE TrueforceDevice.FfbInvertSign, which negates every provider
        /// value again, so there are two sign flips in the chain and only the
        /// wheel settles which combination is right.</summary>
        public bool InvertConstantDirection { get; set; }

        public Action<string> Logger { get; set; }

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private string _openedName;
        private Thread _thread;
        private volatile bool _stopping;
        private int _running;

        private readonly byte[] _buf  = new byte[BlockBytes];
        private readonly byte[] _prev = new byte[BlockBytes];
        private bool _havePrev;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastBlockChangeTicks;
        private volatile bool _blockLive;

        // Scratch the poll thread decodes into, so a decode that rejects the
        // command allocates nothing. Published state is a separate instance.
        private readonly ArcadeFfbState _scratch = new ArcadeFfbState();

        // What is CURRENTLY playing, which is not the same as what the last
        // command said. One command arrives at a time and each names a single
        // effect, so the latch keeps the others alive until their own expiry
        // rather than letting every command silently cancel the rest.
        private readonly ArcadeFfbState _latch = new ArcadeFfbState();
        private long _constantUntil, _springUntil, _frictionUntil,
                     _damperUntil, _periodicUntil, _rumbleUntil;

        // Command-stream trace. Every diagnostic here used to be write-only, so
        // the opcode mix, the magnitude spread and the reject rate of a real
        // cabinet could not be read back at all, and which of several candidate
        // faults actually fires had to be argued rather than measured. One line
        // per CHANGE of the command dword, which is a few tens a second at the
        // cabinet's own rate, not per poll.
        private uint _prevTraceDword;
        private bool _haveTraceDword;
        private long _lastTraceTicks;
        private int  _traceLines;
        /// <summary>Log one line per command change. Off by default: this is a
        /// capture tool, not running commentary.</summary>
        public bool TraceCommands;

        private sealed class StateSnap
        {
            public ArcadeFfbState State;
            public long Ticks;
        }

        // One reference swap publishes both the state and its timestamp, so the
        // pump can never read a state with the wrong age.
        private volatile StateSnap _snap;

        // Scalar force latch: (ticks48 << 16) | (ushort)value, the same packing
        // and wrap-safe age math as the tap and the AC source.
        private long _ffbPacked;

        // Diagnostics. How a game encodes its second byte is the single biggest
        // unknown in any of these protocols, so we count what actually arrives
        // instead of guessing twice. Poll thread writes, UI reads; a stale read
        // is fine.
        private readonly int[,] _paramHistogram = new int[256, 256];
        // The param histogram alone cannot say how hard the cabinet pushes,
        // which is what decides whether a magnitude range is being decoded
        // sensibly. This one is [opcode, magnitude].
        private readonly int[,] _opMagHistogram = new int[256, 256];
        private long _pollCount;
        private long _commandCount;
        private long _rejectedCount;
        private uint _lastRawDword;
        private uint _lastRawDwordAlt;
        private bool _loggedOpen;
        private bool _loggedStale;
        private bool _loggedNoDecoder;

        public override string Name => _decoder != null
            ? "TeknoParrot: " + _decoder.Name
            : "TeknoParrot arcade IO";
        public override bool IsEnhanced => true;

        // A cabinet publishes force, not telemetry. Saying so keeps the SimHub
        // overlay off these frames: it would otherwise paint the last game's
        // cached flags onto them, and an ABS flag left set by a sim that has
        // since closed would buzz for the whole arcade session with no speed
        // anywhere to end it.
        public override bool PublishesPhysics => false;
        public override bool IsRunning => _running != 0;

        /// <summary>True while the block is open AND changing. This is a real
        /// reading of the cabinet, so unlike the base physics proxy it does not
        /// need RPM or speed, neither of which this block carries.</summary>
        public override bool IsSessionActive => _blockLive;

        /// <summary>Deliberately left false. The block has no pause flag; block
        /// liveness is a good proxy but not an authority, and claiming authority
        /// here would make the provider's pause release return zero every tick
        /// the moment the proxy dipped.</summary>
        public override bool HasAuthoritativeSessionState => false;

        public bool MapOpen => _view != null;
        public string OpenedMapName => _openedName;
        public bool HaveDecoder => _decoder != null;
        public long PollCount => Interlocked.Read(ref _pollCount);
        public long CommandCount => Interlocked.Read(ref _commandCount);
        /// <summary>Commands the decoder rejected as not-a-real-command. A high
        /// count next to a low CommandCount usually means the wrong game id.</summary>
        public long RejectedCount => Interlocked.Read(ref _rejectedCount);
        /// <summary>The raw DWORD from the configured slot, and from the other
        /// candidate slot, for a first capture on an unknown cabinet.</summary>
        public uint LastRawFfbDword => _lastRawDword;
        public uint LastRawFfbDwordAltSlot => _lastRawDwordAlt;

        private int EffectiveSlot
        {
            get
            {
                int s = SlotOverride;
                if (s == 2 || s == 6) return s;
                return _decoder != null ? _decoder.SlotIndex : 2;
            }
        }

        /// <summary>How many times each second byte was seen for a given command
        /// byte, so one drive answers what a game's parameter byte means. Returns
        /// a copy.</summary>
        public int[] ParamHistogram(byte opcode)
        {
            var outp = new int[256];
            for (int i = 0; i < 256; i++) outp[i] = _paramHistogram[opcode, i];
            return outp;
        }


        public override void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            _stopping = false;
            // Deliberately NOT opening here. AC throws from Start() and leans on
            // the plugin's 1 Hz retry, but that retry only runs for games SimHub
            // recognises, which is exactly what an arcade title is not. Opening
            // lazily in the loop means the source can be started the moment the
            // game is seen and simply waits for TeknoParrot to publish.
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "TeknoParrotJvsTelemetrySource",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            Log($"started for game id {_gameId}"
                + (_decoder != null ? $" ({_decoder.Name}, slot {EffectiveSlot})" : ", NO DECODER")
                + $"; waiting for {MapNames[0]}.");
        }

        public override void Stop()
        {
            _stopping = true;
            try { var t = _thread; if (t != null) t.Join(2000); } catch { }
            _thread = null;
            try { LogCommandSummary(); } catch { }
            CleanupMap();
            Interlocked.Exchange(ref _ffbPacked, 0);
            _snap = null;
            ClearLatch();
            try { if (_decoder != null) _decoder.Reset(); } catch { }
            _havePrev = false;
            _blockLive = false;
            _loggedOpen = false;
            _loggedStale = false;
            _loggedNoDecoder = false;
            Interlocked.Exchange(ref _running, 0);
        }

        /// <summary>The latest arcade constant force if it is no older than
        /// <paramref name="maxAgeMs"/>, else null. Same contract, packing and
        /// wrap-safe age math as UsbPcapFfbTap and the AC source, so the provider
        /// chain consults all of them interchangeably.</summary>
        public short? TryGetFreshFfbTarget(int maxAgeMs)
        {
            long packed = Interlocked.Read(ref _ffbPacked);
            if (packed == 0) return null;
            long now = _sw.ElapsedTicks & FfbTimestampMask;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if (AgeTicks(packed, now) > maxAgeTicks) return null;
            return (short)(packed & 0xffff);
        }

        public void ApplyTuning(double forceScale, bool invertDirection, bool acceptDamper)
        {
            ForceScale = forceScale;
            InvertConstantDirection = invertDirection;
            // The direct reader has no published damper to accept: it decodes
            // the cabinet itself, and none of these protocols command one.
        }

        public void ClearLastFfbTarget()
        {
            Interlocked.Exchange(ref _ffbPacked, 0);
        }

        /// <summary>The effect-shaped part of the latest command (spring, damper,
        /// friction, periodic), if fresh. The caller renders these through
        /// HidppEffectEngine; the constant part goes through TryGetFreshFfbTarget
        /// instead, because the engine refuses constants by design.</summary>
        public ArcadeFfbState TryGetFreshState(int maxAgeMs)
        {
            var s = _snap;
            if (s == null) return null;
            long now = _sw.ElapsedTicks;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if ((now - s.Ticks) > maxAgeTicks) return null;
            return s.State;
        }

        private void PollLoop()
        {
            TimeBeginPeriod(1);
            try
            {
                var sw = Stopwatch.StartNew();
                long nextTickMs = 0;
                while (!_stopping)
                {
                    int periodMs = TickPeriodMs;

                    if (_view == null)
                    {
                        if (!TryOpen()) periodMs = RetryPeriodMs;
                    }
                    else
                    {
                        if (!PollOnce()) periodMs = RetryPeriodMs;
                    }

                    nextTickMs += periodMs;
                    long sleepMs = nextTickMs - sw.ElapsedMilliseconds;
                    if (sleepMs < 0)
                    {
                        // Fell behind (GC pause, a long reopen). Re-phase rather
                        // than spin to catch up: catching up on an IO block would
                        // only burn CPU reading the same bytes.
                        nextTickMs = sw.ElapsedMilliseconds + periodMs;
                        sleepMs = 0;
                    }
                    if (sleepMs > 0) Thread.Sleep((int)sleepMs);
                }
            }
            catch (Exception ex)
            {
                Log("poll loop stopped: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // Cleanup here as well as in Stop(), because Stop()'s Join can
                // time out and then null the view under a still-running read.
                CleanupMap();
                TimeEndPeriod(1);
            }
        }

        private bool TryOpen()
        {
            for (int i = 0; i < MapNames.Length; i++)
            {
                MemoryMappedFile mmf = null;
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(MapNames[i], MemoryMappedFileRights.Read);
                    var view = mmf.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.Read);
                    _mmf = mmf;
                    _view = view;
                    _openedName = MapNames[i];
                    _havePrev = false;
                    _lastBlockChangeTicks = _sw.ElapsedTicks;
                    if (!_loggedOpen)
                    {
                        _loggedOpen = true;
                        Log($"opened {MapNames[i]} (command slot int index {EffectiveSlot}).");
                    }
                    return true;
                }
                catch
                {
                    // Not running yet, or a name that does not exist in this
                    // namespace. Quietly try the next; a game that is simply not
                    // started must not spam the log.
                    if (mmf != null) { try { mmf.Dispose(); } catch { } }
                }
            }
            return false;
        }

        // Returns false when the block could not be read, which paces the caller
        // down to the retry cadence.
        private bool PollOnce()
        {
            try
            {
                // One ReadArray, not a field at a time: the command bytes must be
                // interpreted together, and separate reads could pair a magnitude
                // from one command with the opcode of the next.
                _view.ReadArray(0, _buf, 0, BlockBytes);
            }
            catch
            {
                // The game quitting leaves the view valid as a CLR object while
                // every read throws. Drop it and let the loop reopen.
                CleanupMap();
                _blockLive = false;
                Interlocked.Exchange(ref _ffbPacked, 0);
                _snap = null;
                ClearLatch();
                return false;
            }

            Interlocked.Increment(ref _pollCount);
            long now = _sw.ElapsedTicks;

            // Whole-block liveness. See the header: this stands in for the
            // sequence counter the block does not have.
            if (!_havePrev)
            {
                Buffer.BlockCopy(_buf, 0, _prev, 0, BlockBytes);
                _havePrev = true;
                _lastBlockChangeTicks = now;
            }
            else
            {
                bool changed = false;
                for (int i = 0; i < BlockBytes; i++)
                {
                    if (_buf[i] != _prev[i]) { changed = true; break; }
                }
                if (changed)
                {
                    Buffer.BlockCopy(_buf, 0, _prev, 0, BlockBytes);
                    _lastBlockChangeTicks = now;
                }
            }

            long staleTicks = (Stopwatch.Frequency / 1000L) * StaleBlockMs;
            bool live = (now - _lastBlockChangeTicks) <= staleTicks;
            if (live != _blockLive)
            {
                _blockLive = live;
                if (!live && !_loggedStale)
                {
                    _loggedStale = true;
                    Log("cabinet IO block stopped changing; holding force off until it moves again.");
                }
                else if (live && _loggedStale)
                {
                    _loggedStale = false;
                    Log("cabinet IO block is moving again.");
                }
            }

            int slot = EffectiveSlot;
            _lastRawDword    = ReadDword(_buf, slot * 4);
            _lastRawDwordAlt = ReadDword(_buf, (slot == 2 ? 6 : 2) * 4);

            if (!live)
            {
                // Do NOT keep publishing: a frozen block means paused, gone, or
                // an attract screen, and replaying a held force there is exactly
                // the full-lock failure we have shipped a fix for once already.
                Interlocked.Exchange(ref _ffbPacked, 0);
                _snap = null;
                ClearLatch();
                EmitLivenessFrame();
                return true;
            }

            var dec = _decoder;
            if (dec == null)
            {
                if (!_loggedNoDecoder)
                {
                    _loggedNoDecoder = true;
                    Log($"no decoder for game id {_gameId}; the block is being read but nothing is "
                        + "being rendered. Raw command values are still available for a capture.");
                }
                EmitLivenessFrame();
                return true;
            }

            uint dword = _lastRawDword;
            _paramHistogram[(dword >> 16) & 0xff, (dword >> 8) & 0xff]++;
            _opMagHistogram[(dword >> 16) & 0xff, dword & 0xff]++;

            // The scratch holds THIS command only, so it starts empty. What is
            // playing lives in the latch, and the two are merged below.
            _scratch.Constant = default(ArcadeTrigger);
            _scratch.Spring   = default(ArcadeTrigger);
            _scratch.Friction = default(ArcadeTrigger);
            _scratch.Damper   = default(ArcadeTrigger);
            _scratch.Periodic = default(ArcadeTrigger);
            _scratch.Rumble   = default(ArcadeTrigger);

            bool ok;
            try { ok = dec.TryDecode(_buf, dword, _scratch); }
            catch { ok = false; }

            if (TraceCommands && (!_haveTraceDword || dword != _prevTraceDword))
            {
                // Bounded so a pathological stream cannot fill the log: past the
                // cap we keep counting in the histograms and stop narrating.
                if (_traceLines < 4000)
                {
                    _traceLines++;
                    double sinceMs = _haveTraceDword
                        ? (now - _lastTraceTicks) * 1000.0 / Stopwatch.Frequency : 0.0;
                    Log($"ARCADETRACE 0x{dword:X8} op=0x{(dword >> 16) & 0xff:X2} "
                        + $"param=0x{(dword >> 8) & 0xff:X2} mag=0x{dword & 0xff:X2} "
                        + $"{(ok ? "decoded" : "REJECTED")} +{sinceMs:F0}ms");
                    if (_traceLines == 4000)
                        Log("ARCADETRACE: 4000 lines reached, no more will be logged. The "
                            + "histograms in the shutdown summary still count every command.");
                }
                _prevTraceDword = dword;
                _haveTraceDword = true;
                _lastTraceTicks = now;
            }

            if (!ok)
            {
                // Not a real command. That is NOT the cabinet asking for silence:
                // these protocols leave idle values in the slot between commands,
                // so rejecting one says only "there is nothing new here". Cutting
                // the force on it made every idle dword a total force cut, taken
                // again every poll. What is playing keeps playing and still ends
                // on its own expiry.
                Interlocked.Increment(ref _rejectedCount);
                if (_latch.Constant.Present && now >= _constantUntil)
                    _latch.Constant = default(ArcadeTrigger);
                if (_latch.Constant.Present)
                    Interlocked.Exchange(ref _ffbPacked,
                        PackFfb(ToLsb(_latch.Constant.Strength), now & FfbTimestampMask));
                else
                    Interlocked.Exchange(ref _ffbPacked, 0);
                EmitLivenessFrame();
                return true;
            }

            Interlocked.Increment(ref _commandCount);

            // Merge this command into what is already playing. A command names
            // one effect; the ones it does not name keep running until they
            // expire, because the protocol has no way to say "and stop the
            // others" and the cabinet does not mean it. Clearing them here made
            // the constant and the spring mutually exclusive, so the wheel
            // stepped between them every time the cabinet interleaved a spring
            // or a sine into a run of constants.
            long holdTicks = (Stopwatch.Frequency / 1000L) * CommandHoldMs;
            MergeTrigger(ref _latch.Constant, _scratch.Constant, ref _constantUntil, now, holdTicks);
            MergeTrigger(ref _latch.Spring,   _scratch.Spring,   ref _springUntil,   now, holdTicks);
            MergeTrigger(ref _latch.Friction, _scratch.Friction, ref _frictionUntil, now, holdTicks);
            MergeTrigger(ref _latch.Damper,   _scratch.Damper,   ref _damperUntil,   now, holdTicks);
            MergeTrigger(ref _latch.Periodic, _scratch.Periodic, ref _periodicUntil, now, holdTicks);
            MergeTrigger(ref _latch.Rumble,   _scratch.Rumble,   ref _rumbleUntil,   now, holdTicks);

            // The constant is scalar-shaped and rides the normal force path.
            if (_latch.Constant.Present)
            {
                Interlocked.Exchange(ref _ffbPacked,
                    PackFfb(ToLsb(_latch.Constant.Strength), now & FfbTimestampMask));
            }
            else
            {
                Interlocked.Exchange(ref _ffbPacked, 0);
            }

            // Publish the effect-shaped part only when it genuinely changed: the
            // renderer re-downloads on every new state, and a download resets the
            // engine's per-effect filter state.
            var prev = _snap;
            if (prev == null || !prev.State.SameAs(_latch))
            {
                var copy = new ArcadeFfbState
                {
                    Constant = _latch.Constant,
                    Spring   = _latch.Spring,
                    Friction = _latch.Friction,
                    Damper   = _latch.Damper,
                    Periodic = _latch.Periodic,
                    Rumble   = _latch.Rumble,
                };
                _snap = new StateSnap { State = copy, Ticks = now };
            }
            else
            {
                // Unchanged, but still current: refresh the stamp so the
                // renderer's freshness window does not expire under a held
                // command. A single reference swap keeps that atomic.
                _snap = new StateSnap { State = prev.State, Ticks = now };
            }

            EmitLivenessFrame();
            return true;
        }

        // The frame carries no physics: this block has no RPM, speed or pedal
        // data, and inventing any would be worse than absent. It exists so
        // MeasuredHz reads live, which is what keeps the provider's pause release
        // from muting the arcade force, and so the UI can show the source is up.
        // FrameResampler treats it as a non-physics keepalive, which is correct:
        // telemetry-driven effects genuinely have nothing to run on here.
        /// <summary>Fold one command's trigger into what is playing. A trigger the
        /// command carries replaces its kind and restarts that kind's clock; a
        /// kind the command is silent about keeps running until its own expiry,
        /// then goes quiet on its own.</summary>
        private static void MergeTrigger(ref ArcadeTrigger live, ArcadeTrigger fresh,
                                         ref long until, long now, long holdTicks)
        {
            if (fresh.Present)
            {
                live = fresh;
                until = now + holdTicks;
                return;
            }
            if (live.Present && now >= until) live = default(ArcadeTrigger);
        }

        /// <summary>What the cabinet actually sent, printed once when the source
        /// stops. The counters and histograms have always been collected and were
        /// never readable anywhere, so which opcodes a cabinet really uses, how
        /// hard it pushes and how much of its stream we reject were all matters
        /// of argument rather than record. One paragraph at shutdown ends
        /// that.</summary>
        private void LogCommandSummary()
        {
            long polls = PollCount, cmds = CommandCount, rej = RejectedCount;
            if (polls == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.Append("Arcade command summary for game id ").Append(_gameId)
              .Append(": polls ").Append(polls)
              .Append(", decoded ").Append(cmds)
              .Append(", rejected ").Append(rej);
            if (cmds + rej > 0)
                sb.Append(" (").Append((100.0 * rej / (cmds + rej)).ToString("F1"))
                  .Append("% of commands rejected)");
            sb.Append('.');

            for (int op = 0; op < 256; op++)
            {
                long n = 0; int magLo = -1, magHi = -1;
                for (int m = 0; m < 256; m++)
                {
                    int c = _opMagHistogram[op, m];
                    if (c == 0) continue;
                    n += c;
                    if (magLo < 0) magLo = m;
                    magHi = m;
                }
                if (n == 0) continue;
                sb.Append(" op 0x").Append(op.ToString("X2")).Append(": ").Append(n)
                  .Append(n == 1 ? " command" : " commands")
                  .Append(", mag 0x").Append(magLo.ToString("X2"))
                  .Append(" to 0x").Append(magHi.ToString("X2")).Append('.');
            }
            Log(sb.ToString());
        }

        private void ClearLatch()
        {
            _latch.Constant = default(ArcadeTrigger);
            _latch.Spring   = default(ArcadeTrigger);
            _latch.Friction = default(ArcadeTrigger);
            _latch.Damper   = default(ArcadeTrigger);
            _latch.Periodic = default(ArcadeTrigger);
            _latch.Rumble   = default(ArcadeTrigger);
            _constantUntil = _springUntil = _frictionUntil = 0;
            _damperUntil = _periodicUntil = _rumbleUntil = 0;
        }

        private void EmitLivenessFrame()
        {
            if ((Interlocked.Read(ref _pollCount) % EmitEveryNPolls) != 0) return;
            TelemetryFrame frame = default(TelemetryFrame);
            EmitFrame(frame);
        }

        /// <summary>A decoded 0..1 strength to the signed int16 LSB scale the FFB
        /// pipeline speaks, the same scale the wire tap decodes from HID++ 0x8123,
        /// so downstream sign, scale and smoothing are identical for both.</summary>
        internal short ToLsb(double signed01)
        {
            // The constant is RIGHT as authored. This was flipped once on a wrong
            // reading of the rig and flipped straight back: the reported "menu
            // forces reversed" is the SPRING, which is what Initial D uses for the
            // detents when you turn the wheel through options, and the race force
            // that was already correct is the constant. Flipping here broke the
            // half that worked and left the half that did not.
            //
            // InvertConstantDirection stays as the per-cabinet override.
            double f = signed01 * ForceScale;
            if (InvertConstantDirection) f = -f;
            double scaled = Math.Round(f * 32767.0);
            if (scaled >  32767.0) return  32767;
            if (scaled < -32767.0) return -32767;
            return (short)scaled;
        }

        private static uint ReadDword(byte[] b, int off)
        {
            if (off < 0 || off + 4 > b.Length) return 0;
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        internal static long PackFfb(short value, long ticksMasked)
            => (ticksMasked << 16) | (ushort)value;

        internal static long AgeTicks(long packed, long nowTicksMasked)
            => (nowTicksMasked - (long)((ulong)packed >> 16)) & FfbTimestampMask;

        private void CleanupMap()
        {
            try { if (_view != null) _view.Dispose(); } catch { }
            try { if (_mmf != null) _mmf.Dispose(); } catch { }
            _view = null;
            _mmf = null;
        }

        private void Log(string msg)
        {
            var logger = Logger;
            if (logger == null) return;
            try { logger("Arcade: " + msg); } catch { }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);
    }
}
