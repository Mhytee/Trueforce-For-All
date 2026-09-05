// Reads the RENDERED force feedback a publishing build of FFBArcadePlugin would
// have sent to the wheel, out of its shared block.
//
// WHAT MAKES THIS DIFFERENT FROM THE DIRECT READER. TeknoParrotJvsTelemetrySource
// reads the cabinet IO block and decodes the protocol itself, which needs one
// decoder per game and reaches only the dozen or so titles whose forces live in
// that block. This reads what that plugin produced, which covers about ninety
// games including the many that read game memory instead.
//
// And it carries something the direct route cannot: the USER'S OWN TUNING. The
// publisher captures at the last moment before the device, so min and max force,
// power mode, the alternative-FFB windows, the doubling options and the per-game
// FeedbackLength have all already been applied. Someone who spent an evening
// tuning their wheel in that plugin's GUI keeps every bit of it.
//
// The values arrive on SDL's scale, which is a gift: levels and coefficients are
// signed 16-bit, exactly what HidppEffectEngine's wire format wants, so nothing
// is rounded through a normalised middle step.
//
// LICENSING BOUNDARY, DELIBERATE. FFBArcadePlugin is GPL-3.0 and this project is
// GPL-2.0-only, which cannot be combined into one work. They are separate
// programs in separate processes: theirs runs inside the game, ours inside
// SimHub, and the only thing crossing is this fixed-layout block of numbers. No
// code, headers or linkage are shared in either direction, and that separation is
// a requirement rather than an implementation detail. Do not "simplify" this by
// pulling any of their source in.
//
// The block is a seqlock: the writer makes the sequence odd, writes, then makes
// it even. A reader seeing an odd value, or a different value either side of its
// read, retries.

using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class FfbArcadePluginSource : TelemetrySourceBase, IArcadeForceSource
    {
        public const string DefaultMapName = "FFBArcadePlugin_Output";

        private const uint Magic = 0x4F424646;   // 'FFBO'
        private const uint SupportedVersion = 2; // v2 publishes rendered SDL effects

        private const int SlotCount = 16;
        private const int SlotBytes = 84;
        private const int HeaderBytes = 64;
        public const int BlockBytes = HeaderBytes + SlotCount * SlotBytes;   // 1408

        // Header offsets.
        private const int OffMagic = 0, OffVersion = 4, OffSeq = 8, OffFrame = 12;
        private const int OffGameId = 16, OffFeedbackLen = 20, OffFlags = 24, OffSlotCount = 28;
        private const int OffRumbleLow = 32, OffRumbleHigh = 36, OffRumbleLen = 40, OffRumbleFrame = 44;

        // Offsets within a slot.
        private const int SKind = 0, SRunning = 4, SLength = 8, SDirection = 12;
        private const int SLevel = 16, SLevel2 = 20, SPeriod = 24, SOffset = 28, SPhase = 32;
        private const int SRightSat = 52, SLeftSat = 56, SRightCoeff = 60, SLeftCoeff = 64;
        private const int SDeadband = 68, SCenter = 72, SUpdatedFrame = 76;

        // Effect kinds, mirroring the publisher's FFB_EFX_*.
        private const uint KConstant = 1, KSine = 2, KTriangle = 3, KSawUp = 4, KSawDown = 5;
        private const uint KSpring = 6, KDamper = 7, KInertia = 8, KFriction = 9, KRamp = 10;

        private const uint FlagRendering = 0x0001;
        private const uint FlagNoDevice = 0x0002;
        private const uint SdlInfinity = 0xFFFFFFFF;

        private const int TickPeriodMs = 2;
        private const int RetryPeriodMs = 500;
        private const int EmitEveryNPolls = 8;

        // The publisher runs on the game's ~60 Hz FFB loop, so about 16 ms a frame.
        // Four missed frames is a stall rather than jitter.
        private const int StaleFrameMs = 70;
        private const double FramePeriodMs = 1000.0 / 60.0;

        private const long FfbTimestampMask = 0xffff_ffff_ffffL;

        public string MapName { get; set; } = DefaultMapName;

        /// <summary>Scales every published value into device force. The publisher's
        /// output is already the user's tuned force, so 1.0 means "exactly what
        /// their wheel would have felt".</summary>
        public double ForceScale { get; set; } = 1.0;

        /// <summary>Log the live effect set whenever it changes, so a run can say
        /// WHICH effect is which rather than leaving it to be inferred from feel.
        /// Off by default; the ARCADEFX access code turns it on.
        ///
        /// This exists because inferring it was tried and got it backwards: "the
        /// menu forces feel reversed" was read as the constant when the race force
        /// is the constant, and the wrong half got flipped.</summary>
        public bool TraceEffects { get; set; }

        private string _lastTrace;
        private int _traceLines;

        /// <summary>Flips the constant-force direction. This is BEFORE
        /// TrueforceDevice.FfbInvertSign, which negates every provider value again,
        /// so there are two flips in the chain and only the wheel settles it.</summary>
        public bool InvertConstantDirection { get; set; }

        /// <summary>Whether to render a damper the publisher sends us. ON by
        /// default: nothing emits a damper unless someone enabled one, so honouring
        /// it respects a choice already made rather than adding one of our own.
        ///
        /// Ours is a passive damper, one steady amount. A commanded damper varies
        /// its coefficient as the game decides, which is a different thing, and is
        /// why the engine's damper gain was calibrated on the effects bench. A
        /// damper arriving here is rendered through that calibrated path.</summary>
        public bool AcceptPublishedDamper { get; set; } = true;

        public Action<string> Logger { get; set; }

        // The publisher looks for this handle to decide whether anyone is
        // listening. Windows releases it if we crash, so a dead reader is detected
        // without either side having to time anything out.
        private EventWaitHandle _readerBeacon;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private Thread _thread;
        private volatile bool _stopping;
        private int _running;

        private readonly byte[] _buf = new byte[BlockBytes];
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        private uint _lastFrame;
        private long _lastFrameChangeTicks;
        private volatile bool _live;

        private readonly ArcadeFfbState _scratch = new ArcadeFfbState();

        private sealed class StateSnap
        {
            public ArcadeFfbState State;
            public long Ticks;
        }
        private volatile StateSnap _snap;

        private long _ffbPacked;

        private long _pollCount, _readCount, _tornCount;
        private int _publishedGameId;
        private uint _feedbackLengthMs;
        private bool _loggedOpen, _loggedStale, _loggedVersion, _loggedDoubleDrive, _loggedNoDevice;
        private volatile bool _publisherRendering;

        public override string Name => "FFBArcadePlugin output";
        public override bool IsEnhanced => true;

        // A cabinet publishes force, not telemetry. Saying so keeps the SimHub
        // overlay off these frames: it would otherwise paint the last game's
        // cached flags onto them, and an ABS flag left set by a sim that has
        // since closed would buzz for the whole arcade session with no speed
        // anywhere to end it.
        public override bool PublishesPhysics => false;
        public override bool IsRunning => _running != 0;

        /// <summary>True while the publisher's frame counter is advancing: a real
        /// liveness reading rather than the "did any byte change" heuristic the
        /// direct reader has to use.</summary>
        public override bool IsSessionActive => _live;

        public override bool HasAuthoritativeSessionState => false;

        public bool MapOpen => _view != null;
        public int PublishedGameId => _publishedGameId;
        public long PollCount => Interlocked.Read(ref _pollCount);
        public long ReadCount => Interlocked.Read(ref _readCount);
        public long TornReadCount => Interlocked.Read(ref _tornCount);

        /// <summary>True when the game has genuinely handed the wheel over: we are
        /// reading a live block AND the publisher says it is not driving the device
        /// itself.
        ///
        /// This is what frees the wheel's lights and screen. Everywhere else that
        /// has to answer "is the game writing to this wheel" measures it off the
        /// wire and can only ever prove a quiet moment. Here it is structural: in
        /// publish-only mode the game's submissions are suppressed inside its own
        /// process, so there is nothing on the pipe to measure, and the one case
        /// where that stops being true (nobody listening, so it went back to
        /// driving) is the flag we are reading.</summary>
        public bool HandedOver => _live && !_publisherRendering;

        /// <summary>The event a reader holds so the publisher knows it is there:
        /// the mapping name with "_Reader" appended, matching
        /// SharedOutputReaderEventName in the publisher.</summary>
        public static string ReaderEventName(string mapName)
            => (string.IsNullOrEmpty(mapName) ? DefaultMapName : mapName) + "_Reader";

        /// <summary>True when a compatible publisher block exists right now. How
        /// the plugin decides to prefer this over decoding the cabinet itself.</summary>
        public static bool Present(string mapName)
        {
            foreach (var n in Candidates(mapName))
            {
                try
                {
                    using (var mmf = MemoryMappedFile.OpenExisting(n, MemoryMappedFileRights.Read))
                    using (var view = mmf.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read))
                    {
                        if (view.ReadUInt32(OffMagic) == Magic) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static string[] Candidates(string mapName)
        {
            string n = string.IsNullOrEmpty(mapName) ? DefaultMapName : mapName;
            return new[] { n, "Local\\" + n, "Global\\" + n };
        }

        public override void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            _stopping = false;

            // Raised before the poll thread and before the block is open, so a
            // publisher coming up later sees a reader immediately rather than
            // spending its fallback timeout driving the wheel.
            try
            {
                bool created;
                _readerBeacon = new EventWaitHandle(
                    false, EventResetMode.ManualReset, ReaderEventName(MapName), out created);
            }
            catch (Exception ex)
            {
                _readerBeacon = null;
                Log("could not raise the reader beacon (" + ex.GetType().Name + "): the game will keep "
                    + "driving the wheel itself alongside us.");
            }

            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "FfbArcadePluginSource",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            Log("started; waiting for a publishing FFBArcadePlugin build.");
        }

        public override void Stop()
        {
            _stopping = true;
            try { var t = _thread; if (t != null) t.Join(2000); } catch { }
            _thread = null;
            CleanupMap();
            _publisherRendering = false;
            try { if (_readerBeacon != null) _readerBeacon.Dispose(); } catch { }
            _readerBeacon = null;
            Interlocked.Exchange(ref _ffbPacked, 0);
            _snap = null;
            _live = false;
            _loggedOpen = false;
            _loggedStale = false;
            _loggedVersion = false;
            _loggedDoubleDrive = false;
            _loggedNoDevice = false;
            Interlocked.Exchange(ref _running, 0);
        }

        public short? TryGetFreshFfbTarget(int maxAgeMs)
        {
            long packed = Interlocked.Read(ref _ffbPacked);
            if (packed == 0) return null;
            long now = _sw.ElapsedTicks & FfbTimestampMask;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if (((now - (long)((ulong)packed >> 16)) & FfbTimestampMask) > maxAgeTicks) return null;
            return (short)(packed & 0xffff);
        }

        public void ApplyTuning(double forceScale, bool invertDirection, bool acceptDamper)
        {
            ForceScale = forceScale;
            InvertConstantDirection = invertDirection;
            AcceptPublishedDamper = acceptDamper;
        }

        public void ClearLastFfbTarget() => Interlocked.Exchange(ref _ffbPacked, 0);

        public ArcadeFfbState TryGetFreshState(int maxAgeMs)
        {
            var s = _snap;
            if (s == null) return null;
            long maxAgeTicks = (Stopwatch.Frequency / 1000L) * maxAgeMs;
            if ((_sw.ElapsedTicks - s.Ticks) > maxAgeTicks) return null;
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
                    if (_view == null) { if (!TryOpen()) periodMs = RetryPeriodMs; }
                    else { if (!PollOnce()) periodMs = RetryPeriodMs; }

                    nextTickMs += periodMs;
                    long sleepMs = nextTickMs - sw.ElapsedMilliseconds;
                    if (sleepMs < 0) { nextTickMs = sw.ElapsedMilliseconds + periodMs; sleepMs = 0; }
                    if (sleepMs > 0) Thread.Sleep((int)sleepMs);
                }
            }
            catch (Exception ex)
            {
                Log("poll loop stopped: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                CleanupMap();
                TimeEndPeriod(1);
            }
        }

        private bool TryOpen()
        {
            foreach (var name in Candidates(MapName))
            {
                MemoryMappedFile mmf = null;
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                    var view = mmf.CreateViewAccessor(0, BlockBytes, MemoryMappedFileAccess.Read);

                    if (view.ReadUInt32(OffMagic) != Magic) { view.Dispose(); mmf.Dispose(); continue; }

                    uint version = view.ReadUInt32(OffVersion);
                    if (version != SupportedVersion)
                    {
                        // Refuse rather than guess: a layout we do not know would be
                        // read as force.
                        if (!_loggedVersion)
                        {
                            _loggedVersion = true;
                            Log($"found a publisher block with layout version {version}, but this build "
                                + $"understands version {SupportedVersion}. Ignoring it.");
                        }
                        view.Dispose(); mmf.Dispose();
                        return false;
                    }

                    _mmf = mmf;
                    _view = view;
                    _lastFrame = 0;
                    _lastFrameChangeTicks = _sw.ElapsedTicks;
                    if (!_loggedOpen) { _loggedOpen = true; Log($"opened {name}."); }
                    return true;
                }
                catch
                {
                    if (mmf != null) { try { mmf.Dispose(); } catch { } }
                }
            }
            return false;
        }

        private bool PollOnce()
        {
            uint seq1, seq2;
            try
            {
                // Seqlock read. An odd sequence means mid-update; a changed one
                // across the body means it updated underneath us. Either way a torn
                // frame here would be a force nobody commanded.
                seq1 = _view.ReadUInt32(OffSeq);
                if ((seq1 & 1) != 0) { Interlocked.Increment(ref _tornCount); return true; }
                _view.ReadArray(0, _buf, 0, BlockBytes);
                seq2 = _view.ReadUInt32(OffSeq);
            }
            catch
            {
                CleanupMap();
                _live = false;
                Interlocked.Exchange(ref _ffbPacked, 0);
                _snap = null;
                return false;
            }

            Interlocked.Increment(ref _pollCount);
            if (seq1 != seq2) { Interlocked.Increment(ref _tornCount); return true; }
            Interlocked.Increment(ref _readCount);

            long now = _sw.ElapsedTicks;
            uint frame = U32(OffFrame);
            if (frame != _lastFrame) { _lastFrame = frame; _lastFrameChangeTicks = now; }

            long staleTicks = (Stopwatch.Frequency / 1000L) * StaleFrameMs;
            bool live = (now - _lastFrameChangeTicks) <= staleTicks;
            if (live != _live)
            {
                _live = live;
                if (!live && !_loggedStale)
                {
                    _loggedStale = true;
                    Log("the publisher stopped advancing its frame counter; holding force off.");
                }
                else if (live && _loggedStale)
                {
                    _loggedStale = false;
                    Log("the publisher is running again.");
                }
            }

            _publishedGameId = (int)U32(OffGameId);
            _feedbackLengthMs = U32(OffFeedbackLen);

            // The publisher opened no haptic device, so it will never capture
            // anything and every slot stays zero forever. Worth saying loudly: the
            // game runs, this block ticks, and the wheel is simply dead with
            // nothing else anywhere to explain it.
            uint flags = U32(OffFlags);
            _publisherRendering = (flags & FlagRendering) != 0;
            if ((flags & FlagNoDevice) != 0 && !_loggedNoDevice)
            {
                _loggedNoDevice = true;
                Log("the game found NO force feedback device to open, so it is publishing an empty block "
                    + "and the wheel will stay silent. Check DeviceGUID in that game's FFBPlugin.ini "
                    + "matches your wheel.");
            }

            // If the publisher is submitting to the device while we are reading,
            // our beacon did not reach it and the wheel is driven from both ends.
            // Both edges, not just the first. This logged once per session, which
            // meant a line in the log could not be told apart from a startup
            // transient that had already resolved, and that is the whole question
            // it exists to answer: a game that fell back before we opened the
            // block recovers on its own within a couple of hundred milliseconds,
            // while one whose beacon never arrives fights us all session.
            bool doubleDriving = (flags & FlagRendering) != 0;
            if (doubleDriving != _loggedDoubleDrive)
            {
                _loggedDoubleDrive = doubleDriving;
                Log(doubleDriving
                    ? "the game is ALSO driving the wheel directly (its reader beacon check failed), so "
                      + "two forces are fighting. If this is not followed by a 'handed the wheel over' "
                      + "line within a second or so, check that SimHub and the game run as the same "
                      + "Windows user."
                    : "the game has handed the wheel over and stopped driving it directly.");
            }

            if (!live)
            {
                Interlocked.Exchange(ref _ffbPacked, 0);
                _snap = null;
                EmitLivenessFrame();
                return true;
            }

            if (TraceEffects) TraceLiveEffects();

            BuildState(frame);
            EmitLivenessFrame();
            return true;
        }

        private void BuildState(uint frame)
        {
            _scratch.Constant = default(ArcadeTrigger);
            _scratch.Spring   = default(ArcadeTrigger);
            _scratch.Friction = default(ArcadeTrigger);
            _scratch.Damper   = default(ArcadeTrigger);
            _scratch.Periodic = default(ArcadeTrigger);
            _scratch.Inertia  = default(ArcadeTrigger);
            _scratch.Ramp     = default(ArcadeTrigger);
            _scratch.Rumble   = default(ArcadeTrigger);

            long now = _sw.ElapsedTicks;
            bool haveConstant = false;

            for (int i = 0; i < SlotCount; i++)
            {
                int b = HeaderBytes + i * SlotBytes;
                uint kind = U32(b + SKind);
                if (kind == 0) continue;
                if (U32(b + SRunning) == 0) continue;      // downloaded but never started
                if (Expired(b, frame)) continue;

                switch (kind)
                {
                    case KConstant:
                    {
                        // SDL names a direction by where the force COMES FROM, so
                        // dir[0] = -1 (from the left) drives the wheel RIGHT. The
                        // level itself is always non-negative here.
                        int dir = I32(b + SDirection);
                        double level = I32(b + SLevel) / 32767.0;
                        double signed = (dir < 0 ? 1.0 : -1.0) * level;
                        _scratch.Set(ArcadeTrigger.Constant(signed));
                        haveConstant = true;
                        break;
                    }

                    case KSpring:
                    case KDamper:
                    case KFriction:
                    case KInertia:
                    {
                        if (kind == KDamper && !AcceptPublishedDamper) break;
                        var k = kind == KSpring ? ArcadeTriggerKind.Spring
                              : kind == KDamper ? ArcadeTriggerKind.Damper
                              : kind == KFriction ? ArcadeTriggerKind.Friction
                              : ArcadeTriggerKind.Inertia;
                        // Coefficients and centre are SDL's signed 16-bit;
                        // saturations and deadband are its unsigned 16-bit.
                        _scratch.Set(ArcadeTrigger.ConditionFull(k,
                            I32(b + SLeftCoeff) / 32767.0,
                            I32(b + SRightCoeff) / 32767.0,
                            I32(b + SLeftSat) / 65535.0,
                            I32(b + SRightSat) / 65535.0,
                            I32(b + SDeadband) / 65535.0,
                            I32(b + SCenter) / 32767.0));
                        break;
                    }

                    case KSine:
                    case KTriangle:
                    case KSawUp:
                    case KSawDown:
                    {
                        var k = kind == KSine ? ArcadeTriggerKind.Sine
                              : kind == KTriangle ? ArcadeTriggerKind.Triangle
                              : kind == KSawUp ? ArcadeTriggerKind.SawtoothUp
                              : ArcadeTriggerKind.SawtoothDown;
                        // SDL's phase is hundredths of a degree, 0..36000.
                        _scratch.Set(ArcadeTrigger.Periodic(k,
                            I32(b + SLevel) / 32767.0,
                            (int)U32(b + SPeriod),
                            I32(b + SOffset) / 32767.0,
                            (U32(b + SPhase) % 36000u) / 36000.0));
                        break;
                    }

                    default:
                        break;   // ramp and left/right are not rendered here
                }
            }

            // Rumble, but only when this cabinet has no force feedback of its own.
            //
            // For any game that emits force, the rumble arguments ARE that force
            // re-encoded for someone playing on a gamepad, with direction carried
            // by which motor. Rendering both would deliver the same signal twice.
            // It is only independent information on cabinets that never call a
            // force trigger at all, and there it is the WHOLE signal: ten of the
            // supported games drive nothing but these two motors.
            //
            // These are intensities, not frequencies, despite SDL naming them
            // low_frequency and high_frequency: they are two fixed motors, a large
            // mass that rumbles low and a small one that buzzes high. What they
            // become on a wheel is therefore ours to choose rather than theirs.
            if (!haveConstant && !_scratch.AnyForceShaped)
            {
                uint rlow = U32(OffRumbleLow), rhigh = U32(OffRumbleHigh);
                uint rframe = U32(OffRumbleFrame);
                bool rumbleFresh = frame >= rframe
                    && (frame - rframe) * FramePeriodMs <= StaleFrameMs;
                if (rumbleFresh && (rlow != 0 || rhigh != 0))
                    _scratch.Set(ArcadeTrigger.Rumble(rlow / 65535.0, rhigh / 65535.0));
            }

            if (haveConstant)
            {
                Interlocked.Exchange(ref _ffbPacked,
                    PackFfb(ToLsb(_scratch.Constant.Strength), now & FfbTimestampMask));
            }
            else
            {
                Interlocked.Exchange(ref _ffbPacked, 0);
            }

            var prev = _snap;
            if (prev == null || !prev.State.SameAs(_scratch))
            {
                _snap = new StateSnap { State = Copy(_scratch), Ticks = now };
            }
            else
            {
                _snap = new StateSnap { State = prev.State, Ticks = now };
            }
        }

        // The reference relies on the device expiring an effect after its length,
        // which is where the per-game FeedbackLength does its work: at 80 ms a
        // force decays almost at once when the game goes quiet, at 5000 ms it is
        // effectively held. Reproducing that here is what keeps the feel the user
        // tuned, and it is also what stops a stale slot playing forever.
        private bool Expired(int slotBase, uint frame)
        {
            uint length = U32(slotBase + SLength);
            if (length == SdlInfinity || length == 0) return false;
            uint updated = U32(slotBase + SUpdatedFrame);
            double sinceMs = (frame >= updated ? frame - updated : 0) * FramePeriodMs;
            return sinceMs > length;
        }

        private static ArcadeFfbState Copy(ArcadeFfbState s) => new ArcadeFfbState
        {
            Constant = s.Constant,
            Spring   = s.Spring,
            Friction = s.Friction,
            Damper   = s.Damper,
            Periodic = s.Periodic,
            Inertia  = s.Inertia,
            Ramp     = s.Ramp,
            Rumble   = s.Rumble,
        };

        private void EmitLivenessFrame()
        {
            if ((Interlocked.Read(ref _pollCount) % EmitEveryNPolls) != 0) return;
            TelemetryFrame frame = default(TelemetryFrame);
            EmitFrame(frame);
        }

        /// <summary>One line per CHANGE of the live effect set, naming each effect
        /// and its signed value. Turning the wheel through a menu and then driving
        /// a corner produces two distinguishable lines, which is all it takes to
        /// say which effect carries which feel.</summary>
        private void TraceLiveEffects()
        {
            var st = _scratch;
            var sb = new System.Text.StringBuilder("ARCADEFX");
            Append(sb, "constant", st.Constant, true);
            Append(sb, "spring",   st.Spring,   false);
            Append(sb, "damper",   st.Damper,   false);
            Append(sb, "friction", st.Friction, false);
            Append(sb, "periodic", st.Periodic, true);
            Append(sb, "rumble",   st.Rumble,   false);
            if (sb.Length == "ARCADEFX".Length) sb.Append(" (nothing live)");
            string line = sb.ToString();
            if (line == _lastTrace) return;
            _lastTrace = line;
            if (_traceLines >= 3000) return;
            if (++_traceLines == 3000)
            {
                Log("ARCADEFX: 3000 lines reached, no more will be logged.");
                return;
            }
            Log(line);
        }

        private static void Append(System.Text.StringBuilder sb, string name,
                                   ArcadeTrigger t, bool signed)
        {
            if (!t.Present) return;
            sb.Append(' ').Append(name).Append('=');
            // The sign is the whole point for a constant or a periodic, so it is
            // printed explicitly rather than left to a leading minus that is easy
            // to miss in a wall of log.
            if (signed) sb.Append(t.Strength >= 0 ? "RIGHT" : "LEFT").Append(':');
            sb.Append(t.Strength.ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture));
            if (t.CoeffLeft != 0 || t.CoeffRight != 0)
                sb.Append("(coef ").Append(t.CoeffLeft.ToString("F2",
                        System.Globalization.CultureInfo.InvariantCulture))
                  .Append('/').Append(t.CoeffRight.ToString("F2",
                        System.Globalization.CultureInfo.InvariantCulture)).Append(')');
        }

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
            if (scaled > 32767.0) return 32767;
            if (scaled < -32767.0) return -32767;
            return (short)scaled;
        }

        private uint U32(int off)
            => (uint)(_buf[off] | (_buf[off + 1] << 8) | (_buf[off + 2] << 16) | (_buf[off + 3] << 24));

        private int I32(int off) => unchecked((int)U32(off));

        private static long PackFfb(short value, long ticksMasked)
            => (ticksMasked << 16) | (ushort)value;

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
            try { logger("Arcade (published): " + msg); } catch { }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);
    }
}
