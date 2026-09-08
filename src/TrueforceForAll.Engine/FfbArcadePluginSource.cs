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

        // How long the steering force takes to release once it lapses, instead of
        // being cut.
        //
        // Measured on the rig 2026-09-07: this game leaves gaps of 5017 ms and
        // 6067 ms with no steering command at all, during long drifts and with the
        // input held steady. No hold length is right for a gap that size. Holding
        // longer means several more seconds of a stale force that stopped matching
        // the corner, and dropping at the end of the hold is a cliff. The cliff is
        // what "it cut all force" is: the wheel carries a full force for five
        // seconds and then has none in a single frame.
        //
        // Releasing over a third of a second is neither. The wheel goes light the
        // way it would if the game had wound the force down itself, and the hold
        // becomes a question of taste rather than the thing standing between the
        // driver and a jolt.
        private const double SteeringReleaseMs = 350.0;

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

        /// <summary>How long the STEADY forces keep acting after the cabinet
        /// stops sending them: the constant, the spring, the damper, the friction
        /// and the inertia. 0 means "however long the publisher stamped on it",
        /// which is that game's FeedbackLength.
        ///
        /// Split from the waveform hold below because the two want opposite
        /// things and the reference plugin gives them one number. Measured here
        /// 2026-09-06: the constant arrives with FeedbackLength, while a sine
        /// arrives with its own PERIOD (6 ms and 49 ms on the rig), so the one
        /// setting a user can reach already governs only half of what its name
        /// suggests, and nothing at all reaches the buzz.
        ///
        /// It covers the conditions as well as the constant because the cabinet
        /// sends one command per frame, so the spring is displaced on the same
        /// schedule the constant is. Holding one while the other lapsed produced
        /// two collapses a few seconds apart rather than one, which is harder to
        /// reason about than either extreme (rig, 2026-09-07).</summary>
        public int SteadyHoldMs { get; set; }

        /// <summary>The same for the waveform effects: sine, triangle and both
        /// sawtooths. 0 leaves them at the length the game asks for, which for
        /// this protocol is a single cycle.</summary>
        public int VibrationHoldMs { get; set; }

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

            // Engine data for Initial D 8 comes from the game's own memory, because the arcade FFB
            // stream carries forces only. Read only, and it stays silent unless the chain resolves.

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
            _liftSmoothed = 0;
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
                Log("the game found NO force feedback device to open, so it is sending nothing "
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
                // No release here: the block going quiet is the game leaving, and
                // a wheel that fades on the way out would be reproducing a force
                // whose source is already gone.
                Interlocked.Exchange(ref _ffbPacked, 0);
                _releaseFrom = 0.0;
                _releasing = false;
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
            uint constantStamp = 0;

            for (int i = 0; i < SlotCount; i++)
            {
                int b = HeaderBytes + i * SlotBytes;
                uint kind = U32(b + SKind);
                if (kind == 0) continue;
                // Read before the running and expiry tests, both of which skip the
                // slot we most want to time.
                if (kind == KConstant) constantStamp = U32(b + SUpdatedFrame);
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

            SampleCabinetWord(constantStamp);
            NoteConstantLapsed(haveConstant, frame);

            if (haveConstant)
            {
                _releaseFrom = _scratch.Constant.Strength;
                _releasing = false;
                Interlocked.Exchange(ref _ffbPacked,
                    PackFfb(ToLsb(_scratch.Constant.Strength), now & FfbTimestampMask));
            }
            else if (_releaseFrom != 0.0)
            {
                if (!_releasing) { _releasing = true; _releaseStartTicks = now; }
                double sinceMs = (now - _releaseStartTicks) * 1000.0 / Stopwatch.Frequency;
                double k = ReleaseScale(sinceMs, SteeringReleaseMs);
                if (k <= 0.0)
                {
                    _releaseFrom = 0.0;
                    _releasing = false;
                    Interlocked.Exchange(ref _ffbPacked, 0);
                }
                else
                {
                    // Through ToLsb, not around it, so the weak-force lift winds
                    // down with the force rather than sitting on top of a fading
                    // one and becoming the whole signal at the end.
                    Interlocked.Exchange(ref _ffbPacked,
                        PackFfb(ToLsb(_releaseFrom * k), now & FfbTimestampMask));
                }
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
            NoteSlotLength(slotBase, length);
            // Infinite and unset stay as they are whatever the overrides say. The
            // default centering spring and the friction arrive that way, and an
            // override that expired them would delete an effect the game holds on
            // purpose. Only a slot that already had a finite length is rescaled.
            if (length == SdlInfinity || length == 0) return false;
            length = HeldLength(length, U32(slotBase + SKind), SteadyHoldMs, VibrationHoldMs);
            uint updated = U32(slotBase + SUpdatedFrame);
            double sinceMs = (frame >= updated ? frame - updated : 0) * FramePeriodMs;
            return sinceMs > length;
        }

        /// <summary>Which hold applies to a slot, or 0 to leave the published
        /// length alone. Conditions are deliberately absent: the spring and the
        /// friction stay on whatever the game asked for, so the two controls that
        /// exist each own one thing and nothing owns a slot twice.</summary>
        /// <summary>Says how long a gap in the steering command actually was, on
        /// the reading where the wheel goes slack.
        ///
        /// This is the measurement behind the Steering hold slider. The cabinet
        /// sends one command per frame and a waveform frame displaces a steering
        /// frame, so the steering force lapses whenever a run of waveform frames
        /// outlasts its hold. How long a hold is enough is therefore a question
        /// about THIS game's frame runs, and guessing at it from feel takes a rig
        /// session per guess. The number printed here answers it in one lap.
        ///
        /// Bounded hard: one line a second and forty a session, because the whole
        /// point is a fault that repeats.</summary>
        private void NoteConstantLapsed(bool haveConstant, uint frame)
        {
            if (haveConstant) { _constantWasLive = true; return; }
            if (!_constantWasLive) return;
            _constantWasLive = false;

            if (_lapseLines >= 40) return;
            long ms = _sw.ElapsedMilliseconds;
            if (_lastLapseMs != 0 && ms - _lastLapseMs < 1000) return;
            _lastLapseMs = ms;
            _lapseLines++;

            for (int i = 0; i < SlotCount; i++)
            {
                int b = HeaderBytes + i * SlotBytes;
                if (U32(b + SKind) != KConstant) continue;
                uint updated = U32(b + SUpdatedFrame);
                double ageMs = (frame >= updated ? frame - updated : 0) * FramePeriodMs;
                Log("the steering force just lapsed: " + ageMs.ToString("0")
                    + " ms since the cabinet last sent one, against a hold of "
                    + (SteadyHoldMs > 0
                        ? SteadyHoldMs + " ms (Steady force hold)"
                        : U32(b + SLength) + " ms (the game's own Force linger)")
                    + ". The wheel is slack until the next steering command. A hold longer than "
                    + "the gap printed here would have carried it through."
                    + CabinetWordSummary());
                return;
            }
        }

        private bool _constantWasLive;
        private long _lastLapseMs;
        private int  _lapseLines;

        // WATCHING THE CABINET DIRECTLY, READ ONLY.
        //
        // Everything else in this file reads what the reference plugin PRODUCED.
        // That cannot answer the question that matters here, because a command the
        // game sent and that plugin discarded looks exactly like a command the game
        // never sent: in both cases no slot is updated and the force goes stale.
        //
        // The cabinet's own IO block says which. It is a separate 64 byte mapping
        // that TeknoParrot writes and both plugins read, so opening it read only
        // alongside disturbs nothing. For this protocol the command is int slot 2,
        // and its bytes are opcode in [2], direction in [1], magnitude in [0].
        //
        // The specific suspicion: the reference decoder guards its steering opcode
        // with (ffb[0] > 0x00 && ffb[0] < 0x80), and those two excluded values are
        // exactly where zero force lands in each direction. If the game commands
        // zero while the car slides, that command is dropped and the wheel keeps a
        // stale force until it times out. This says whether that is happening.
        //
        // One honest limit, stated where it will be read: the slot holds a value,
        // not a stream of writes, and nothing clears it between frames. So this
        // counts values and changes, never individual writes, and cannot tell a
        // game that rewrote the same word from one that wrote nothing.
        private const int CabinetFfbSlot = 2;

        private static readonly string[] MapNamesJvs =
        {
            "TeknoParrot_JvsState",
            "Local\\TeknoParrot_JvsState",
            "Global\\TeknoParrot_JvsState",
        };

        private MemoryMappedFile _jvsMmf;
        private MemoryMappedViewAccessor _jvsView;
        private long _jvsNextTryMs;
        private uint _jvsWindowStamp, _jvsLastWord;
        private int _jvsSamples, _jvsChanges, _jvsZeroMagFrames;
        private readonly int[] _jvsOpcodes = new int[256];

        private bool JvsReady()
        {
            if (_jvsView != null) return true;
            long ms = _sw.ElapsedMilliseconds;
            if (ms < _jvsNextTryMs) return false;
            _jvsNextTryMs = ms + 2000;
            for (int i = 0; i < MapNamesJvs.Length; i++)
            {
                try
                {
                    var mmf = MemoryMappedFile.OpenExisting(
                        MapNamesJvs[i], MemoryMappedFileRights.Read);
                    _jvsView = mmf.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read);
                    _jvsMmf = mmf;
                    Log("also reading the cabinet's own controls directly, without writing to them, "
                        + "so a command the FFB Arcade Plugin threw away can be told apart from one "
                        + "the game never sent.");
                    return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>One reading of the cabinet word, counted against the window
        /// since the steering command was last refreshed. The window resets on
        /// every refresh, so whatever it holds when a lapse fires describes exactly
        /// the gap that lapsed.</summary>
        private void SampleCabinetWord(uint constantStamp)
        {
            if (!JvsReady()) return;

            uint w;
            try { w = _jvsView.ReadUInt32(CabinetFfbSlot * 4); }
            catch { return; }

            if (constantStamp != _jvsWindowStamp)
            {
                _jvsWindowStamp = constantStamp;
                _jvsSamples = 0;
                _jvsChanges = 0;
                _jvsZeroMagFrames = 0;
                Array.Clear(_jvsOpcodes, 0, _jvsOpcodes.Length);
            }

            _jvsSamples++;
            if (w != _jvsLastWord) { _jvsChanges++; _jvsLastWord = w; }

            byte opcode = (byte)(w >> 16), magnitude = (byte)w;
            _jvsOpcodes[opcode]++;
            // The two values the reference guard throws away, which are where zero
            // force lands: 0x80 for a force from the left, 0x00 for one from the right.
            if (opcode == 0x04 && (magnitude == 0x00 || magnitude == 0x80)) _jvsZeroMagFrames++;
        }

        /// <summary>What the cabinet was saying during the gap that just lapsed.</summary>
        private string CabinetWordSummary()
        {
            if (_jvsView == null)
                return " The cabinet IO block is not open, so what the game commanded during "
                     + "that gap is not known.";

            var sb = new System.Text.StringBuilder();
            sb.Append(" Cabinet IO over that gap: ").Append(_jvsSamples).Append(" readings, ")
              .Append(_jvsChanges).Append(" value changes, opcodes");
            bool any = false;
            for (int op = 0; op < 256; op++)
            {
                if (_jvsOpcodes[op] == 0) continue;
                sb.Append(any ? ", " : " ").Append("0x").Append(op.ToString("x2"))
                  .Append(" x").Append(_jvsOpcodes[op]);
                any = true;
            }
            if (!any) sb.Append(" none");
            sb.Append(". Readings where the game asked for a steering force of zero, which the "
                    + "reference plugin discards: ").Append(_jvsZeroMagFrames);
            sb.Append(". Last word 0x").Append(_jvsLastWord.ToString("x8")).Append(".");
            return sb.ToString();
        }

        /// <summary>How much of the lapsed force is left after a given time, 1
        /// at the start of the release and 0 at its end. Linear: the release is
        /// short enough that a curve would only be a different kind of guess, and
        /// a straight line is the one shape whose end is unambiguous.</summary>
        internal static double ReleaseScale(double sinceMs, double releaseMs)
        {
            if (releaseMs <= 0.0 || sinceMs >= releaseMs) return 0.0;
            if (sinceMs <= 0.0) return 1.0;
            return 1.0 - sinceMs / releaseMs;
        }

        private double _releaseFrom;
        private bool _releasing;
        private long _releaseStartTicks;

        internal static uint HeldLength(uint published, uint kind, int steadyHoldMs, int vibHoldMs)
        {
            bool steady = kind == KConstant || kind == KSpring || kind == KDamper
                       || kind == KInertia || kind == KFriction || kind == KRamp;
            bool wave = kind == KSine || kind == KTriangle || kind == KSawUp || kind == KSawDown;
            int over = steady ? steadyHoldMs : wave ? vibHoldMs : 0;
            return over > 0 ? (uint)over : published;
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

        /// <summary>What duration each effect kind actually arrives with, said once per kind.
        ///
        /// This answers a question worth settling rather than arguing: the reference plugin has one
        /// FeedbackLength for the whole game, and the suspicion is that it stamps that on every
        /// effect instead of passing through the duration the game asked for. Our side already
        /// honours whatever length each slot carries, so if every kind reports the same number, the
        /// flattening happened upstream and no change here can undo it. If they differ, the game's
        /// own durations are coming through and the shared setting is not the constraint it looks
        /// like. Bounded to one line per kind so it cannot fill a log.</summary>
        private void NoteSlotLength(int slotBase, uint length)
        {
            uint kind = U32(slotBase + SKind);
            if (kind == 0 || kind > 15) return;
            uint mask = 1u << (int)kind;
            if ((_slotLengthNoted & mask) != 0) return;
            _slotLengthNoted |= mask;
            Log("effect kind " + kind + " (" + KindName(kind) + ") arrives with a duration of "
                + (length == SdlInfinity ? "infinite" : length + " ms")
                + ". The game's FeedbackLength setting is " + _feedbackLengthMs
                + " ms; a match on every kind means the reference flattened them.");
        }

        private uint _slotLengthNoted;

        private static string KindName(uint kind)
        {
            switch (kind)
            {
                case KConstant: return "constant";
                case KSine: return "sine";
                case KTriangle: return "triangle";
                case KSawUp: return "saw up";
                case KSawDown: return "saw down";
                case KSpring: return "spring";
                case KDamper: return "damper";
                case KInertia: return "inertia";
                case KFriction: return "friction";
                case KRamp: return "ramp";
                default: return "kind " + kind;
            }
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

        /// <summary>A floor under the steering force, faded in across the first fraction of travel
        /// so it does not step at centre. 0 disables it.
        ///
        /// The reference plugin has its own MinForce and it is the thing that feels wrong here: it
        /// computes level = strength * (MaxForce - MinForce) + MinForce and gates that on the
        /// command being above a hair of nothing, with the sign carried separately. So a force
        /// crossing centre goes from plus the floor, to zero, to minus the floor, and with a floor
        /// of 20 that is a 40 point jump out of 100. Raising or lowering it moves the notch rather
        /// than removing it.
        ///
        /// This one is the same idea without the step: the floor ramps in over a band tied to its own
        /// size, so centre is genuinely zero and everything past the band is lifted. Set the
        /// reference plugin's own MinForce to 0 when using this, or both apply.</summary>
        public double MinForce01 { get; set; }

        /// <summary>The ramp the floor comes in over, as a fraction of the floor itself, with a
        /// hard minimum so a small floor still gets a gentle edge.
        ///
        /// This is the anti-oscillation control, and it is the only one needed. Near zero the
        /// transform's gain is floor/band + (1 - floor), so a narrow band means enormous gain on
        /// tiny signals: at a floor of 0.20 over a band of 0.03 that is seven and a half times, and
        /// a loop with that much gain rings. Tying the band to the floor bounds the gain a little
        /// above two whatever the floor is set to, while leaving the lift on real forces untouched,
        /// which is the whole point of the setting.</summary>
        private const double FloorBandOfFloor = 0.75;
        private const double FloorBandMin = 0.08;

        /// <summary>How quickly the lift follows a change, as a fraction per poll at the 500 Hz
        /// tick. This is what actually stops the oscillation, and it works because no static curve
        /// can.
        ///
        /// The curve has to have gain above one near zero or it does not lift weak forces, which is
        /// the entire point of the setting; and gain above one in a loop with lag can ring. Those
        /// are in direct conflict and widening the ramp only trades one for the other.
        ///
        /// What separates them is not size but FREQUENCY. A force worth lifting is sustained for as
        /// long as the corner lasts. An oscillation reverses several times a second. Smoothing the
        /// LIFT alone, and leaving the game's own force untouched, lets the first through at full
        /// strength while the second averages toward nothing and stops feeding the loop.
        ///
        /// About a 130 ms time constant. Measured against a reversal at eight times a second,
        /// which is what the ring looked like: at 40 ms the lift still followed it to four fifths
        /// of full and kept feeding the loop, and at 130 ms it reaches barely a third. The cost is
        /// that a lift takes about that long to build, which is right anyway. A transient strong
        /// enough to matter needs no lift; only a sustained light force does.</summary>
        private const double LiftFollowPerPoll = 0.015;

        private double _liftSmoothed;

        /// <summary>Apply the floor. Odd about zero, continuous through it, and monotonic, so the
        /// wheel never reverses or jumps as the force changes sign.</summary>
        /// <summary>Lift the weak forces without raising the strong ones.
        ///
        /// This is a TUNING control, not a friction workaround. These cabinets send forces that are
        /// simply too light on a strong wheel, and the useful thing is to raise the bottom of the
        /// range while leaving the top where it is, so the quiet moments can be felt without the
        /// loud ones getting louder. Everything past the ramp is lifted by the floor and then
        /// compressed into what is left below full scale, which is what keeps the top reachable.
        ///
        /// The ramp near zero does two jobs. It keeps centre continuous, so the force does not step
        /// as it changes sign. And it bounds the gain on tiny signals, which is what stops the wheel
        /// oscillating: a floor applied too abruptly turns a small command into a large push, the
        /// wheel moves, the game answers the other way, and the loop sustains itself. The gain near
        /// zero is floor/band + (1 - floor), so tying the band to the floor holds it near two
        /// whatever the floor is set to. The lift on real forces is untouched by any of that.</summary>
        internal static double ApplyMinForce(double f, double min01)
        {
            if (min01 <= 0.0 || f == 0.0) return f;
            if (min01 > 0.95) min01 = 0.95;

            double band = min01 * FloorBandOfFloor;
            if (band < FloorBandMin) band = FloorBandMin;

            double mag = Math.Abs(f);
            double lift = min01 * (mag >= band ? 1.0 : mag / band);
            double outMag = lift + mag * (1.0 - min01);
            if (outMag > 1.0) outMag = 1.0;
            return f < 0 ? -outMag : outMag;
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
            // The lift is smoothed, the game's own force is not. See LiftFollowPerPoll: this is
            // the part that stops the wheel ringing around centre, and it cannot be done inside the
            // curve because the curve has no memory of what the force was doing a moment ago.
            double raw = signed01;
            double lift = ApplyMinForce(raw, MinForce01) - raw;
            _liftSmoothed += (lift - _liftSmoothed) * LiftFollowPerPoll;
            double f = (raw + _liftSmoothed) * ForceScale;
            if (f > 1.0) f = 1.0;
            else if (f < -1.0) f = -1.0;
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
            try { if (_jvsView != null) _jvsView.Dispose(); } catch { }
            try { if (_jvsMmf != null) _jvsMmf.Dispose(); } catch { }
            _jvsView = null;
            _jvsMmf = null;
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
