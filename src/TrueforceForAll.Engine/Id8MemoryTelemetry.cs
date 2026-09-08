// Live engine data for Initial D Arcade Stage 8, read out of the running game.
//
// The game exe has its relocations stripped and a fixed image base, so every address recovered by
// static analysis of the file is valid in memory on every launch. That means there is no scanning
// here at all: five pointer hops from one fixed address land on the player's car state, and rpm,
// gear and speed are then plain reads at known offsets. See memscan/exe-map/EXE-MAP.md.
//
// STRICTLY READ ONLY. This opens the game with PROCESS_VM_READ and calls ReadProcessMemory. It never
// writes to the game, never injects anything, never installs a hook and never patches a byte.
//
// Why the plugin needs this at all: the arcade FFB path gives forces but no telemetry, so effects
// that need engine state (the redline buzz, the engine pulse, rev lights) have nothing to work from.
// The dial rpm plus the car's redline from Id8CarTable is enough to drive them.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrueforceForAll.Core
{
    /// <summary>One sample of what the game knows about the player's car.</summary>
    public struct Id8Sample
    {
        /// <summary>True when the whole chain resolved and the invariants held.</summary>
        public bool Valid;

        /// <summary>Rpm as the needle shows it, in the dial units the redline table is written in.
        /// This is the normalised rpm scaled by the active face's full scale, which is how a gauge
        /// works: the needle sits at the same fraction of its sweep as the engine is of its range.
        /// Confirmed against the owner's own dial reading on the R35 (a 9000 face, so 0.90).</summary>
        public double DialRpm;

        /// <summary>The game's own normalised rpm, 0 to 1 against its 10000 scale.</summary>
        public double RpmNorm;

        /// <summary>The raw internal rpm, which idles at 800 and pins near 8040 at the limiter.
        /// Kept because the relationship between this and the dial is worth logging.</summary>
        public double InternalRpm;

        /// <summary>The value the on-screen meter widget is displaying, when the race HUD is up.</summary>
        public double MeterRpm;

        public int Gear;
        public double SpeedKmh;
        public int CarId;

        /// <summary>Accelerator, 0 to 1. The engine pulse uses this for its load layer.</summary>
        public double Throttle01;

        /// <summary>Brake pedal, 0 to 1.</summary>
        public double Brake01;

        /// <summary>How far the car is sliding, 0 to 1. The game's own figure, the one it scales
        /// tyre squeal by, so it already means what it looks like it means.</summary>
        public double SlipNorm;

        /// <summary>Longitudinal acceleration in metres per second squared, positive forward, from
        /// the game's own single-frame speed delta.</summary>
        public double AccelLongMps2;

        /// <summary>The same quantity measured across our own sampling interval. Immune to the
        /// aliasing that makes the single-frame figure jitter.</summary>
        public double AccelLongMeasured;

        /// <summary>Lateral acceleration in metres per second squared, positive turning RIGHT to
        /// match what every consumer expects. The game stores no such value, so this is speed times
        /// yaw rate, negated for the game's heading convention.</summary>
        public double AccelLatMps2;

        /// <summary>Yaw rate in radians per second. Kept so the lateral sign can be checked against
        /// the steering rather than argued about.</summary>
        public double YawRateRadPerSec;

        /// <summary>Steered front wheel angle, -1 to 1. The independent witness for which way the
        /// car is actually turning.</summary>
        public double SteerNorm;

        /// <summary>Surface coarseness under the car, 0 smooth to 1 harshest. Built from the codes
        /// the game itself assigns each wheel, rather than synthesised from noise.</summary>
        public double SurfaceRumble;

        /// <summary>True on the reading where the car first hits something, wall or car. Detected
        /// here rather than taken from the technique bits: the game names a wall hit in its
        /// technique table but never awards one in this build, so the contact flags are the only
        /// real source.</summary>
        public bool WallHitStarted;

        /// <summary>True when the thing hit was another car rather than scenery.</summary>
        public bool HitWasAnotherCar;

        /// <summary>A button was pressed this reading, outside a race. The cabinet has no keyboard
        /// and its menus are driven from the wheel, so this is someone moving through the options.
        /// Which bit means what is not established, so the bits themselves are carried out.</summary>
        public bool MenuButtonPressed;

        /// <summary>The bits that went down, for working out which button is which.</summary>
        public uint MenuButtonBits;

        /// <summary>The screen changed, which outside a race is what confirming a choice does.</summary>
        public bool SceneChanged;

        /// <summary>Which screen the game is on. 5 is the race; the rest are not yet identified.
        ///
        /// Exposed so the plugin can tell a race from everything else without re-reading memory.
        /// Rewriting the leaderboards the instant a run finishes lands DURING the results, which is
        /// the one moment the player is reading what they just did; the boards should change when
        /// they leave it.
        ///
        /// No initialiser: Id8Sample is a struct, so it defaults to 0. That reads as "not the race",
        /// which is the safe direction here. A deferred rewrite that fires slightly early because
        /// the scene could not be read costs nothing; one that never fires would leave the player
        /// looking at a target they have already beaten.</summary>
        public int SceneId;

        /// <summary>True while the game's own clock is frozen, which is its pause. Read rather
        /// than inferred from focus or from telemetry going quiet, both of which guess.</summary>
        public bool Paused;

        /// <summary>The techniques the game just scored, as a bitfield of one-frame pulses: 4 wall
        /// hit, 8 brake, 11 gutter drop, 14 shortcut, 15 blind attack, 16 in-wheel lift. These are
        /// EDGE events, set once per zone, so they must be caught rather than polled.</summary>
        public uint TechniqueFlags;

        /// <summary>Fraction of the car's top speed, 0 to 1.</summary>
        public double SpeedFraction;

        /// <summary>Gear count read live, rather than taken from the table. A tuned gearbox is the
        /// case where the two could differ.</summary>
        public int LiveGearCount;

        /// <summary>True when the car is in manual. The game has both.</summary>
        public bool ManualGearbox;

        /// <summary>True when no wheel is touching the ground. Iroha's hairpins and the jumps put
        /// the car in the air often enough for this to matter, and it also stops the traction-loss
        /// effect screaming at a slip angle measured while nothing is gripping.</summary>
        public bool Airborne;

        /// <summary>How hard the car just hit something, in g, or 0 for no impact. The game keeps
        /// its own impact figures, so this does not have to be inferred from an acceleration spike
        /// sampled at the wrong moment, which is why crash strength did not track crash severity.</summary>
        public double ImpactG;

        /// <summary>Body sideslip in degrees: the angle between where the car points and where it
        /// is going. Signed, positive one way round. This is the measurement a traction-loss effect
        /// most wants, because a degree of it means the same thing on every car.</summary>
        public double SideslipDeg;

        /// <summary>The game's slip split across its two axles. It models a single track, so there
        /// is one slip figure weighted two ways rather than four independent tyres.</summary>
        public double SlipAxleA, SlipAxleB;

        /// <summary>The car flag word. Bit 20 selects the tuned tachometer face.</summary>
        public uint CarFlags;

        /// <summary>True when the tuned face is active, so the tuned redline applies.</summary>
        public bool TunedFace;

        /// <summary>True while the rev limiter is cutting.</summary>
        public bool AtLimiter;

        /// <summary>The car this CarID names, or null when it is not in the table.</summary>
        public Id8Car Car;

        /// <summary>True when the session exists, which it does in the menus and the garage as well
        /// as in a race. Identity below is readable whenever this holds.</summary>
        public bool SessionValid;

        /// <summary>The player's name from their card, empty for a guest. This is a Japanese game
        /// and the name is Shift-JIS, so it is very often katakana.</summary>
        public string PlayerName;

        /// <summary>The same name in plain ASCII, for the wheel's panel, which cannot draw
        /// Japanese. Katakana is romanised; anything else that will not fit is dropped.</summary>
        public string PlayerNameAscii;

        /// <summary>The player's team name, empty when they have none.</summary>
        public string TeamName;

        /// <summary>True once a card has been swiped. Until then the game fills the record with its
        /// own placeholder, whose name romanises to something that looks like a fault rather than
        /// like a driver, so nothing about the player is worth showing yet.</summary>
        public bool HasCard;

        /// <summary>The card id reads as a real one. Kept SEPARATE from the name witness because
        /// HasCard is the OR of the two and cannot answer "a card went in but the profile has not
        /// loaded yet", which is the moment a display would want to say so.</summary>
        public bool CardIdPresent;

        /// <summary>A real driver name is loaded, as opposed to the game's own placeholder.</summary>
        public bool PlayerNamed;

        /// <summary>The raw card id, carried out so the log can say what it actually read rather
        /// than only what was concluded from it.</summary>
        public int CardId;

        /// <summary>The car selected in the garage, which is what identifies the car outside a
        /// race. Null when it cannot be read or is not in the table.</summary>
        public Id8Car GarageCar;

        /// <summary>True while a race exists. False in the menus and the garage, where the timing
        /// below is meaningless but the identity above still reads.</summary>
        public bool InRace;

        /// <summary>Milliseconds since GO. Negative through the countdown.</summary>
        public int RaceElapsedMs;

        /// <summary>Seconds left on the course clock, which is what this game runs on instead of a
        /// lap limit.</summary>
        public int TimeLeftSeconds;

        /// <summary>Laps finished so far, and the course's total. Zero total means a sprint.</summary>
        public int LapsCompleted, TotalLaps;

        /// <summary>The last completed lap and the best of the session, in milliseconds. Timed from
        /// the game's own lap counter against its race clock, because SimHub has no idea this game
        /// exists and leaves its own lap fields at zero.</summary>
        public int LastLapMs, BestLapMs;

        /// <summary>True on the reading where a lap time lands, which is what a display waits
        /// for. One frame wide in the game, so it is latched rather than polled.</summary>
        public bool LapJustCompleted;

        /// <summary>How far around the lap the car is, 0 to 1.</summary>
        public double CourseFraction;

        /// <summary>Metres ahead of the opponent. Negative means behind.</summary>
        public double GapMetres;

        /// <summary>The course's own name, from the table in the exe.</summary>
        public string CourseName;

        /// <summary>The game mode, from session + 0x8. Time Attack is 2. Zero when unknown.
        /// A lap only belongs on a time-trial leaderboard if it was set in Time Attack.</summary>
        public int GameMode;

        /// <summary>Course 0..15 and direction 0..1 as the game indexes its boards, or -1 when
        /// there is no course. Every leaderboard in the game is keyed on
        /// slot = direction + 2 * course, so these are what a submission and a board write both
        /// need. Direction 0 is downhill, confirmed on the cabinet.
        ///
        /// Set to -1 at construction rather than defaulting to 0, because 0 is a real course and a
        /// real direction: a zeroed sample must not read as "Akina Lake, downhill".</summary>
        public int CourseId, Direction;

        /// <summary>3, 2 or 1 while the lights are counting down, 0 once the race is running, and
        /// -1 when there is no countdown to show. The race clock runs NEGATIVE before GO, which is
        /// what makes this readable without watching the screen.</summary>
        public int CountdownSeconds;

        /// <summary>True for the moment just after GO, so a display can say so.</summary>
        public bool JustStarted;

        /// <summary>True once the player has crossed the goal.</summary>
        public bool Finished;

        /// <summary>Did the player win? Null when the race was not decided at the goal, which
        /// includes running out of time.</summary>
        public bool? Won;

        /// <summary>True only when the race ended by crossing the goal. False when the clock ran
        /// out, which is a different ending and not a placing.</summary>
        public bool ReachedGoal;

        /// <summary>The raw fields the result is judged from. Their meanings are only likely in the
        /// map, so they are carried out whole and logged rather than trusted silently.</summary>
        public int RaceState, ActorRank, ActorState, ResultCode, EndReason, FinishTimeMs;

        /// <summary>The per-section times, filled in as the run passes each checkpoint.</summary>
        public int[] SectionTimes;

        /// <summary>The sum of <see cref="SectionTimes"/>, which is THE official time.
        ///
        /// Not <see cref="FinishTimeMs"/>. Measured on the cabinet: a run whose elapsed clock read
        /// 202914 was stored and displayed by the game as 202918, and the section sum was 202918
        /// exactly. The result routine at 0x008d7150 never reads the clock; it sums this array and
        /// stores the total, so per-section rounding is where the few milliseconds go. Submitting
        /// the clock instead would make every one of our times a shade fast.</summary>
        public int SectionSumMs;
    }

    /// <summary>Holds the reader for one arcade telemetry source and stamps its values onto the
    /// frames that source emits. Both arcade routes use this, so whichever one wins, the engine
    /// data is the same.</summary>
    public sealed class Id8FrameFiller : IDisposable
    {
        // Arcade polls are 2 ms apart, so this retries about twice a second while the game is
        // absent or in its menus. Cheap enough to leave running, slow enough not to churn.
        private const long RetryEveryNPolls = 250;

        private readonly Id8MemoryTelemetry _reader;
        private long _lastAttempt = long.MinValue / 2;

        public Id8FrameFiller(Action<string> log)
        {
            _reader = new Id8MemoryTelemetry { Log = log };
        }

        /// <summary>Take the techniques scored since the last call, clearing them. They are
        /// one-frame pulses in the game, so the reader latches them and this drains the latch.</summary>
        public uint TakeTechniques() { return _reader.TakeTechniques(); }

        /// <summary>Take a menu action since the last call: 2 when a choice was confirmed, 1 when
        /// the options were moved through, 0 for neither. Latched, because we sample far faster
        /// than anything that would play a haptic for it.</summary>
        public int TakeMenuAction()
        {
            int v = _menuAction;
            _menuAction = 0;
            return v;
        }

        private int _menuAction;

        /// <summary>Did the car just start touching a wall? Latched by the filler, because the edge
        /// lasts one reading and a display polls far more slowly than we sample.</summary>
        public bool TakeWallHit()
        {
            bool v = _wallHitSeen;
            _wallHitSeen = false;
            return v;
        }

        /// <summary>Was the last impact another car? Only meaningful right after TakeWallHit.</summary>
        public bool LastHitWasCar { get { return _lastHitWasCar; } }

        private bool _wallHitSeen, _lastHitWasCar;

        public bool Verified { get { return _reader.Verified; } }

        /// <summary>How many processes looked like the cabinet at the last search.</summary>
        public int CandidateCount { get { return _reader.CandidateCount; } }

        /// <summary>DIAGNOSTIC (2026-09-06): passthrough for the progress-record dump.</summary>
        public string DumpProgress() { return _reader.DumpProgress(); }

        /// <summary>DIAGNOSTIC (2026-09-06): passthrough for the section-counter hunt.</summary>
        public string ScanCounters(bool restart) { return _reader.ScanCounters(restart); }

        /// <summary>DIAGNOSTIC (2026-09-06): passthrough for the known-string search.</summary>
        public string ScanForString(string needle, int maxHits) { return _reader.ScanForString(needle, maxHits); }

        /// <summary>DIAGNOSTIC (2026-09-06): passthrough for the number search.</summary>
        public string ScanForNumber(double value, int maxHits) { return _reader.ScanForNumber(value, maxHits); }

        /// <summary>DIAGNOSTIC (2026-09-06): passthrough for the many-value search.</summary>
        public string ScanForNumbers(double[] values, int maxHits) { return _reader.ScanForNumbers(values, maxHits); }

        /// <summary>DIAGNOSTIC (2026-09-06): passthrough for the time range search.</summary>
        public string ScanForTime(double milliseconds, int maxHits) { return _reader.ScanForTime(milliseconds, maxHits); }

        /// <summary>DIAGNOSTIC (2026-09-06): passthrough for the proximity search.</summary>
        public string ScanForCluster(double[] values, int windowBytes, int maxHits) { return _reader.ScanForCluster(values, windowBytes, maxHits); }

        /// <summary>The attached game's process name, or null when not attached.</summary>
        public string GameProcessName { get { return _reader.GameProcessName; } }

        /// <summary>The attached game's full path, or null when it could not be read.</summary>
        public string GameExePath { get { return _reader.GameExePath; } }
        public string Status { get { return _reader.Status; } }

        /// <summary>The most recent reading, for anything that wants more than a telemetry frame
        /// can carry: the clock, the lap count, the course and who is driving. A frame has no
        /// fields for those.</summary>
        /// <summary>The most recent reading. Copied under a lock rather than handed out directly:
        /// the sample is a large struct written on the telemetry thread and read from the panel,
        /// the wheel's screen and the dash properties, and a struct that size does not assign
        /// atomically. Without this a reader can catch a mix of two samples, showing one car's
        /// name against another's redline. The lock is uncontended in practice and costs a few
        /// nanoseconds against a read that already crossed a process boundary.</summary>
        public Id8Sample Last { get { lock (_lastGate) return _last; } }

        private Id8Sample _last;
        private readonly object _lastGate = new object();

        /// <summary>Stamp engine data onto a frame. Does nothing at all unless the chain resolves
        /// and the invariants hold, so a wrong build or a denied read leaves the frame untouched.</summary>
        public void Fill(ref TelemetryFrame frame, long pollCount)
        {
            if (!_reader.Verified)
            {
                if (pollCount - _lastAttempt < RetryEveryNPolls) return;
                _lastAttempt = pollCount;
                if (!_reader.Attach()) return;
            }

            Id8Sample s;
            try { s = _reader.Sample(); }
            catch { return; }

            // Once the race is decided, stop feeding the effects. The race record and the car state
            // both stay readable through the results, the earnings and the continue screen, with
            // rpm sitting at whatever it was when the flag fell, so the engine pulse hummed on
            // while the driver read their winnings. Leaving the frame empty lets every effect decay
            // the way it does when a game closes.
            if (s.Finished) { lock (_lastGate) _last = s; return; }
            // Kept even when the car is not readable, because the identity half survives the menus
            // and an idle display is exactly what wants it there.
            lock (_lastGate) _last = s;
            if (s.WallHitStarted) { _wallHitSeen = true; _lastHitWasCar = s.HitWasAnotherCar; }
            // Confirming outranks moving, so a screen change wins when both land together.
            if (!s.InRace)
            {
                if (s.SceneChanged) _menuAction = 2;
                else if (s.MenuButtonPressed && _menuAction == 0) _menuAction = 1;
            }
            if (!s.Valid) return;

            // Publish the DIAL, because the redline and the rpm ceiling both come from the face
            // table and every ratio downstream needs one consistent scale.
            frame.Rpms = s.DialRpm;
            frame.SpeedKmh = s.SpeedKmh;
            // The engine pulse's load layer and the traction-loss effect both read throttle.
            frame.Throttle01 = s.Throttle01;
            frame.Brake01 = s.Brake01;

            // WheelSlip is deliberately NOT set, and that is the point.
            //
            // The traction-loss effect chooses its path by presence: with WheelSlip it takes the
            // direct route, and only WITHOUT it does it read SideslipDeg. Supplying both therefore
            // starved the better signal, which is why the effect stayed silent through real slides.
            // The direct route needs 0.50 to reach full, and this game's figure is a slip angle
            // over 90 degrees, so that would be a 45 degree slide. The sideslip route engages at
            // one degree and saturates at ten, which is what a tyre actually does.

            // Longitudinal and lateral g. Publishing these also gets collision for free: the frame
            // enricher derives a collision magnitude from an acceleration spike whenever the source
            // did not supply one, and that derivation is universal rather than physics-gated.
            // The measured figure where we have one. It is the same quantity, without the
            // single-frame aliasing that made the dot shiver at the rev limiter.
            frame.AccelerationSurge = s.AccelLongMeasured != 0 ? s.AccelLongMeasured : s.AccelLongMps2;
            frame.AccelerationSway = s.AccelLatMps2;

            // Surface texture, from the codes the game assigns each wheel. There is no vertical
            // acceleration anywhere in this game, so this is the whole of the road feel.
            frame.SurfaceRumble = s.SurfaceRumble;
            frame.Airborne = s.Airborne;

            // The game's own impact figure, in g. Supplying this stops the frame enricher deriving
            // one from an acceleration spike, which it sampled at the wrong moment: a crash happens
            // inside a single game frame, so the derived value was luck rather than severity.
            // Only set on an actual impact, so the enricher still covers anything this misses.
            if (s.ImpactG > 0.01) frame.CollisionMagnitude = s.ImpactG;

            // Grip in use per axle. The game models a single track, so it holds one slip figure and
            // weights it twice rather than simulating four tyres. Those two weightings ARE its
            // front and rear utilisation, which is exactly what the axle-slip effect asks for, and
            // it needs no per-tyre data to run. Which weighting is which axle is not established:
            // both are speculative in the map, and the pairing is recorded in the log so a drive
            // can settle it rather than a guess.
            if (s.SlipAxleA > 0 || s.SlipAxleB > 0)
            {
                // Scaled, because the game's figure is not a grip utilisation. It normalises slip
                // angle by 90 degrees, so 0.85 of it would be a 76 degree slide, and the effect
                // only begins at 0.85 of full utilisation. Passed through raw it could never fire:
                // a measured session peaked at 0.45, which is 40 degrees and already a large drift.
                //
                // This factor puts the onset near a 35 degree slide, which in this game is a
                // committed drift rather than ordinary cornering. It is calibrated from ONE
                // measured session, so it is a named constant rather than a buried multiply.
                const double SlipAngleToGrip = 2.2;
                frame.FrontGrip01 = Math.Min(1.0, s.SlipAxleA * SlipAngleToGrip);
                frame.RearGrip01 = Math.Min(1.0, s.SlipAxleB * SlipAngleToGrip);
                // Positive means the rear is closer to its limit than the front, the oversteer
                // direction. Measured, the two sit within two hundredths of each other, because the
                // game models a single track and weights one slip figure twice. So this will stay
                // near zero and the effect's front-versus-rear salience has little to work with.
                frame.GripBalance = (s.SlipAxleB - s.SlipAxleA) * SlipAngleToGrip;
            }

            // Sideslip and yaw rate. The traction-loss effect prefers sideslip outright where it
            // exists, and rightly: with it the effect works in degrees, engaging at 1 and reaching
            // full at 10, instead of the normalised path's 45 degrees. Without these two it was
            // running on its fallback and staying almost silent through real slides.
            frame.SideslipDeg = s.SideslipDeg;
            frame.YawRateDegPerSec = s.YawRateRadPerSec * (180.0 / Math.PI);

            // Where the wheel is. This is what the cabinet's own spring has been missing: the
            // plugin looks for a position from the DirectInput reader, then the HID reader, then
            // the game's steering telemetry, and under TeknoParrot there was never any of the
            // three, so a commanded spring evaluated at zero and rendered as nothing. It reads as
            // "the spring effect is broken" when it is only that nobody knew where the wheel was.
            //
            // Sign: positive is steering RIGHT. The game adds (pi/7)*0.5 times this to the heading
            // when it forms the slip angle, and a rising heading turns the car right, so the two
            // agree. The trace prints this next to the lateral g, which is where to check it.
            frame.SteeringAngle = s.SteerNorm;
            frame.Gear = s.Gear > 0 ? s.Gear.ToString(System.Globalization.CultureInfo.InvariantCulture) : "N";
            frame.RedlineReached = s.AtLimiter;

            Id8Car car = s.Car;
            if (car != null)
            {
                frame.RedlineRpm = car.Redline(s.TunedFace);
                // The face's printed maximum, which is what a rev bar should scale over. It is not
                // what the dial conversion uses; see the LimiterNorm note in Sample.
                frame.MaxRpm = car.DialMax(s.TunedFace);
                // A rotary has no cylinders, but its firing rate matches a four, and that rate is
                // what an engine-pulse effect actually needs.
                if (car.PulseCylinderEquivalent > 0) frame.NumCylinders = car.PulseCylinderEquivalent;
                frame.ForwardGearCount = s.LiveGearCount > 0 ? s.LiveGearCount : car.GearCount;
            }
        }

        public void Dispose() { try { _reader.Dispose(); } catch { } }
    }

    /// <summary>Read-only reader for the running Initial D 8 process.</summary>
    public sealed class Id8MemoryTelemetry : IDisposable
    {
        // ---- static roots proven against the exe's bytes. The image cannot relocate. ----
        private const uint SessionPtrVa = 0x013a82dc;

        // Player identity, readable whenever the session exists, so it works in the menus and the
        // garage where there is no race and therefore no car state.
        private const int SessionToPlayer = 0x28;
        // session + 0x8, low byte: 1 RaceTest, 2 TimeAttack, 3 Story, 4 VS battle, 5 net battle.
        // Proven in the map. Needed because a battle time is not comparable with a time-trial board.
        private const int SessionGameMode = 0x8;
        private const int PlayerRecordKind = 0x4;    // 3 = a human record
        private const int PlayerCardId = 0x18;       // -1 when nobody has swiped a card

        /// <summary>What the game puts in the name field before anyone signs in: katakana reading
        /// "player". Seen on the cabinet, and the reason a name alone is not proof of a driver.</summary>
        private const string PlaceholderName = "プレイヤー";
        private const int PlayerName = 0x28;         // Shift-JIS, NUL terminated
        private const int PlayerTeamName = 0x68;
        private const int PlayerGarageSpecs = 0x8cc; // spec[3], 0x60 bytes each, CarID at +0
        private const int PlayerSelectedCar = 0x9ee; // int16 index 0..2 into the garage
        private const int GarageSpecStride = 0x60;
        private const uint ExpectedImageBase = 0x00400000;

        // ---- the chain ----
        private const int SessionToRace = 0x29c;
        private const int RaceToSlot = 0x34;
        private const int RaceToActors = 0x28;
        private const int ActorToCarObject = 0x28;
        private const int ActorToHudBlock = 0x30;
        private const int CarObjectToCarState = 0x1018;

        // ---- fields, all verified ----
        private const int CsRpm = 0xa10;
        private const int CsRpmNorm = 0xa1c;         // rpm / 10000, about 0.08 idle to 0.81 at the limiter

        /// <summary>Turn the game's normalised rpm into the reading on the car's tachometer.
        ///
        /// Anchored on the REV LIMITER, not on the face's printed maximum. Measured on two cars,
        /// the limiter always pins the normalised rpm near 0.813, but the redline sits at a
        /// different fraction of each face: 0.778 of the R35's 9000 face and 0.846 of the tuned
        /// Trueno's 13000 face. Scaling by the face therefore put the R35 just into the red at the
        /// limiter while the Trueno stopped short and its buzz could never fire. What is actually
        /// true of this game is that the limiter cuts AT the redline on every car.
        ///
        /// Public and static so this can be tested without a running game, which matters: it has
        /// been wrong twice, and both times only the wheel said so.</summary>
        public static double DialRpmFor(double rpmNorm, int redline)
        {
            if (redline <= 0 || rpmNorm <= 0 || double.IsNaN(rpmNorm) || double.IsInfinity(rpmNorm))
                return 0.0;
            return rpmNorm * (redline / (double)LimiterNorm);
        }

        /// <summary>Normalised rpm at which the rev limiter cuts, the same for every car. The exe
        /// builds it as a gear-table base of 6500 plus 1540, giving 8040 against the 10000 scale,
        /// and it then oscillates about 100 either side. Measured live at 0.813 on a GT-R and 0.814
        /// on a tuned Trueno, which agrees.</summary>
        private const float LimiterNorm = 0.804f;
        private const int CsRpmNormaliser = 0xa20;   // always 10000.0
        private const int CsRpmIdle = 0xa2c;         // always 800.0
        private const int CsFrameTime = 0x78;        // always 1/60
        private const int CsGear = 0x9e4;
        private const int CsSpeedMs = 0x7e0;
        private const int CsCarFlags = 0x9b8;
        private const int CsAtLimiter = 0xa3c;
        private const int CsYawRate = 0x10c;         // rad per FRAME, so x60 for rad/s
        private const int CsSpeedDelta = 0x7dc;      // m/s per FRAME, so x60 for m/s squared
        private const int CsSlipNorm = 0x828;        // 0..1, the same value the tyre squeal uses
        private const int CsSteerNorm = 0x2a4;       // -1..1, the steered front wheel angle
        private const int CsSurfaceAttr = 0x1ec;     // one attribute code per wheel
        private const int CsCollisionNow = 0x1dc;    // 1 on the frame of a car-to-car contact
        private const int CsCollisionCooldown = 0x1e0;  // set to 30 on contact, then counts down
        private const uint LastImpactVa = 0x013d3224;   // the last car-to-car impact, kept by the game
        private const uint GameTimeMsVa = 0x013d2d00;   // the game's own millisecond clock
        private const uint GameTimePausedVa = 0x013d2d0c;  // 1 while the game clock is frozen

        /// <summary>A SECOND, independent way in: a table of per-slot pointers to each car's
        /// display block, from which the actor is a fixed distance back. It exists because the
        /// static addresses are the only part of this that a modified executable could move, and
        /// two unrelated roots are far likelier to survive than one. Everything past the root is a
        /// field offset inside a class, which nothing short of recompiling the game can change.
        /// The hud block sits at actor + 0x30 and the table holds the pointer kept at actor +
        /// 0x6b0, so the actor is that far back from what the table points at.</summary>
        private const uint HudBlockTableVa = 0x013d3214;
        private const int ActorToOutPtr = 0x6b0;
        private const int CoTechniqueFlags = 0x884;     // one-frame pulses as the game scores you
        private const int CsGearCount = 0x9e8;          // 5 or 6, live rather than from the table
        private const int CsManualGearbox = 0xa80;      // 1 = manual
        private const int CsSpeedFrac = 0x7ec;          // 0..1 of the car's top speed
        private const int CsCollisionMag = 0x1e4;    // the game's own impact magnitude
        // Wall contact. carState+0x7f8 was tried first and NEVER fires: instrumented over a race
        // it read zero through every wall hit while the car-to-car path worked, which fits its
        // speculative rating in the map. These three are the remaining candidates, all better
        // evidenced, and all are read so one scrape settles which is real.
        private const int CsWallContact = 0x7f8;     // speculative, measured dead
        private const int CoWallScrape = 0x1bca;     // set and cleared explicitly by the car update
        private const int HudWallProbeHit = 0x538;   // one byte per wheel side, stride 0x48
        private const int CsWallImpactVel = 0x7fc;   // m/s, lateral then longitudinal
        // Per-wheel ground contact, in the display block: one validity byte per wheel, stride 0x48.
        private const int HudColliValid = 0x418;
        private const int HudColliStride = 0x48;
        private const int CsHeading = 0x128;         // body heading, radians
        private const int CsTravelHeading = 0x14c;   // the direction the car is actually moving
        private const int CsSlipSideA = 0x82c;       // slip_norm weighted for one axle
        private const int CsSlipSideB = 0x830;       // and for the other
        private const int CoSurfaceLoop = 0x1bc8;    // 0 none, 1 leaves, 2 rumble strip, 3 grass, 4 gutter

        // Race progress and the clock. All hang off the race record, which exists only during a
        // race, so these are the fields that go quiet in the menus.
        private const int RaceToState = 0x4;         // 2 building the course, 3 counting down
        private const int RaceToGap = 0x14;          // metres, positive means the player leads
        private const int RaceToFinishMs = 0x38;
        // Pointer to the int32 array of section times. The Time Attack result sums these and
        // stores the total as the record: 0x008d72bc mov ecx,[race+0x230]; 0x008d72e3 add total.
        private const int RaceToSectionTimes = 0x230;
        // Generous: the longest course seen used 4. Only bounds the scan.
        private const int MaxSections = 16;     // race_elapsed at the goal, 0 until then
        private const int RaceToResultCode = 0x40;   // -2 undecided
        private const int RaceToEndReason = 0x58;    // 0 = reached the goal
        private const int ActorToRank = 0x10;        // -1 until decided
        private const int ActorToState = 0x18;       // 3 standing at the countdown, 5 finished
        private const int RaceToElapsedMs = 0x18;    // ms since GO, negative through the countdown
        private const int RaceToCourse = 0x24;
        private const int RaceToTimeLeftS = 0xbc;
        private const int RaceToProgress = 0xc0;     // + slot * 0xb4
        private const int ProgressStride = 0xb4;
        private const int ProgressLaps = 0x8;
        private const int ProgressCourseFraction = 0xc;
        private const int CourseToSections = 0x8;
        private const int CourseToWord = 0x4;        // low byte is the CourseOrder index
        private const int SectionsToNumLaps = 0x9e4;
        private const uint CourseNameTableVa = 0x0121c9e0;   // ptr32[16] to ASCII names
        private const int CoCarId = 0x1be4;
        private const int HudTachoRpm = 0xc;

        // The on-screen meter, valid only while the race HUD scene is up. +0x8 is the value the
        // needle is actually showing, after the meter's own smoothing, which is the number the
        // redline table is expressed in.
        private const uint SceneMgrPtrVa = 0x013a3d0c;
        private const int SceneMgrToHud = 0x1c;
        private const int SceneMgrToSceneId = 0x20;
        private const uint InputPtrVa2 = 0x013a8758;   // the cabinet's input record
        private const int InputButtons = 0x4;          // bitmask
        private const int InputButtons2 = 0x17;        // a second bitmask
        /// <summary>The race scene. Public because the plugin needs to tell "in a race" from
        /// "anywhere else" to time a board rewrite, and re-deriving that constant there would be a
        /// second copy of a measured number.</summary>
        public const int RaceSceneId = 5;
        private const int HudToMeter = 0x4c;
        private const int MeterTachoShown = 0x8;
        private const int CsThrottle = 0x228;
        // +0x250 is the brake PEDAL. +0x254 is the physics brake the pedal feeds, which lags it.
        private const int CsBrake = 0x250;

        // the two classes the player's car can be
        private const uint VtablePlayerA = 0x00f9666c;
        private const uint VtablePlayerB = 0x0100aab4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
            System.Text.StringBuilder name, ref uint size);

        /// <summary>The full path of the running game. MainModule is tried first, since this class
        /// already reads it for the image base, and Windows is asked directly if that throws:
        /// MainModule can fail on its own for reasons the handle we already hold does not share.
        /// Returns null rather than a guess, because a wrong path is worse than none.</summary>
        private static string ReadExePath(Process game, IntPtr handle)
        {
            try
            {
                string p = game.MainModule != null ? game.MainModule.FileName : null;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref size) && size > 0)
                    return sb.ToString(0, (int)size);
            }
            catch { }
            return null;
        }

        /// <summary>The process name of the attached game, or null when not attached. This is the
        /// name a SimHub custom game profile needs in order to detect the title.</summary>
        public string GameProcessName { get; private set; }

        /// <summary>The full path of the attached game, or null when it could not be read. This is
        /// what a SimHub profile needs in order to LAUNCH the title rather than only notice it.
        /// Null is a legitimate answer and callers must treat it as one.</summary>
        public string GameExePath { get; private set; }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);

        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;

        private IntPtr _h = IntPtr.Zero;
        private int _pid = -1;
        private readonly byte[] _buf = new byte[8];
        private readonly object _gate = new object();

        /// <summary>Set by the owner to route diagnostics into the plugin log.</summary>
        public Action<string> Log { get; set; }

        /// <summary>True once the chain has resolved and the three invariants have held at least
        /// once. Until then nothing is published, so a wrong build simply produces no telemetry
        /// rather than nonsense.</summary>
        public bool Verified { get; private set; }

        /// <summary>Why the reader is not producing values, for the UI and the log.</summary>
        public string Status { get; private set; } = "not started";

        private bool _loggedVerified;
        private bool _loggedIdentity;
        private bool _loggedAltRoot;
        /// <summary>The session root actually in use. Normally the address the map recorded; on a
        /// build whose data has moved, whatever the relocation found.</summary>
        private uint _sessionRootVa = SessionPtrVa;
        private bool _triedRelocate;
        private uint _moduleBase;
        private int _moduleSize;
        private bool _scannedPlayerRecord;
        private int _lastCarId = -1;
        private long _lastTraceTicks;
        private int _traceLines;
        private double _peakLongPos, _peakLongNeg, _peakLatAbs, _peakSlip, _peakSurface, _peakImpactG;
        private double _peakGameImpact, _peakAxleA, _peakAxleB, _peakSideslip;
        private double _peakFrameDecel, _peakMeasuredDecel;
        private int _prevCollisionCooldown;
        private bool _prevWallContact, _wallHitEdge, _hitWasCar;
        private int _impactTraceLines;
        private ulong _prevButtons;
        private bool _haveButtons, _haveScene;
        private int _prevSceneId;
        private double _prevSpeedMs, _prevHeading;
        private double _yawRateForG, _yawSmoothed, _longSmoothed;
        private uint _prevGameMs;
        private bool _baselined;
        private uint _prevRace;
        private bool _sawCountdown;
        private uint _techniqueSeen;
        private int _prevLapsCompleted, _prevLapBoundaryMs, _lastLapMs, _bestLapMs;

        /// <summary>Take the techniques scored since the last call and clear them. Latched because
        /// the game sets each bit for a single frame.</summary>
        public uint TakeTechniques()
        {
            uint v = _techniqueSeen;
            _techniqueSeen = 0;
            return v;
        }
        private const int TraceLineCap = 400;

        /// <summary>Log the three rpm readings side by side, capped and rate limited. On while the
        /// dial relationship is being established; the readings are what settle it.</summary>
        public bool Trace { get; set; } = true;

        /// <summary>Attach to the running game if it is there. Safe to call repeatedly; it is a
        /// no-op while already attached to a live process.</summary>
        public bool Attach()
        {
            lock (_gate)
            {
                if (_h != IntPtr.Zero)
                {
                    try
                    {
                        Process cur = Process.GetProcessById(_pid);
                        if (!cur.HasExited) return true;
                    }
                    catch { }
                    Detach();
                }

                Process game = FindGame();
                if (game == null) { Status = "the game is not running"; return false; }

                uint moduleBase;
                try
                {
                    ProcessModule m = game.MainModule;
                    if (m == null) { Status = "could not read the game's main module"; return false; }
                    moduleBase = (uint)m.BaseAddress.ToInt64();
                }
                catch (Exception ex)
                {
                    Status = "could not read the game's main module: " + ex.Message;
                    return false;
                }

                if (moduleBase != ExpectedImageBase)
                {
                    Status = "the game loaded at 0x" + moduleBase.ToString("x8") + " but the map was built for 0x"
                             + ExpectedImageBase.ToString("x8") + ", so this is a different build and no data is safe to use";
                    Emit(Status);
                    return false;
                }

                IntPtr h = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, game.Id);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Status = err == 5
                        ? "cannot read the game's memory because access was denied. The game is running with higher "
                          + "privileges than SimHub, so start SimHub as administrator or start the game without it."
                        : "cannot open the game for reading, Windows error " + err;
                    Emit(Status);
                    return false;
                }

                _h = h;
                _pid = game.Id;
                _moduleBase = moduleBase;
                try { _moduleSize = game.MainModule != null ? game.MainModule.ModuleMemorySize : 0; }
                catch { _moduleSize = 0; }
                // The identity of the running dump, captured while we are attached and it is
                // knowable. Dump names differ between users, so nothing downstream should ever
                // guess at one: a SimHub game profile that detects and launches this game wants
                // exactly the process name and path we are reading from right now.
                GameProcessName = game.ProcessName;
                GameExePath = ReadExePath(game, h);
                Status = "attached to " + game.ProcessName;
                Emit("attached to the game for read-only telemetry (pid " + game.Id + ", process "
                     + GameProcessName + ", exe " + (GameExePath ?? "UNREADABLE") + ")");
                return true;
            }
        }

        /// <summary>The running cabinet, chosen from every process whose name looks like one.
        ///
        /// More than one can match, and it is not a hypothetical: a crashed or hung copy of the
        /// game from an earlier run stays in the process table with the same name and the same
        /// image base, so attaching to it SUCCEEDS and then resolves nothing. Every symptom of that
        /// looks like a broken map rather than a wrong target: no gear, no speed, no countdown, and
        /// a memory search that reads a corpse and finds none of what it was asked for.
        ///
        /// So the newest process wins, a window beats no window, and finding more than one is said
        /// out loud once rather than silently resolved.</summary>
        private Process FindGame()
        {
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return null; }

            var candidates = new List<Process>();
            foreach (Process p in all)
            {
                try
                {
                    if (p.HasExited) continue;
                    if (p.ProcessName.IndexOf("InitialD8", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    candidates.Add(p);
                }
                catch { }
            }
            CandidateCount = candidates.Count;
            if (candidates.Count == 0) return null;

            Process best = null;
            foreach (Process p in candidates)
            {
                if (best == null) { best = p; continue; }
                if (Better(p, best)) best = p;
            }

            if (candidates.Count > 1 && best != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("more than one copy of the game is running, which is usually a crashed one ")
                  .Append("left behind. Reading pid ").Append(best.Id).Append(", also present:");
                foreach (Process p in candidates)
                {
                    if (p.Id == best.Id) continue;
                    sb.Append(' ').Append(p.Id);
                    try { sb.Append(" (started ").Append(p.StartTime.ToString("HH:mm:ss")).Append(')'); }
                    catch { }
                }
                sb.Append(". If the wheel is dead, close the stale one.");
                Emit(sb.ToString());
            }
            return best;
        }

        /// <summary>How many processes looked like the cabinet at the last search. Above one means
        /// a stale copy is present and the choice below actually mattered.</summary>
        public int CandidateCount { get; private set; }

        /// <summary>Is a a better target than b? A process with a window beats one without, since a
        /// hung copy usually loses its window first, and otherwise the one that started most
        /// recently wins: the live game is always newer than the corpse of a previous run.</summary>
        private static bool Better(Process a, Process b)
        {
            try
            {
                bool aw = a.MainWindowHandle != IntPtr.Zero, bw = b.MainWindowHandle != IntPtr.Zero;
                if (aw != bw) return aw;
            }
            catch { }
            try { return a.StartTime > b.StartTime; }
            catch { return false; }
        }

        public void Detach()
        {
            lock (_gate)
            {
                if (_h != IntPtr.Zero) { try { CloseHandle(_h); } catch { } }
                _h = IntPtr.Zero;
                _pid = -1;
                GameProcessName = null;
                GameExePath = null;
                Verified = false;
                _baselined = false;
                _yawSmoothed = _longSmoothed = 0;
                _prevGameMs = 0;
                // The next process may be a different build, so anything learned about this one
                // has to go with it. Carrying a relocated root into another executable would read
                // a plausible-looking address in entirely the wrong place.
                _sessionRootVa = SessionPtrVa;
                _triedRelocate = false;
                _loggedAltRoot = false;
                _moduleBase = 0;
                _moduleSize = 0;
                _loggedVerified = false;
                _lastCarId = -1;
            }
        }

        /// <summary>Read into a caller's buffer. The shared one is 8 bytes, which is enough for
        /// every scalar but not for a name.</summary>
        private bool ReadInto(uint addr, byte[] buf, int len)
        {
            if (_h == IntPtr.Zero || addr == 0 || buf == null || len <= 0 || len > buf.Length) return false;
            IntPtr got;
            return ReadProcessMemory(_h, (IntPtr)addr, buf, len, out got) && got.ToInt64() == len;
        }

        private bool Read(uint addr, int len)
        {
            if (_h == IntPtr.Zero || addr == 0) return false;
            IntPtr got;
            return ReadProcessMemory(_h, (IntPtr)addr, _buf, len, out got) && got.ToInt64() == len;
        }

        private bool U32(uint addr, out uint v)
        {
            v = 0;
            if (!Read(addr, 4)) return false;
            v = BitConverter.ToUInt32(_buf, 0);
            return true;
        }

        private bool I32(uint addr, out int v)
        {
            v = 0;
            if (!Read(addr, 4)) return false;
            v = BitConverter.ToInt32(_buf, 0);
            return true;
        }

        private bool F32(uint addr, out float v)
        {
            v = 0;
            if (!Read(addr, 4)) return false;
            v = BitConverter.ToSingle(_buf, 0);
            return true;
        }

        private uint Deref(uint addr)
        {
            uint v;
            return U32(addr, out v) ? v : 0u;
        }

        /// <summary>Walk the chain and read one sample. Returns a sample whose Valid is false
        /// whenever anything along the way does not hold, which is the normal state in menus.</summary>
        public Id8Sample Sample()
        {
            var s = new Id8Sample();
            // 0 is a valid course and a valid direction, so "unknown" has to be said explicitly.
            s.CourseId = -1;
            s.Direction = -1;
            lock (_gate)
            {
                if (_h == IntPtr.Zero) return s;

                // Menus first, and outside every other check. The cabinet's input record and its
                // scene manager exist from the attract screen onward, long before a session and
                // with no race at all, which is exactly where someone is when they are navigating.
                ReadMenuActivity(ref s);

                uint session = Deref(_sessionRootVa);
                if (session != 0 && I32(session + SessionGameMode, out int modeWord)) s.GameMode = modeWord & 0xff;
                if (session == 0)
                {
                    // The recorded root gives nothing. That is the normal state before a game
                    // builds its session, and it is ALSO what a modified build looks like, so the
                    // two have to be told apart before concluding anything.
                    //
                    // The second root settles it: if the per-slot display table finds a real car
                    // while the first root reads zero, the data has moved. Then, and only then, go
                    // looking for where it moved to.
                    if (!_triedRelocate && FindCarByAltRoot() != 0)
                    {
                        _triedRelocate = true;
                        uint found = RelocateSessionRoot();
                        if (found != 0)
                        {
                            _sessionRootVa = found;
                            Emit("this is a MODIFIED build: the session root is at 0x" + found.ToString("x8")
                                 + ", not the 0x" + SessionPtrVa.ToString("x8") + " the map recorded. Found it again "
                                 + "by testing every global the code writes after a constructor call and keeping the "
                                 + "one whose chain reaches a car state holding the right constants. Field offsets "
                                 + "are unaffected, so everything else applies as recorded.");
                            session = Deref(found);
                        }
                        else
                        {
                            Emit("this looks like a MODIFIED build: a car is reachable through the display table "
                                 + "but the session root at 0x" + SessionPtrVa.ToString("x8") + " reads zero, and "
                                 + "searching for a replacement found none. The map needs rebuilding for this "
                                 + "executable.");
                        }
                    }
                    if (session == 0)
                    {
                        Status = "the game has not built its session yet";
                        return s;
                    }
                }

                // Identity first, because it survives outside a race. Everything below this needs
                // a live race; this does not, which is what lets an idle screen name the driver.
                s.SessionValid = true;
                ReadIdentity(session, ref s);
                if (!_scannedPlayerRecord)
                {
                    _scannedPlayerRecord = true;
                    ScanPlayerRecordForNames(Deref(session + SessionToPlayer));
                }
                if (!_loggedIdentity && !string.IsNullOrEmpty(s.PlayerName))
                {
                    _loggedIdentity = true;
                    Emit("player is '" + s.PlayerName + "'"
                         + " cardId=" + s.CardId + " hasCard=" + s.HasCard
                         + (s.PlayerNameAscii != null && s.PlayerNameAscii != s.PlayerName
                            ? " (shown on the wheel as '" + s.PlayerNameAscii + "')" : "")
                         + (string.IsNullOrEmpty(s.TeamName) ? "" : ", team '" + s.TeamName + "'")
                         + (s.GarageCar != null ? ", garage car " + s.GarageCar.Code + " " + s.GarageCar.Name : ""));
                }

                uint race = Deref(session + SessionToRace);
                if (race == 0) { Status = "not in a race"; return s; }
                s.InRace = true;
                if (race != _prevRace)
                {
                    // A new race: the lap clock starts again, so nothing from the last one carries.
                    _prevRace = race;
                    _sawCountdown = false;
                    _prevLapsCompleted = 0;
                    _prevLapBoundaryMs = 0;
                    _lastLapMs = 0;
                    _bestLapMs = 0;
                }

                int slot;
                if (!I32(race + RaceToSlot, out slot) || slot < 0 || slot > 1) { Status = "the player slot is not readable"; return s; }

                uint actor = Deref((uint)(race + RaceToActors + 4 * slot));
                if (actor == 0) { Status = "the player's actor record is missing"; return s; }

                uint carObject = Deref(actor + ActorToCarObject);
                if (carObject == 0) { Status = "the player's car object is missing"; return s; }

                uint vtable = Deref(carObject);
                if (vtable != VtablePlayerA && vtable != VtablePlayerB)
                {
                    Status = "the car object's class is 0x" + vtable.ToString("x8") + ", which is not a player car";
                    return s;
                }

                uint carState = Deref(carObject + CarObjectToCarState);
                if (carState == 0) { Status = "the car state is missing"; return s; }

                // The three invariants. If any fails the layout is not the one that was analysed,
                // and publishing anything would be worse than publishing nothing.
                float normaliser, idle, frameTime;
                if (!F32(carState + CsRpmNormaliser, out normaliser) ||
                    !F32(carState + CsRpmIdle, out idle) ||
                    !F32(carState + CsFrameTime, out frameTime))
                {
                    Status = "the car state is not readable";
                    return s;
                }

                if (Math.Abs(normaliser - 10000f) > 1f ||
                    Math.Abs(idle - 800f) > 1f ||
                    Math.Abs(frameTime - (1f / 60f)) > 0.002f)
                {
                    Status = "the car state does not match the map: rpm scale " + normaliser.ToString("0.##")
                             + ", idle " + idle.ToString("0.##") + ", frame time " + frameTime.ToString("0.#####")
                             + ". Expected 10000, 800 and 0.01667. Treating the game as unmapped.";
                    if (Verified) Emit(Status);
                    Verified = false;
                    return s;
                }

                float internalRpm, speedMs, tacho, throttle, brake, yawRate, speedDelta, slipNorm;
                int gear, carId, limiter;
                uint flags;
                float rpmNorm;
                F32(carState + CsRpmNorm, out rpmNorm);
                F32(carState + CsRpm, out internalRpm);
                F32(carState + CsSpeedMs, out speedMs);
                F32(carState + CsThrottle, out throttle);
                F32(carState + CsBrake, out brake);
                F32(carState + CsYawRate, out yawRate);
                F32(carState + CsSpeedDelta, out speedDelta);
                F32(carState + CsSlipNorm, out slipNorm);
                I32(carState + CsGear, out gear);
                U32(carState + CsCarFlags, out flags);
                I32(carState + CsAtLimiter, out limiter);
                I32(carObject + CoCarId, out carId);
                F32(actor + ActorToHudBlock + HudTachoRpm, out tacho);

                // The displayed dial value, when the race HUD is up.
                float meter = 0f;
                uint sceneMgr = Deref(SceneMgrPtrVa);
                if (sceneMgr != 0)
                {
                    int sceneId;
                    if (I32(sceneMgr + SceneMgrToSceneId, out sceneId) && sceneId == RaceSceneId)
                    {
                        uint hud = Deref(sceneMgr + SceneMgrToHud);
                        if (hud != 0)
                        {
                            uint m = Deref(hud + HudToMeter);
                            if (m != 0) F32(m + MeterTachoShown, out meter);
                        }
                    }
                }

                s.Valid = true;
                s.InternalRpm = internalRpm;
                s.RpmNorm = rpmNorm;
                s.Car = Id8CarTable.Find(carId);
                // Dial rpm, anchored on the REV LIMITER rather than on the face's printed maximum.
                //
                // Scaling by the face maximum was tried first and is wrong. Measured on two cars,
                // the limiter always pins the normalised rpm at the same place, about 0.813, but
                // the redline sits at a different fraction of each face: 0.778 of the R35's 9000
                // face, 0.846 of the tuned Trueno's 13000 face. So face scaling put the R35's
                // needle just into the red at the limiter while the Trueno's stopped short of it
                // and its buzz could never fire at all.
                //
                // What is actually true of this game is that the limiter cuts AT the redline, for
                // every car. Anchoring there makes the needle read the car's redline when the
                // limiter bites, which is both correct and reachable on every face.
                //
                // The display block value at +0xc and the meter widget are both on the INTERNAL
                // scale (measured within 1% of +0xa10), so neither is the dial despite the meter
                // being labelled as one. Without a table row there is no redline, so the raw
                // value stands.
                bool tunedFace = (flags & Id8CarTable.TunedTachoFaceBit) != 0;
                s.DialRpm = s.Car != null
                    ? DialRpmFor(rpmNorm, s.Car.Redline(tunedFace))
                    : tacho;
                s.MeterRpm = meter;
                s.Gear = gear;
                s.SpeedKmh = speedMs * 3.6;
                s.Throttle01 = Clamp01(throttle);
                s.Brake01 = Clamp01(brake);
                s.SlipNorm = Clamp01(slipNorm);
                // Both stored per FRAME at a fixed 60 Hz, so sixty of them make a second.
                s.AccelLongMps2 = Finite(speedDelta) * 60.0;

                // The measured longitudinal figure is computed below, against the game's clock,
                // for the same reason the yaw rate is.
                // No lateral acceleration is stored anywhere: the physics is single track. For a
                // car following its heading, speed times yaw rate IS the lateral acceleration.
                //
                // NEGATED, on the only evidence that can settle it: the owner has now reported
                // the circle mirrored twice, both times while this was unnegated.
                //
                // I previously reverted a negation because the steering angle and the lateral g
                // "agreed" 37 times out of 37. That check was worthless. The steering field's own
                // sign convention is exactly as unknown as the yaw's, so agreement between them
                // only proves the two share a convention, not that either points right. Validating
                // an assumption against another assumption is not a measurement, and it overturned
                // a state the owner had already judged correct.
                //
                // The geometry argument was no better: it needs the handedness of the game's axes,
                // which is not established anywhere in the map.

                // Yaw rate measured across our own sampling interval, from the heading the game
                // keeps, rather than from its single-frame value. Same aliasing that made the
                // longitudinal figure jitter: the stored rate covers one 60 Hz frame while we read
                // at a similar unsynchronised rate, so we double-count some frames and skip others.
                // On the lateral axis that jitter is the shivering dot.
                // Both rates are differenced against THE GAME'S OWN CLOCK, not ours.
                //
                // Measuring across our sampling interval was still wrong, and this is why: the
                // game advances heading and speed once per 60 Hz frame, while we read every 16.1 ms
                // unsynchronised. Roughly one sample in twenty lands inside a frame the game has
                // not stepped yet, sees no change at all, and reports zero. Alternating a real rate
                // with zero IS the jitter, and no choice of window on our side can remove it,
                // because the source is quantised to the game's frames rather than to time.
                //
                // Using the game's millisecond clock makes the two agree: when it has not moved,
                // neither has the physics, so the previous rate stands rather than being replaced
                // by a zero that never happened.
                uint gameMs;
                bool clockMoved = U32(GameTimeMsVa, out gameMs) && gameMs != _prevGameMs;
                double gdt = clockMoved && _prevGameMs != 0 && gameMs > _prevGameMs
                    ? (gameMs - _prevGameMs) / 1000.0
                    : 0.0;

                float headingNow;
                bool haveHeading = F32(carState + CsHeading, out headingNow);
                if (!_baselined && haveHeading)
                {
                    // Seed the baselines before differencing against them. Without this the first
                    // interval subtracts a heading of zero and a speed of zero and reports a rate
                    // of several hundred degrees a second on a stationary car.
                    _prevHeading = Finite(headingNow);
                    _prevSpeedMs = Finite(speedMs);
                    _baselined = true;
                }
                else if (clockMoved && gdt > 0.004 && gdt < 0.5 && haveHeading)
                {
                    double measured = WrapRadians(Finite(headingNow) - _prevHeading) / gdt;
                    double dSpeed = (Finite(speedMs) - _prevSpeedMs) / gdt;
                    _prevHeading = Finite(headingNow);
                    _prevSpeedMs = Finite(speedMs);

                    // A car cannot rotate this fast, and neither can it change speed this hard
                    // without having hit something. A reset or a warp between courses would
                    // otherwise land in the smoother and take a second to decay back out.
                    const double MaxYawRadPerSec = 10.0;      // about 570 degrees a second
                    const double MaxAccelMps2 = 200.0;        // about 20 g
                    if (Math.Abs(measured) <= MaxYawRadPerSec && Math.Abs(dSpeed) <= MaxAccelMps2)
                    {
                        // A short average over about three game frames. The rate is genuinely
                        // stepwise at 60 Hz, so a display fed the raw value twitches even when it
                        // is right. Fifty milliseconds is under what reads as lag on a g meter.
                        _yawSmoothed += (measured - _yawSmoothed) * 0.35;
                        _longSmoothed += (dSpeed - _longSmoothed) * 0.35;
                    }
                }
                if (clockMoved) _prevGameMs = gameMs;

                s.YawRateRadPerSec = _yawSmoothed;
                _yawRateForG = _yawSmoothed / 60.0;
                s.AccelLongMeasured = _longSmoothed;

                s.AccelLatMps2 = -(Finite(speedMs) * _yawRateForG * 60.0);

                s.SteerNorm = 0;
                float steer;
                if (F32(carState + CsSteerNorm, out steer)) s.SteerNorm = Finite(steer);
                s.SurfaceRumble = ReadSurfaceRumble(carState, carObject);
                _lastHudBlockVa = (uint)(actor + ActorToHudBlock);
                s.ImpactG = ReadImpact(carState, carObject, actor + ActorToHudBlock);
                s.Airborne = ReadAirborne(actor + ActorToHudBlock);
                s.WallHitStarted = _wallHitEdge;
                s.HitWasAnotherCar = _hitWasCar;

                uint paused;
                s.Paused = U32(GameTimePausedVa, out paused) && paused != 0;

                float sfrac;
                if (F32(carState + CsSpeedFrac, out sfrac)) s.SpeedFraction = Clamp01(sfrac);
                byte[] one = new byte[1];
                if (ReadInto(carState + CsManualGearbox, one, 1)) s.ManualGearbox = one[0] != 0;
                int liveGears;
                if (I32(carState + CsGearCount, out liveGears) && liveGears >= 4 && liveGears <= 8)
                    s.LiveGearCount = liveGears;

                // Techniques are one-frame pulses, so polling the word only catches them by luck,
                // exactly as the collision flag did. Accumulate every bit seen and let the consumer
                // clear them; missing a scored drift is worse than reporting it a frame late.
                uint tech;
                if (U32(carObject + CoTechniqueFlags, out tech) && tech != 0) _techniqueSeen |= tech;
                s.TechniqueFlags = _techniqueSeen;
                float sa, sb;
                if (F32(carState + CsSlipSideA, out sa)) s.SlipAxleA = Clamp01(sa);
                if (F32(carState + CsSlipSideB, out sb)) s.SlipAxleB = Clamp01(sb);

                // Body sideslip, from the two headings the game already keeps: where the car points
                // minus where it is travelling. Taken from these rather than from the stored slip
                // angle at +0x814, which has the steered front wheel folded into it and so reads
                // non-zero merely for turning the wheel.
                float head, travel;
                if (F32(carState + CsHeading, out head) && F32(carState + CsTravelHeading, out travel))
                    s.SideslipDeg = WrapRadians(Finite(head) - Finite(travel)) * (180.0 / Math.PI);
                ReadRaceProgress(race, slot, ref s);
                s.CarId = carId;
                s.CarFlags = flags;
                s.TunedFace = (flags & Id8CarTable.TunedTachoFaceBit) != 0;
                s.AtLimiter = limiter != 0;

                if (!Verified)
                {
                    Verified = true;
                    if (!_loggedVerified)
                    {
                        _loggedVerified = true;
                        Emit("the memory map applies to this build: the chain resolved and all three invariants hold");
                    }
                }
                Status = "reading";

                // A bounded trace of the three rpm readings side by side. The dial the driver reads
                // is the ground truth for where the redline buzz should fire, so this is what says
                // whether the published value is on that scale. Capped, and one line every two
                // seconds, so it cannot fill the log.
                // Peaks, tracked on EVERY sample rather than on the traced ones. A brake event lasts
                // under a second and a two-second trace interval walks straight past it, which is
                // how braking came to look like a tenth of a g when it is not.
                double lg = s.AccelLongMps2 / 9.81, latAbs = Math.Abs(s.AccelLatMps2) / 9.81;
                // Impacts and resets have to be kept out of the driving peaks. A wall stops the car
                // inside one frame, which reads as several g of "braking" and buried the real
                // figure: a session showed -8.42g, which is a crash, not a brake pedal. Anything
                // past this bound is recorded as an impact instead, where it is the useful number.
                const double DrivingLimitG = 2.0;
                if (Math.Abs(lg) <= DrivingLimitG)
                {
                    if (lg > _peakLongPos) _peakLongPos = lg;
                    if (lg < _peakLongNeg) _peakLongNeg = lg;
                }
                else if (Math.Abs(lg) > Math.Abs(_peakImpactG)) _peakImpactG = lg;
                if (latAbs <= DrivingLimitG && latAbs > _peakLatAbs) _peakLatAbs = latAbs;
                if (s.SlipNorm > _peakSlip) _peakSlip = s.SlipNorm;
                if (s.ImpactG > _peakGameImpact) _peakGameImpact = s.ImpactG;
                if (s.SlipAxleA > _peakAxleA) _peakAxleA = s.SlipAxleA;
                if (s.SlipAxleB > _peakAxleB) _peakAxleB = s.SlipAxleB;
                if (Math.Abs(s.SideslipDeg) > _peakSideslip) _peakSideslip = Math.Abs(s.SideslipDeg);
                double md = s.AccelLongMeasured / 9.81;
                if (lg < _peakFrameDecel && lg > -3.0) _peakFrameDecel = lg;
                if (md < _peakMeasuredDecel && md > -3.0) _peakMeasuredDecel = md;
                if (s.SurfaceRumble > _peakSurface) _peakSurface = s.SurfaceRumble;

                if (Trace && _traceLines < TraceLineCap)
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (nowTicks - _lastTraceTicks >= Stopwatch.Frequency * 2)
                    {
                        _lastTraceTicks = nowTicks;
                        _traceLines++;
                        int redline = s.Car != null ? s.Car.Redline(s.TunedFace) : 0;
                        // The traction-loss effect wants about 0.5 for full strength and ignores
                        // anything under 0.05, so the slip peak is what says whether the signal is
                        // simply small in this game or the effect is not running at all.
                        Emit("PEAKS so far: accel +" + _peakLongPos.ToString("0.00")
                             + "g, braking " + _peakLongNeg.ToString("0.00")
                             + "g, lateral " + _peakLatAbs.ToString("0.00")
                             + "g, slip " + _peakSlip.ToString("0.00")
                             + " (0.05 deadband, 0.50 is full), surface " + _peakSurface.ToString("0.00")
                             + ", biggest impact " + _peakImpactG.ToString("0.0") + "g"
                             + ", game impact " + _peakGameImpact.ToString("0.0") + "g"
                             + ", axles " + _peakAxleA.ToString("0.00") + "/" + _peakAxleB.ToString("0.00")
                             + ", sideslip " + _peakSideslip.ToString("0.0") + " deg"
                             + " | decel: frame-delta " + _peakFrameDecel.ToString("0.00")
                             + "g vs measured " + _peakMeasuredDecel.ToString("0.00") + "g");
                        // Steering is deliberately NOT compared against the lateral g here any
                        // more. Its own sign convention is unknown, so agreement between the two
                        // proves only that they share one, and reading that as confirmation once
                        // overturned a correct setting.
                        Emit("steer " + s.SteerNorm.ToString("0.00")
                             + " | yaw/s " + s.YawRateRadPerSec.ToString("0.000")
                             + " | latG " + (s.AccelLatMps2 / 9.81).ToString("0.00")
                             + " | longG " + (s.AccelLongMps2 / 9.81).ToString("0.00")
                             + " | slip " + s.SlipNorm.ToString("0.00")
                             + " | surf " + s.SurfaceRumble.ToString("0.00"));
                        Emit("DIAL " + s.DialRpm.ToString("0")
                             + " | norm " + rpmNorm.ToString("0.000")
                             + " | internal " + internalRpm.ToString("0")
                             + " | display block " + tacho.ToString("0")
                             + " | meter " + meter.ToString("0")
                             + " | redline " + redline
                             + " | gear " + gear
                             + " | " + (speedMs * 3.6).ToString("0") + " km/h"
                             + (_traceLines == TraceLineCap ? "  (last trace line)" : ""));
                    }
                }

                if (carId != _lastCarId)
                {
                    _lastCarId = carId;
                    Emit(s.Car != null
                        ? "car is CarID " + carId + " " + s.Car.Code + " " + s.Car.Name + ", "
                          + (s.TunedFace ? "tuned" : "stock") + " tacho face, redline "
                          + s.Car.Redline(s.TunedFace) + " of " + s.Car.DialMax(s.TunedFace)
                          + ", " + s.Car.Cylinders + " cylinders, " + s.Car.GearCount + " gears"
                        : "car is CarID " + carId + ", which is not in the engine table");
                }
                return s;
            }
        }

        /// <summary>The clock, the lap count and where the car is round the course. This game runs
        /// on a countdown rather than a lap limit, so the seconds left is the number that matters
        /// and the lap count is secondary.</summary>
        private void ReadRaceProgress(uint race, int slot, ref Id8Sample s)
        {
            int v;
            if (I32(race + RaceToElapsedMs, out v)) s.RaceElapsedMs = v;
            if (I32(race + RaceToTimeLeftS, out v)) s.TimeLeftSeconds = v;
            float f;
            if (F32(race + RaceToGap, out f)) s.GapMetres = Finite(f);

            if (I32(race + RaceToState, out v)) s.RaceState = v;
            if (I32(race + RaceToResultCode, out v)) s.ResultCode = v;
            if (I32(race + RaceToEndReason, out v)) s.EndReason = v;
            if (I32(race + RaceToFinishMs, out v)) s.FinishTimeMs = v;
            ReadSectionTimes(race, ref s);

            // The countdown, straight off the clock: it runs from -3000 to 0 before GO, so the
            // number on the lights is just how many whole seconds are left.
            //
            // GO requires having actually SEEN the countdown first. The race record exists long
            // before the lights do, all through the loading screen and the opening cutscene, and
            // its clock reads zero for that whole time. Testing "elapsed has reached zero" was
            // therefore true from the moment a race was selected, and the panel said GO over the
            // cutscene. A real start always passes through negative first.
            if (s.RaceElapsedMs < 0 && s.RaceElapsedMs > -4000)
            {
                s.CountdownSeconds = (-s.RaceElapsedMs + 999) / 1000;
                if (s.CountdownSeconds > 3) s.CountdownSeconds = 3;
                _sawCountdown = true;
            }
            else if (s.RaceElapsedMs > 0 && _sawCountdown)
            {
                s.CountdownSeconds = 0;
                s.JustStarted = s.RaceElapsedMs < 1200;
            }
            else s.CountdownSeconds = -1;

            uint actor = Deref((uint)(race + RaceToActors + 4 * slot));
            if (actor != 0)
            {
                if (I32(actor + ActorToRank, out v)) s.ActorRank = v;
                if (I32(actor + ActorToState, out v)) s.ActorState = v;
            }

            // Finished, and whether it was a win. Rank is the direct answer where it has been
            // decided; the gap is the fallback, and in a two car battle being ahead at the goal is
            // the same thing. Both readings are carried out so the log can settle the encoding.
            // Two different endings, and only one of them is a race result. end_reason 0 means the
            // goal was reached; anything else is the clock or an event. A measured session ended
            // with end_reason 1 at exactly 120012 ms, which is the two minute limit expiring, and
            // rank still read 0 from its initial value. Treating that as a win would have called a
            // time-out a victory.
            s.Finished = s.ActorState == 5 || s.FinishTimeMs > 0;
            s.ReachedGoal = s.Finished && s.EndReason == 0;
            if (s.ReachedGoal)
            {
                if (s.ActorRank == 0) s.Won = true;
                else if (s.ActorRank > 0) s.Won = false;
                else s.Won = s.GapMetres > 0;
            }
            else s.Won = null;

            uint progress = (uint)(race + RaceToProgress + slot * ProgressStride);
            _lastProgressVa = progress;
            if (I32(progress + ProgressLaps, out v)) s.LapsCompleted = v;

            // Lap timing. SimHub knows nothing about this game, so its lap fields are zero and the
            // panel's lap card never fires. The game counts laps FINISHED and keeps a race clock,
            // so a rise in the count is a lap boundary and the time between two boundaries is the
            // lap. Latched, because the boundary is one frame wide like everything else here.
            if (s.LapsCompleted > _prevLapsCompleted)
            {
                if (_prevLapBoundaryMs > 0 || _prevLapsCompleted > 0)
                    s.LastLapMs = s.RaceElapsedMs - _prevLapBoundaryMs;
                else
                    s.LastLapMs = s.RaceElapsedMs;   // the first lap runs from GO
                if (s.LastLapMs > 0)
                {
                    s.LapJustCompleted = true;
                    if (_bestLapMs == 0 || s.LastLapMs < _bestLapMs) _bestLapMs = s.LastLapMs;
                }
                _prevLapBoundaryMs = s.RaceElapsedMs;
                _lastLapMs = s.LastLapMs;
            }
            else s.LastLapMs = _lastLapMs;
            s.BestLapMs = _bestLapMs;
            _prevLapsCompleted = s.LapsCompleted;
            if (F32(progress + ProgressCourseFraction, out f)) s.CourseFraction = Clamp01(f);

            uint course = Deref(race + RaceToCourse);
            if (course == 0) return;

            uint word;
            if (U32(course + CourseToWord, out word))
            {
                s.CourseName = CourseNameFor((int)(word & 0xff));
                s.CourseId = (int)(word & 0xff);
                s.Direction = (int)((word >> 28) & 0xf);
                // Anything outside the board grid is not a course we can key on. Better to carry
                // -1 than a number that would silently address the wrong board.
                if (s.CourseId > 15) s.CourseId = -1;
                if (s.Direction > 1) s.Direction = -1;
            }

            uint sections = Deref(course + CourseToSections);
            if (sections != 0 && I32(sections + SectionsToNumLaps, out v) && v > 0 && v < 100)
                s.TotalLaps = v;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(IntPtr process, IntPtr address,
            out MemoryBasicInformation info, uint length);

        /// <summary>The 32-bit layout, which is the only one that matters here: SimHub is a 32-bit
        /// host and the cabinet is a 32-bit game.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        private const uint MemCommit = 0x1000;
        private const uint PageNoAccess = 0x01;
        private const uint PageGuard = 0x100;

        /// <summary>Every protection with execute in it: PAGE_EXECUTE, _READ, _READWRITE and
        /// _WRITECOPY are 0x10, 0x20, 0x40 and 0x80, so one mask covers the lot.</summary>
        private const uint PageExecuteAny = 0xF0;

        /// <summary>DIAGNOSTIC (2026-09-06): find a known string in the game and describe what
        /// surrounds it.
        ///
        /// Written for the leaderboard. With no network the cabinet fills its ONLINE board with
        /// placeholder rows, every one under the name SEGA with the same six minute time, and a
        /// repeated known string is what makes a blind search tractable: the hits land at a regular
        /// stride, that stride IS the row size, and the bytes around each hit are the row.
        ///
        /// STRICTLY READ ONLY, like the rest of this class. It walks committed readable regions
        /// with VirtualQueryEx and reads them. It writes nothing, anywhere.
        ///
        /// Expensive by nature, so it is one-shot and deliberately triggered, never on a timer.</summary>
        public string ScanForString(string needle, int maxHits)
        {
            if (string.IsNullOrEmpty(needle)) return "no search text given.";
            var needles = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("ascii", System.Text.Encoding.ASCII.GetBytes(needle)),
                new KeyValuePair<string, byte[]>("utf16", System.Text.Encoding.Unicode.GetBytes(needle)),
            };
            return ScanFor(needle, needles, maxHits, false);
        }

        /// <summary>DIAGNOSTIC (2026-09-06): find a NUMBER rather than a string.
        ///
        /// The leaderboard turned out not to hold its placeholder name as text: a search for SEGA
        /// found only font filenames and copyright strings, so the rows must carry a time and an
        /// index into a name table instead. A time is a number, and the placeholder rows all share
        /// one, which makes it the same kind of repeated known value that a name would have been.
        ///
        /// Searched in every plausible encoding at once, because how the game stores a duration is
        /// exactly what we do not know: 32 and 16 bit integers, and a float or a double, in case it
        /// keeps seconds the way it keeps most other quantities.</summary>
        public string ScanForNumber(double value, int maxHits)
        {
            return ScanForNumbers(new[] { value }, maxHits);
        }

        /// <summary>Several candidate values in ONE pass.
        ///
        /// A memory walk costs a quarter of a gigabyte of reads, and the interesting question is
        /// almost never a single value: six minutes could be stored as milliseconds, as frames, or
        /// as centiseconds, and running three walks to find out wastes two of them. Every value is
        /// searched in every encoding at once and each hit remembers which candidate it came from,
        /// so one run says which unit the game actually uses.</summary>
        public string ScanForNumbers(double[] values, int maxHits)
        {
            if (values == null || values.Length == 0) return "no values given.";
            var needles = new List<KeyValuePair<string, byte[]>>();
            var label = new System.Text.StringBuilder();
            foreach (double value in values)
            {
                string v = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (label.Length > 0) label.Append(", ");
                label.Append(v);
                bool whole = value == Math.Floor(value);
                if (whole && value >= int.MinValue && value <= int.MaxValue)
                    needles.Add(new KeyValuePair<string, byte[]>(v + "/int32", BitConverter.GetBytes((int)value)));
                // Deliberately NO int16. Two bytes match far too much to be evidence: searching
                // 21600 that way found the pair 60 54 inside x86 instructions, and then, once code
                // was excluded, inside the low half of pointers like 0x01f95460. Both times it
                // buried the four byte candidates under hundreds of coincidences. A time that
                // really is stored in sixteen bits will have to be asked for by name.
                needles.Add(new KeyValuePair<string, byte[]>(v + "/float", BitConverter.GetBytes((float)value)));
                needles.Add(new KeyValuePair<string, byte[]>(v + "/double", BitConverter.GetBytes(value)));
            }
            return ScanFor(label.ToString(), needles, maxHits, true);
        }

        /// <summary>True once every candidate has as many hits as it is allowed, which is what
        /// ends the walk. Sharing one budget let a single noisy needle end the search for all.</summary>
        private static bool AllFull(List<KeyValuePair<string, byte[]>> needles,
            Dictionary<string, int> perNeedle, int maxHits)
        {
            foreach (var n in needles)
            {
                int c;
                if (!perNeedle.TryGetValue(n.Key, out c) || c < maxHits) return false;
            }
            return true;
        }

        private string ScanFor(string label, List<KeyValuePair<string, byte[]>> needles, int maxHits, bool numeric)
        {
            if (_h == IntPtr.Zero) return "not attached to the game.";
            if (needles == null || needles.Count == 0) return "nothing to search for.";
            if (maxHits <= 0) maxHits = 64;

            var hits = new List<uint>();
            var kinds = new List<string>();
            long scanned = 0;
            const long ScanCap = 768L * 1024 * 1024;
            const int Chunk = 1 << 20;
            byte[] buf = new byte[Chunk + 64];

            // Numbers are alignment-checked; text is not. Passed down rather than inferred inside
            // the matcher, which cannot tell a two byte string from a two byte integer.
            bool aligned = numeric;
            var perNeedle = new Dictionary<string, int>(StringComparer.Ordinal);

            uint address = 0x00010000;
            while (address < 0x7FFE0000 && !AllFull(needles, perNeedle, maxHits) && scanned < ScanCap)
            {
                MemoryBasicInformation mbi;
                if (VirtualQueryEx(_h, (IntPtr)address, out mbi,
                        (uint)Marshal.SizeOf(typeof(MemoryBasicInformation))) == 0)
                    break;

                uint regionBase = (uint)mbi.BaseAddress.ToInt64();
                uint regionSize = (uint)mbi.RegionSize.ToInt64();
                if (regionSize == 0) break;

                // Executable pages are skipped outright. The first number search drowned in them:
                // searching for 21600 as an int16 found the byte pair 60 54 inside x86 instruction
                // encodings 242 times, filled the hit cap after 57 of 246 MB, and never reached the
                // heap where a table would actually live. Nothing we are looking for is code.
                bool readable = mbi.State == MemCommit
                    && (mbi.Protect & PageNoAccess) == 0
                    && (mbi.Protect & PageGuard) == 0
                    && (mbi.Protect & PageExecuteAny) == 0;

                if (readable)
                {
                    for (uint off = 0; off < regionSize && !AllFull(needles, perNeedle, maxHits); off += Chunk)
                    {
                        // A few bytes of overlap, so a match cannot fall across a chunk boundary.
                        int want = (int)Math.Min((long)Chunk + 32, regionSize - off);
                        if (want <= 0) break;
                        if (!ReadInto(regionBase + off, buf, want)) continue;
                        scanned += want;
                        foreach (var n in needles)
                        {
                            int already;
                            perNeedle.TryGetValue(n.Key, out already);
                            if (already >= maxHits) continue;
                            int before = hits.Count;
                            FindIn(buf, want, n.Value, regionBase + off, hits, kinds, n.Key,
                                   maxHits, aligned, already);
                            perNeedle[n.Key] = already + (hits.Count - before);
                        }
                    }
                }

                uint next = regionBase + regionSize;
                if (next <= address) break;
                address = next;
            }

            return BuildReport(label, hits, kinds, scanned);
        }

        /// <summary>Turn a set of hits into the report: what hit, how often, at what spacing, and
        /// what surrounds the first few of each. Shared by the exact and the range scanners, since
        /// how a hit was found says nothing about how it should be read.</summary>
        private string BuildReport(string label, List<uint> hits, List<string> kinds, long scanned)
        {
            if (hits.Count == 0)
                return "no " + label + " found in " + (scanned / (1024 * 1024)) + " MB of readable memory.";

            var sb = new System.Text.StringBuilder();
            sb.Append("found ").Append(hits.Count).Append(" hits for ").Append(label).Append(" in ")
              .Append(scanned / (1024 * 1024)).Append(" MB.");

            // Which candidate actually hit, and how often. With several values and encodings in
            // flight this line alone usually answers the question: the right one lands dozens of
            // times in a row while the rest land once or twice in unrelated data.
            var perKind = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string k in kinds)
            {
                int n;
                perKind[k] = perKind.TryGetValue(k, out n) ? n + 1 : 1;
            }
            string leader = null;
            int leaderCount = 0;
            foreach (var kv in perKind)
            {
                sb.Append(' ').Append(kv.Key).Append(" x").Append(kv.Value).Append(';');
                if (kv.Value > leaderCount) { leader = kv.Key; leaderCount = kv.Value; }
            }

            // The gap between consecutive hits is the row stride, when they are a table.
            // Sorted first. With several needles running at once the hits arrive interleaved,
            // and both the stride and the listing below read neighbours as neighbours.
            var order = new int[hits.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            var byAddress = hits;
            Array.Sort(order, delegate (int x, int y) { return byAddress[x].CompareTo(byAddress[y]); });
            var sortedHits = new List<uint>(hits.Count);
            var sortedKinds = new List<string>(hits.Count);
            foreach (int i in order) { sortedHits.Add(hits[i]); sortedKinds.Add(kinds[i]); }
            hits = sortedHits;
            kinds = sortedKinds;

            // Each candidate gets its own section. Following only the winner was a mistake: the
            // loudest candidate has twice now been the wrong one, and the quieter four byte values
            // sitting underneath it were never shown at all.
            var ranked = new List<string>(perKind.Keys);
            ranked.Sort(delegate (string x, string y) { return perKind[y].CompareTo(perKind[x]); });

            byte[] row = new byte[96];
            // Every candidate that hit anything gets a section, not just the busiest few. When the
            // needle is a REAL time rather than a guessed placeholder, one or two hits is the
            // expected and correct answer, so ranking by count would hide the right value behind a
            // wrong one that happened to appear in more unrelated data.
            int sections = Math.Min(ranked.Count, 8);
            for (int k = 0; k < sections; k++)
            {
                string kind = ranked[k];
                var mine = new List<uint>();
                for (int i = 0; i < hits.Count; i++) if (kinds[i] == kind) mine.Add(hits[i]);
                if (mine.Count == 0) continue;

                // A stride is only meaningful within ONE candidate: two values interleaved produce
                // gaps belonging to neither.
                var strides = new Dictionary<uint, int>();
                for (int i = 1; i < mine.Count; i++)
                {
                    uint d = mine[i] - mine[i - 1];
                    if (d == 0 || d > 0x4000) continue;
                    int n;
                    strides[d] = strides.TryGetValue(d, out n) ? n + 1 : 1;
                }
                uint bestStride = 0;
                int bestCount = 0;
                foreach (var kv in strides) if (kv.Value > bestCount) { bestStride = kv.Key; bestCount = kv.Value; }

                sb.Append("\n== ").Append(kind).Append(": ").Append(mine.Count).Append(" hits");
                if (bestCount > 0)
                    sb.Append(", most common gap 0x").Append(bestStride.ToString("x"))
                      .Append(" (").Append(bestStride).Append(" bytes) x").Append(bestCount);

                int show = Math.Min(mine.Count, 6);
                for (int i = 0; i < show; i++)
                {
                    uint at = mine[i];
                    uint from = at >= 32 ? at - 32 : at;
                    sb.Append("\n  0x").Append(at.ToString("x8"));
                    if (i > 0) sb.Append(" (+0x").Append((mine[i] - mine[i - 1]).ToString("x")).Append(")");
                    if (!ReadInto(from, row, row.Length)) continue;
                    for (int line = 0; line < row.Length; line += 32)
                    {
                        sb.Append("\n    0x").Append((from + (uint)line).ToString("x8")).Append("  ");
                        for (int j = line; j < line + 32; j++) sb.Append(row[j].ToString("x2"));
                        sb.Append("  ");
                        for (int j = line; j < line + 32; j++)
                            sb.Append(row[j] >= 0x20 && row[j] < 0x7f ? (char)row[j] : '.');
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>Find one needle in a buffer.
        ///
        /// Numbers must be ALIGNED to their own width, which is the cheapest way to tell a table
        /// entry from a coincidence: a real int32 in an array sits on a four byte boundary, while
        /// the same four bytes appearing part way through an instruction almost never does.
        /// Text has no such rule and is matched anywhere.
        ///
        /// The cap is per needle rather than shared, so one noisy candidate cannot starve the
        /// others out of the search.</summary>
        /// <summary>DIAGNOSTIC (2026-09-06): find several values sitting NEAR each other.
        ///
        /// Every single-value search has failed for one reason: 2'19''932 is almost certainly not
        /// stored as one number at all. Searching milliseconds found the neighbouring values of a
        /// replay series but never 139932 itself, and the float and double windows found nothing,
        /// so the game keeps times as integers. What is left is the obvious arrangement for a game
        /// that PRINTS m'ss''mmm: three fields, 2, 19 and 932, side by side in a record.
        ///
        /// No single value can find that. 932 alone is common, 19 and 2 are everywhere. Together,
        /// within a few bytes of each other, they are rare.
        ///
        /// So the search anchors on the LARGEST value, which is the rarest, and only then reads the
        /// bytes around each hit looking for the others. They are accepted as a 32-bit integer, a
        /// 16-bit integer or a single byte, because a record that packs minutes into a byte and
        /// milliseconds into a short is exactly the sort of thing this era of game does.</summary>
        public string ScanForCluster(double[] values, int windowBytes, int maxHits)
        {
            if (_h == IntPtr.Zero) return "not attached to the game.";
            if (values == null || values.Length < 2) return "give at least two values to look for.";
            if (maxHits <= 0) maxHits = 32;
            if (windowBytes <= 0 || windowBytes > 512) windowBytes = 64;

            // The rarest value leads. A search anchored on 2 would hit every other word in memory.
            int anchorAt = 0;
            for (int i = 1; i < values.Length; i++)
                if (Math.Abs(values[i]) > Math.Abs(values[anchorAt])) anchorAt = i;
            int anchorValue = (int)values[anchorAt];

            byte[] anchor32 = BitConverter.GetBytes(anchorValue);
            byte[] anchor16 = BitConverter.GetBytes((short)anchorValue);
            bool anchorFitsShort = anchorValue >= short.MinValue && anchorValue <= short.MaxValue;

            var hits = new List<uint>();
            var kinds = new List<string>();
            long scanned = 0;
            const long ScanCap = 768L * 1024 * 1024;
            const int Chunk = 1 << 20;
            byte[] buf = new byte[Chunk + 64];
            byte[] window = new byte[windowBytes * 2];

            uint address = 0x00010000;
            while (address < 0x7FFE0000 && hits.Count < maxHits && scanned < ScanCap)
            {
                MemoryBasicInformation mbi;
                if (VirtualQueryEx(_h, (IntPtr)address, out mbi,
                        (uint)Marshal.SizeOf(typeof(MemoryBasicInformation))) == 0)
                    break;

                uint regionBase = (uint)mbi.BaseAddress.ToInt64();
                uint regionSize = (uint)mbi.RegionSize.ToInt64();
                if (regionSize == 0) break;

                bool readable = mbi.State == MemCommit
                    && (mbi.Protect & PageNoAccess) == 0
                    && (mbi.Protect & PageGuard) == 0
                    && (mbi.Protect & PageExecuteAny) == 0;

                if (readable)
                {
                    for (uint off = 0; off < regionSize && hits.Count < maxHits; off += Chunk)
                    {
                        int want = (int)Math.Min((long)Chunk + 32, regionSize - off);
                        if (want <= 0) break;
                        if (!ReadInto(regionBase + off, buf, want)) continue;
                        scanned += want;
                        uint start = regionBase + off;

                        for (int i = 0; i + 2 <= want && hits.Count < maxHits; i += 2)
                        {
                            bool is32 = i + 4 <= want && Same(buf, i, anchor32);
                            bool is16 = !is32 && anchorFitsShort && Same(buf, i, anchor16);
                            if (!is32 && !is16) continue;

                            uint at = start + (uint)i;
                            uint from = at >= (uint)windowBytes ? at - (uint)windowBytes : 0;
                            if (!ReadInto(from, window, window.Length)) continue;

                            // Every other value must appear somewhere in that window.
                            bool all = true;
                            for (int v = 0; v < values.Length && all; v++)
                            {
                                if (v == anchorAt) continue;
                                if (!Present(window, (int)values[v])) all = false;
                            }
                            if (!all) continue;

                            hits.Add(at);
                            kinds.Add(is32 ? "cluster/int32" : "cluster/int16");
                        }
                    }
                }

                uint next = regionBase + regionSize;
                if (next <= address) break;
                address = next;
            }

            var label = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) label.Append(" + ");
                label.Append(values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            label.Append(" within ").Append(windowBytes).Append(" bytes");
            return BuildReport(label.ToString(), hits, kinds, scanned);
        }

        private static bool Same(byte[] buf, int at, byte[] needle)
        {
            for (int j = 0; j < needle.Length; j++) if (buf[at + j] != needle[j]) return false;
            return true;
        }

        /// <summary>Does this value appear in the window, as a word, a short or a byte? Unaligned
        /// matches count here: this is a local check over a few dozen bytes, so the false positive
        /// rate that ruled alignment in for a whole-memory sweep does not apply.</summary>
        private static bool Present(byte[] window, int value)
        {
            byte[] as32 = BitConverter.GetBytes(value);
            for (int i = 0; i + 4 <= window.Length; i++) if (Same(window, i, as32)) return true;
            if (value >= short.MinValue && value <= short.MaxValue)
            {
                byte[] as16 = BitConverter.GetBytes((short)value);
                for (int i = 0; i + 2 <= window.Length; i++) if (Same(window, i, as16)) return true;
            }
            if (value >= 0 && value <= 255)
                for (int i = 0; i < window.Length; i++) if (window[i] == (byte)value) return true;
            return false;
        }

        /// <summary>One candidate interpretation of a value: a name, a width, and the window a
        /// match must fall inside.</summary>
        private sealed class RangeNeedle
        {
            public string Label;
            public int Width;      // 4 for int32 and float, 8 for double
            public bool IsFloat;
            public bool IsDouble;
            public double Lo, Hi;
        }

        /// <summary>DIAGNOSTIC (2026-09-06): find a TIME by the range it must fall in, rather than
        /// by an exact value.
        ///
        /// Searching for a known lap time as an exact integer found nothing at all, which is itself
        /// the clue: a duration built by accumulating frame times is a float or a double whose bits
        /// will not equal the float of the rounded figure the game prints. 2'19''932 on screen can
        /// be 139.93199920654297 in memory, and an exact search misses it by a hair.
        ///
        /// So every plausible unit is expressed as a window instead: milliseconds and centiseconds
        /// as integers, and seconds as a float and a double, each wide enough to cover the rounding
        /// the display does and narrow enough that a hit means something.</summary>
        public string ScanForTime(double milliseconds, int maxHits)
        {
            if (_h == IntPtr.Zero) return "not attached to the game.";
            if (maxHits <= 0) maxHits = 64;

            double sec = milliseconds / 1000.0;
            var needles = new List<RangeNeedle>
            {
                // A millisecond integer is exact in principle, but allow a tick either way in case
                // the display rounds rather than truncates.
                new RangeNeedle { Label = "ms/int32", Width = 4, Lo = milliseconds - 2, Hi = milliseconds + 2 },
                new RangeNeedle { Label = "centis/int32", Width = 4, Lo = milliseconds / 10.0 - 1, Hi = milliseconds / 10.0 + 1 },
                // Half a millisecond either side: wide enough for any accumulation error that still
                // prints the same figure, narrow enough to exclude a neighbouring time.
                new RangeNeedle { Label = "sec/float", Width = 4, IsFloat = true, Lo = sec - 0.0005, Hi = sec + 0.0005 },
                new RangeNeedle { Label = "sec/double", Width = 8, IsDouble = true, Lo = sec - 0.0005, Hi = sec + 0.0005 },
                // And frames, in case the clock counts them and the display converts.
                new RangeNeedle { Label = "frames/int32", Width = 4, Lo = sec * 60.0 - 1, Hi = sec * 60.0 + 1 },
            };

            var hits = new List<uint>();
            var kinds = new List<string>();
            var perNeedle = new Dictionary<string, int>(StringComparer.Ordinal);
            long scanned = 0;
            const long ScanCap = 768L * 1024 * 1024;
            const int Chunk = 1 << 20;
            byte[] buf = new byte[Chunk + 64];

            uint address = 0x00010000;
            while (address < 0x7FFE0000 && scanned < ScanCap)
            {
                MemoryBasicInformation mbi;
                if (VirtualQueryEx(_h, (IntPtr)address, out mbi,
                        (uint)Marshal.SizeOf(typeof(MemoryBasicInformation))) == 0)
                    break;

                uint regionBase = (uint)mbi.BaseAddress.ToInt64();
                uint regionSize = (uint)mbi.RegionSize.ToInt64();
                if (regionSize == 0) break;

                bool readable = mbi.State == MemCommit
                    && (mbi.Protect & PageNoAccess) == 0
                    && (mbi.Protect & PageGuard) == 0
                    && (mbi.Protect & PageExecuteAny) == 0;

                if (readable)
                {
                    for (uint off = 0; off < regionSize; off += Chunk)
                    {
                        int want = (int)Math.Min((long)Chunk + 32, regionSize - off);
                        if (want <= 0) break;
                        if (!ReadInto(regionBase + off, buf, want)) continue;
                        scanned += want;

                        foreach (RangeNeedle n in needles)
                        {
                            int already;
                            perNeedle.TryGetValue(n.Label, out already);
                            if (already >= maxHits) continue;

                            uint start = regionBase + off;
                            int step = n.Width;
                            int begin = (int)((step - (start % (uint)step)) % (uint)step);
                            for (int i = begin; i + n.Width <= want && already < maxHits; i += step)
                            {
                                double v;
                                if (n.IsDouble) v = BitConverter.ToDouble(buf, i);
                                else if (n.IsFloat) v = BitConverter.ToSingle(buf, i);
                                else v = BitConverter.ToInt32(buf, i);
                                if (double.IsNaN(v) || v < n.Lo || v > n.Hi) continue;
                                hits.Add(start + (uint)i);
                                kinds.Add(n.Label);
                                already++;
                            }
                            perNeedle[n.Label] = already;
                        }
                    }
                }

                uint next = regionBase + regionSize;
                if (next <= address) break;
                address = next;
            }

            return BuildReport(milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                               + " ms as a range", hits, kinds, scanned);
        }

        private static void FindIn(byte[] buf, int len, byte[] needle, uint baseAddr,
            List<uint> hits, List<string> kinds, string kind, int maxHits, bool aligned, int already)
        {
            if (needle.Length == 0 || len < needle.Length) return;
            int found = already;
            for (int i = 0; i <= len - needle.Length && found < maxHits; i++)
            {
                if (buf[i] != needle[0]) continue;
                if (aligned && ((baseAddr + (uint)i) % (uint)needle.Length) != 0) continue;
                int j = 1;
                while (j < needle.Length && buf[i + j] == needle[j]) j++;
                if (j != needle.Length) continue;
                hits.Add(baseAddr + (uint)i);
                kinds.Add(kind);
                found++;
                i += needle.Length - 1;
            }
        }

        /// <summary>DIAGNOSTIC (2026-09-06): hunt for the section counter the game draws on its
        /// own HUD. A raw dump is the wrong instrument for a block this size, because most of it
        /// changes every frame; what a section index looks like instead is a SMALL integer that
        /// steps by exactly one and never moves otherwise. So this watches every word across a
        /// race and reports only the words that behaved that way, which is a handful rather than
        /// hundreds. Delete once the field is found and named.</summary>
        public string ScanCounters(bool restart)
        {
            string a = ScanWindow("prog", _lastProgressVa, ProgressStride, ref _watchProgress, restart);
            string b = ScanWindow("hud", _lastHudBlockVa, HudScanBytes, ref _watchHud, restart);
            if (a == null && b == null) return null;
            if (a == null) return b;
            if (b == null) return a;
            return a + " | " + b;
        }

        /// <summary>How much of the HUD block to watch. The mapped reads reach +0x610, so this
        /// covers them with room over. A block shorter than this simply fails to read and the
        /// window shrinks until it succeeds.</summary>
        private const int HudScanBytes = 0x800;

        /// <summary>What one watched word has done since the race began.</summary>
        private sealed class WordWatch
        {
            public uint Va;
            public int Len;
            public bool Primed;
            public int[] Last, Min, Max, Changes;
            public bool[] StepsByOne;
        }

        private WordWatch _watchProgress, _watchHud;

        private string ScanWindow(string tag, uint va, int len, ref WordWatch w, bool restart)
        {
            if (va == 0) return null;

            byte[] buf = null;
            // Shrink until the read lands: the end of a block is not known from here, and a
            // window that runs off it would otherwise report nothing at all.
            for (int size = len; size >= 0x80; size /= 2)
            {
                byte[] tryBuf = new byte[size];
                if (ReadInto(va, tryBuf, size)) { buf = tryBuf; len = size; break; }
            }
            if (buf == null) return null;

            int words = len / 4;
            if (w == null || restart || w.Va != va || w.Len != len)
            {
                w = new WordWatch
                {
                    Va = va,
                    Len = len,
                    Primed = false,
                    Last = new int[words],
                    Min = new int[words],
                    Max = new int[words],
                    Changes = new int[words],
                    StepsByOne = new bool[words],
                };
                for (int i = 0; i < words; i++) w.StepsByOne[i] = true;
            }

            for (int i = 0; i < words; i++)
            {
                int v = BitConverter.ToInt32(buf, i * 4);
                if (!w.Primed)
                {
                    w.Last[i] = v; w.Min[i] = v; w.Max[i] = v;
                    continue;
                }
                if (v == w.Last[i]) continue;
                // A counter either advances by one or goes back to the start of its range.
                if (v - w.Last[i] != 1 && v > w.Min[i]) w.StepsByOne[i] = false;
                w.Changes[i]++;
                w.Last[i] = v;
                if (v < w.Min[i]) w.Min[i] = v;
                if (v > w.Max[i]) w.Max[i] = v;
            }
            if (!w.Primed) { w.Primed = true; return null; }

            var strict = new System.Text.StringBuilder();
            var loose = new System.Text.StringBuilder();
            int looseCount = 0;
            for (int i = 0; i < words; i++)
            {
                if (w.Changes[i] < 1) continue;
                if (w.Min[i] < 0 || w.Max[i] > 63) continue;   // a section index is small
                var target = w.StepsByOne[i] ? strict : loose;
                if (!w.StepsByOne[i] && ++looseCount > 12) continue;
                if (target.Length > 0) target.Append(' ');
                target.Append("+0x").Append((i * 4).ToString("x3")).Append('=').Append(w.Last[i])
                      .Append('(').Append(w.Min[i]).Append("..").Append(w.Max[i])
                      .Append(", x").Append(w.Changes[i]).Append(')');
            }
            if (strict.Length == 0 && loose.Length == 0) return null;
            var sb = new System.Text.StringBuilder(tag);
            if (strict.Length > 0) sb.Append(" step+1: ").Append(strict);
            if (loose.Length > 0) sb.Append(" small: ").Append(loose);
            return sb.ToString();
        }

        /// <summary>DIAGNOSTIC (2026-09-06): the player's whole progress record, word by word.
        /// The struct is 0xb4 bytes and only two of its words are mapped, laps at +0x8 and course
        /// fraction at +0xc. What this is looking for is where the game keeps the SECTION the car
        /// is in, which should read as a small integer that steps by one and resets with the lap.
        /// Both int and float are printed for every word, because which one a word is cannot be
        /// told from here. Delete once the section field is found and named.</summary>
        public string DumpProgress()
        {
            uint va = _lastProgressVa;
            if (va == 0) return null;
            byte[] buf = new byte[ProgressStride];
            if (!ReadInto(va, buf, ProgressStride)) return null;
            var sb = new System.Text.StringBuilder();
            for (int off = 0; off + 4 <= ProgressStride; off += 4)
            {
                int i = BitConverter.ToInt32(buf, off);
                float fl = BitConverter.ToSingle(buf, off);
                if (i == 0) continue;   // a whole struct of zeroes tells us nothing
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("+0x").Append(off.ToString("x2")).Append('=').Append(i);
                // Show the float reading too, but only when it looks like a real one.
                if (fl > -1e6f && fl < 1e6f && Math.Abs(fl) > 1e-6f)
                    sb.Append('/').Append(fl.ToString("0.####"));
            }
            return sb.Length == 0 ? "(all zero)" : sb.ToString();
        }

        private uint _lastProgressVa;
        private uint _lastHudBlockVa;

        /// <summary>The course's name out of the exe's own table, which is plain ASCII.</summary>
        /// <summary>Read the section times and their sum.
        ///
        /// The array is behind a pointer at race + 0x230 and fills in one entry per checkpoint, so
        /// mid-run it is partial and that is correct: the sum is only the finished time once the
        /// run has reached the goal.
        ///
        /// The count is bounded by a call on the course object in the game (0x008d7297), which is
        /// not worth reproducing, so entries are taken until a zero. A section that genuinely took
        /// no time cannot happen: every one is seconds long.</summary>
        private void ReadSectionTimes(uint race, ref Id8Sample s)
        {
            uint arr = Deref(race + RaceToSectionTimes);
            if (arr == 0) return;

            var times = new List<int>(MaxSections);
            int total = 0;
            for (int i = 0; i < MaxSections; i++)
            {
                if (!I32((uint)(arr + 4 * i), out int v) || v <= 0) break;
                times.Add(v);
                total += v;
            }
            if (times.Count == 0) return;

            s.SectionTimes = times.ToArray();
            s.SectionSumMs = total;
        }

        private string CourseNameFor(int courseOrderIndex)
        {
            if (courseOrderIndex < 0 || courseOrderIndex > 15) return null;
            uint ptr = Deref((uint)(CourseNameTableVa + 4 * courseOrderIndex));
            if (ptr == 0) return null;
            byte[] buf = new byte[32];
            if (!ReadInto(ptr, buf, 32)) return null;
            int n = Array.IndexOf(buf, (byte)0);
            if (n <= 0) return null;
            for (int i = 0; i < n; i++) if (buf[i] < 0x20 || buf[i] > 0x7e) return null;
            return System.Text.Encoding.ASCII.GetString(buf, 0, n);
        }

        /// <summary>Every readable string in the player record, once. The name offset is only a
        /// LIKELY entry in the map, and a screen showing the wrong field is worse than one showing
        /// none, so this says what is actually there rather than trusting the label.</summary>
        private void ScanPlayerRecordForNames(uint player)
        {
            if (player == 0) return;
            // The whole cPlayer record. The first 0x200 bytes held only a venue name, and the
            // owner's actual name is latin text stored somewhere else in here.
            const int span = 0xc78;
            byte[] rec = new byte[span];
            if (!ReadInto(player, rec, span)) return;

            var found = new System.Text.StringBuilder();
            int runStart = -1;
            for (int i = 0; i <= span; i++)
            {
                bool printable = i < span && rec[i] >= 0x20 && rec[i] < 0x7f;
                if (printable) { if (runStart < 0) runStart = i; continue; }
                if (runStart >= 0 && i - runStart >= 3)
                {
                    if (found.Length > 0) found.Append("  ");
                    found.Append("+0x").Append((runStart).ToString("x"))
                         .Append("='")
                         .Append(System.Text.Encoding.ASCII.GetString(rec, runStart, i - runStart))
                         .Append("'");
                }
                runStart = -1;
            }
            Emit(found.Length > 0
                ? "player record strings: " + found
                : "player record holds no readable strings in its first 0x200 bytes, so the name is "
                  + "either elsewhere, stored as Shift-JIS multibyte, or this is a guest with no card");
        }

        /// <summary>Player name, team and the garage car. Readable in the menus, so this is what an
        /// idle display shows when there is no race to report on.</summary>
        private void ReadIdentity(uint session, ref Id8Sample s)
        {
            uint player = Deref(session + SessionToPlayer);
            if (player == 0) return;
            int kind;
            if (!I32(player + PlayerRecordKind, out kind) || kind != 3) return;  // not a human record

            s.PlayerName = ReadShiftJis(player + PlayerName, 32);

            // Is a real driver signed in? Two independent tests, because the card id offset is only
            // a SPECULATIVE entry in the map and testing it alone left the attract message up long
            // after a card had been swiped. The name is the more reliable witness: the game holds
            // its own placeholder, katakana for "player", until somebody signs in, so a name that
            // is present and is NOT that placeholder means a real one.
            int cardId;
            bool cardIdSaysYes = I32(player + PlayerCardId, out cardId) && cardId >= 0;
            bool nameSaysYes = !string.IsNullOrEmpty(s.PlayerName)
                               && !string.Equals(s.PlayerName, PlaceholderName, StringComparison.Ordinal);
            s.HasCard = cardIdSaysYes || nameSaysYes;
            s.CardIdPresent = cardIdSaysYes;
            s.PlayerNamed = nameSaysYes;
            s.CardId = cardId;
            s.PlayerNameAscii = ToAscii(s.PlayerName);
            s.TeamName = ReadShiftJis(player + PlayerTeamName, 32);

            byte[] two = new byte[2];
            if (ReadInto(player + PlayerSelectedCar, two, 2))
            {
                int sel = BitConverter.ToInt16(two, 0);
                if (sel >= 0 && sel <= 2 &&
                    ReadInto((uint)(player + PlayerGarageSpecs + sel * GarageSpecStride), two, 2))
                {
                    s.GarageCar = Id8CarTable.Find(BitConverter.ToUInt16(two, 0));
                }
            }
        }

        /// <summary>Katakana romanised, so a Japanese name can appear on a panel that only draws
        /// ASCII. Longest syllables first, because the small kana modify the one before them and a
        /// naive per-character pass turns SHA into SIYA.
        ///
        /// This is a Japanese arcade game and its default name is katakana, so without this the
        /// wheel simply shows a blank row where a driver's name should be.</summary>
        private static readonly string[][] KatakanaRomaji = new string[][]
        {
            new[]{"キャ","kya"}, new[]{"キュ","kyu"}, new[]{"キョ","kyo"},
            new[]{"シャ","sha"}, new[]{"シュ","shu"}, new[]{"ショ","sho"},
            new[]{"チャ","cha"}, new[]{"チュ","chu"}, new[]{"チョ","cho"},
            new[]{"ニャ","nya"}, new[]{"ニュ","nyu"}, new[]{"ニョ","nyo"},
            new[]{"ヒャ","hya"}, new[]{"ヒュ","hyu"}, new[]{"ヒョ","hyo"},
            new[]{"ミャ","mya"}, new[]{"ミュ","myu"}, new[]{"ミョ","myo"},
            new[]{"リャ","rya"}, new[]{"リュ","ryu"}, new[]{"リョ","ryo"},
            new[]{"ギャ","gya"}, new[]{"ギュ","gyu"}, new[]{"ギョ","gyo"},
            new[]{"ジャ","ja"},  new[]{"ジュ","ju"},  new[]{"ジョ","jo"},
            new[]{"ビャ","bya"}, new[]{"ビュ","byu"}, new[]{"ビョ","byo"},
            new[]{"ピャ","pya"}, new[]{"ピュ","pyu"}, new[]{"ピョ","pyo"},
            new[]{"ティ","ti"},  new[]{"ディ","di"},  new[]{"ファ","fa"},
            new[]{"フィ","fi"},  new[]{"フェ","fe"},  new[]{"フォ","fo"},
            new[]{"ウィ","wi"},  new[]{"ウェ","we"},  new[]{"ヴ","vu"},
            new[]{"ア","a"}, new[]{"イ","i"}, new[]{"ウ","u"}, new[]{"エ","e"}, new[]{"オ","o"},
            new[]{"カ","ka"}, new[]{"キ","ki"}, new[]{"ク","ku"}, new[]{"ケ","ke"}, new[]{"コ","ko"},
            new[]{"サ","sa"}, new[]{"シ","shi"}, new[]{"ス","su"}, new[]{"セ","se"}, new[]{"ソ","so"},
            new[]{"タ","ta"}, new[]{"チ","chi"}, new[]{"ツ","tsu"}, new[]{"テ","te"}, new[]{"ト","to"},
            new[]{"ナ","na"}, new[]{"ニ","ni"}, new[]{"ヌ","nu"}, new[]{"ネ","ne"}, new[]{"ノ","no"},
            new[]{"ハ","ha"}, new[]{"ヒ","hi"}, new[]{"フ","fu"}, new[]{"ヘ","he"}, new[]{"ホ","ho"},
            new[]{"マ","ma"}, new[]{"ミ","mi"}, new[]{"ム","mu"}, new[]{"メ","me"}, new[]{"モ","mo"},
            new[]{"ヤ","ya"}, new[]{"ユ","yu"}, new[]{"ヨ","yo"},
            new[]{"ラ","ra"}, new[]{"リ","ri"}, new[]{"ル","ru"}, new[]{"レ","re"}, new[]{"ロ","ro"},
            new[]{"ワ","wa"}, new[]{"ヲ","wo"}, new[]{"ン","n"},
            new[]{"ガ","ga"}, new[]{"ギ","gi"}, new[]{"グ","gu"}, new[]{"ゲ","ge"}, new[]{"ゴ","go"},
            new[]{"ザ","za"}, new[]{"ジ","ji"}, new[]{"ズ","zu"}, new[]{"ゼ","ze"}, new[]{"ゾ","zo"},
            new[]{"ダ","da"}, new[]{"ヂ","ji"}, new[]{"ヅ","zu"}, new[]{"デ","de"}, new[]{"ド","do"},
            new[]{"バ","ba"}, new[]{"ビ","bi"}, new[]{"ブ","bu"}, new[]{"ベ","be"}, new[]{"ボ","bo"},
            new[]{"パ","pa"}, new[]{"ピ","pi"}, new[]{"プ","pu"}, new[]{"ペ","pe"}, new[]{"ポ","po"},
            new[]{"ッ",""}, new[]{"ー",""}, new[]{"・"," "}, new[]{"　"," "},
        };

        /// <summary>The best ASCII rendering of a name, for a panel that cannot draw anything else.
        /// Returns null when nothing survives, so a caller can say GUEST rather than show a blank
        /// row that looks like a fault.</summary>
        public static string ToAscii(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var sb = new System.Text.StringBuilder(name.Length * 2);
            int i = 0;
            while (i < name.Length)
            {
                char c = name[i];
                if (c >= 0x20 && c < 0x7f) { sb.Append(c); i++; continue; }
                // Full-width ASCII, which Japanese input produces for latin letters.
                if (c >= 0xff01 && c <= 0xff5e) { sb.Append((char)(c - 0xfee0)); i++; continue; }

                bool matched = false;
                for (int k = 0; k < KatakanaRomaji.Length; k++)
                {
                    string kana = KatakanaRomaji[k][0];
                    if (i + kana.Length <= name.Length &&
                        string.CompareOrdinal(name, i, kana, 0, kana.Length) == 0)
                    {
                        sb.Append(KatakanaRomaji[k][1]);
                        i += kana.Length;
                        matched = true;
                        break;
                    }
                }
                if (!matched) i++;   // hiragana, kanji or punctuation we cannot render
            }
            string outp = sb.ToString().Trim();
            return outp.Length == 0 ? null : outp.ToUpperInvariant();
        }

        /// <summary>A NUL-terminated Shift-JIS string, which is how the game stores names. Returns
        /// null rather than mojibake when the bytes do not decode.</summary>
        private string ReadShiftJis(uint addr, int maxBytes)
        {
            byte[] buf = new byte[maxBytes];
            if (!ReadInto(addr, buf, maxBytes)) return null;
            int n = Array.IndexOf(buf, (byte)0);
            if (n < 0) n = maxBytes;
            if (n == 0) return string.Empty;
            try { return System.Text.Encoding.GetEncoding(932).GetString(buf, 0, n).Trim(); }
            catch { return null; }
        }

        /// <summary>How hard the car just hit something, in g. The game records both a car-to-car
        /// impact magnitude and a wall impact velocity, which is far better than watching for an
        /// acceleration spike: a crash happens inside one of the game's frames, and a reader
        /// sampling asynchronously catches it only by luck. That is why the collision effect fired
        /// at the same strength whatever the crash.
        ///
        /// The wall figure is a velocity clamped to about 8 m/s, so it is turned into a comparable
        /// number by treating the impact as a stop over one frame at 60 Hz.</summary>
        private double ReadImpact(uint carState, uint carObject, uint hudBlock)
        {
            double g = 0.0;

            // Watch the COOLDOWN, not the contact flag. The flag is true for a single game frame,
            // and a reader sampling asynchronously at about the same rate misses it most times: a
            // measured race that hit a wall hard reported an impact of exactly zero from here. The
            // cooldown is set to 30 on contact and counts down, so a rise in it is an unmissable
            // edge, and the magnitude is still sitting there when we look.
            int cooldown;
            bool cooldownRose = false;
            if (I32(carState + CsCollisionCooldown, out cooldown))
            {
                cooldownRose = cooldown > _prevCollisionCooldown;
                if (cooldownRose)
                {
                    float mag;
                    if (F32(carState + CsCollisionMag, out mag)) g = Math.Abs(Finite(mag));
                    // The game also keeps the last impact in a static, which survives the frame it
                    // happened on. Whichever is larger is the honest answer.
                    float lastImpact;
                    if (F32(LastImpactVa, out lastImpact))
                    {
                        double m = Math.Abs(Finite(lastImpact)) * 60.0 / 9.81;   // metres per frame -> g
                        if (m > g) g = m;
                    }
                }
                _prevCollisionCooldown = cooldown;
            }

            // Every wall candidate, because the first choice was measured dead and guessing a
            // second time would be no better than guessing the first.
            byte[] one = new byte[1];
            bool flag7f8 = ReadInto(carState + CsWallContact, one, 1) && one[0] != 0;
            bool scrape = ReadInto(carObject + CoWallScrape, one, 1) && one[0] != 0;

            bool probe = false;
            for (int side = 0; side < 4 && !probe; side++)
                if (ReadInto((uint)(hudBlock + HudWallProbeHit + side * HudColliStride), one, 1) && one[0] != 0)
                    probe = true;

            float wvx = 0, wvz = 0;
            F32(carState + CsWallImpactVel, out wvx);
            F32(carState + CsWallImpactVel + 4, out wvz);
            bool velNow = Math.Abs(Finite(wvx)) > 0.05 || Math.Abs(Finite(wvz)) > 0.05;

            // The scrape flag is the one the car update sets and clears by name, so it leads; the
            // impact velocity backs it up for a hit hard enough to register one.
            bool wallNow = scrape || velNow || flag7f8;

            // The rising edge of EITHER kind of impact. Only the wall flag was used before, and it
            // is set only when the wall response passes the game's own threshold, so a glancing
            // scrape along a barrier never counted. A car-to-car hit did not count at all.
            bool impactEdge = (wallNow && !_prevWallContact) || cooldownRose;
            // Instrumented rather than guessed at again. Wall hits have failed to appear twice
            // now, and the two candidate sources cannot be told apart from the outside: this says
            // which flag actually moved, so the next run answers it instead of another theory.
            if (_impactTraceLines < 40 && (impactEdge || wallNow || probe))
            {
                _impactTraceLines++;
                Emit("impact: scrape=" + (scrape ? 1 : 0)
                     + " vel=" + (velNow ? 1 : 0)
                     + " flag7f8=" + (flag7f8 ? 1 : 0)
                     + " probe=" + (probe ? 1 : 0)
                     + " cooldownRose=" + (cooldownRose ? 1 : 0)
                     + " wv=" + Finite(wvx).ToString("0.0") + "/" + Finite(wvz).ToString("0.0")
                     + " magnitude=" + g.ToString("0.00") + "g"
                     + (_impactTraceLines == 40 ? "  (last impact line)" : ""));
            }
            _wallHitEdge = impactEdge;
            if (impactEdge) _hitWasCar = cooldownRose;
            _prevWallContact = wallNow;
            if (wallNow)
            {
                // Two components, lateral then longitudinal. The bigger one is the impact.
                double wall = Math.Max(Math.Abs(Finite(wvx)), Math.Abs(Finite(wvz)));
                // A velocity lost in one 60 Hz frame, expressed in g.
                double wallG = wall * 60.0 / 9.81;
                if (wallG > g) g = wallG;
            }
            return g;
        }

        /// <summary>The second root: walk the per-slot display table to a car object. Used only to
        /// tell a game that has not started yet apart from an executable whose data has moved, which
        /// are indistinguishable from the primary root alone and have completely different fixes.
        /// Returns the car object, or zero.</summary>
        private uint FindCarByAltRoot()
        {
            for (int slot = 0; slot < 2; slot++)
            {
                uint outPtr = Deref((uint)(HudBlockTableVa + 4 * slot));
                if (outPtr == 0) continue;
                // The table holds what the actor keeps at +0x6b0, which points at actor + 0x30.
                uint actor = outPtr - ActorToHudBlock;
                uint carObject = Deref(actor + ActorToCarObject);
                if (carObject == 0) continue;
                uint vt = Deref(carObject);
                if (vt != VtablePlayerA && vt != VtablePlayerB) continue;
                uint carState = Deref(carObject + CarObjectToCarState);
                if (carState == 0) continue;
                // Same three constants as the primary path. Without them this would happily accept
                // any pointer that happened to look like an address.
                float normaliser, idle;
                if (F32(carState + CsRpmNormaliser, out normaliser) &&
                    F32(carState + CsRpmIdle, out idle) &&
                    Math.Abs(normaliser - 10000f) <= 1f && Math.Abs(idle - 800f) <= 1f)
                    return carObject;
            }
            return 0;
        }

        /// <summary>Find the session root again in an executable whose data has moved.
        ///
        /// Only the STATIC addresses can move. Every offset inside a class is compiled in and needs
        /// a recompile to change, which a mod cannot do without the source. So one number, found
        /// again, restores the whole map.
        ///
        /// A code signature alone cannot do it: the instruction that writes the root is
        /// mov [abs], eax after a constructor call, and that shape appears 212 times in the image
        /// because it is how every global singleton is stored. What makes it work is that a
        /// signature does not have to IDENTIFY the root, only to produce a short list of
        /// candidates. The validator then picks the winner, and the validator is position
        /// independent: the three constants in the car state are VALUES, not addresses, so they
        /// read the same wherever the data lives.
        ///
        /// Requires a race to be running, because the car state is what proves a candidate. That is
        /// the honest limit: in the menus there is nothing to validate against and this will not
        /// find anything. Runs once, only after the fixed address has already failed.</summary>
        private uint RelocateSessionRoot()
        {
            // The module's code, read once. A modified build can move the data without moving this.
            const int MaxScanBytes = 12 * 1024 * 1024;
            int len = _moduleSize > 0 && _moduleSize < MaxScanBytes ? _moduleSize : MaxScanBytes;
            byte[] code;
            try { code = new byte[len]; }
            catch { return 0; }
            // Read in pieces: one huge read fails if any page inside is unmapped.
            const int Chunk = 0x10000;
            var have = new bool[(len + Chunk - 1) / Chunk];
            for (int off = 0, k = 0; off < len; off += Chunk, k++)
            {
                int want = Math.Min(Chunk, len - off);
                byte[] buf = new byte[want];
                if (ReadInto((uint)(_moduleBase + off), buf, want))
                {
                    Buffer.BlockCopy(buf, 0, code, off, want);
                    have[k] = true;
                }
            }

            var seen = new System.Collections.Generic.HashSet<uint>();
            for (int i = 0; i + 12 < len; i++)
            {
                // mov ecx, eax ; call rel32 ; mov [abs32], eax
                if (code[i] != 0x8b || code[i + 1] != 0xc8 || code[i + 2] != 0xe8 || code[i + 7] != 0xa3) continue;
                if (!have[i / Chunk]) continue;
                uint cand = BitConverter.ToUInt32(code, i + 8);
                if (cand < 0x10000 || cand > 0x7fffffff) continue;
                if (!seen.Add(cand)) continue;
                if (ChainProvesRoot(cand)) return cand;
            }
            return 0;
        }

        /// <summary>Does treating this address as the session root reach a car state that holds the
        /// three constants? Nothing else is accepted, so a pointer that merely looks like an
        /// address cannot pass.</summary>
        private bool ChainProvesRoot(uint rootVa)
        {
            uint session = Deref(rootVa);
            if (session == 0) return false;
            uint race = Deref(session + SessionToRace);
            if (race == 0) return false;
            int slot;
            if (!I32(race + RaceToSlot, out slot) || slot < 0 || slot > 1) return false;
            uint actor = Deref((uint)(race + RaceToActors + 4 * slot));
            if (actor == 0) return false;
            uint carObject = Deref(actor + ActorToCarObject);
            if (carObject == 0) return false;
            uint carState = Deref(carObject + CarObjectToCarState);
            if (carState == 0) return false;
            float normaliser, idle, frameTime;
            return F32(carState + CsRpmNormaliser, out normaliser)
                && F32(carState + CsRpmIdle, out idle)
                && F32(carState + CsFrameTime, out frameTime)
                && Math.Abs(normaliser - 10000f) <= 1f
                && Math.Abs(idle - 800f) <= 1f
                && Math.Abs(frameTime - (1f / 60f)) <= 0.002f;
        }

        /// <summary>Button presses and screen changes, which is all the cabinet gives us of its
        /// menus. There is no cursor or selected-item field anywhere in the map, so a press is taken
        /// as moving through the options and a change of screen as having confirmed one. That is
        /// approximate: confirming something that stays on the same screen reads as a move. The
        /// bits are carried out so the confirm button can be identified later and used directly.</summary>
        private void ReadMenuActivity(ref Id8Sample s)
        {
            uint input = Deref(InputPtrVa2);
            if (input != 0)
            {
                uint b1, b2 = 0;
                byte[] one = new byte[1];
                if (U32(input + InputButtons, out b1))
                {
                    if (ReadInto(input + InputButtons2, one, 1)) b2 = one[0];
                    ulong now = ((ulong)b2 << 32) | b1;
                    ulong went = now & ~_prevButtons;      // only what just went down
                    if (_haveButtons && went != 0)
                    {
                        s.MenuButtonPressed = true;
                        s.MenuButtonBits = (uint)(went & 0xffffffff);
                    }
                    _prevButtons = now;
                    _haveButtons = true;
                }
            }

            uint sceneMgr = Deref(SceneMgrPtrVa);
            int sceneId;
            if (sceneMgr != 0 && I32(sceneMgr + SceneMgrToSceneId, out sceneId))
            {
                s.SceneId = sceneId;
                if (_haveScene && sceneId != _prevSceneId)
                {
                    s.SceneChanged = true;

                    // Which screen is which, so the leaderboard one can be recognised.
                    //
                    // Climb mode has to swap the board layout when the player LEAVES the leaderboard
                    // screen, not when they enter a race: the in-race time to beat is copied out of
                    // the table when the race scene is built, so a swap at that moment is already
                    // too late. Leaving the leaderboard gives us the whole course-select, car-select
                    // and loading sequence to get the right layout in place.
                    //
                    // Only 5, the race, has ever been identified. One transition per screen change
                    // is nothing next to what this file already logs, and it costs nothing once the
                    // number is known and this line goes again.
                    Log?.Invoke($"[ID8MEM] scene {_prevSceneId} -> {sceneId}");
                }
                _prevSceneId = sceneId;
                _haveScene = true;
            }
        }

        /// <summary>Is any wheel on the ground? The game probes each wheel against the course and
        /// records whether that probe found anything, so all four failing is the car being in the
        /// air. Derived rather than read: the two jump flags in the map are speculative and only
        /// cover the scripted jumps, while this covers every time the car leaves the road.</summary>
        private bool ReadAirborne(uint hudBlock)
        {
            byte[] one = new byte[1];
            for (int wheel = 0; wheel < 4; wheel++)
            {
                if (!ReadInto((uint)(hudBlock + HudColliValid + wheel * HudColliStride), one, 1))
                    return false;   // cannot tell, so do not claim it
                if (one[0] != 0) return false;   // this wheel is on something
            }
            return true;
        }

        /// <summary>How coarse the surface under the car is, 0 to 1. The game classifies what each
        /// wheel is on and separately names the loop sound it is playing, so this reads its own
        /// judgement rather than inventing texture. The worst wheel wins, and two wheels on a strip
        /// read harsher than one.</summary>
        private double ReadSurfaceRumble(uint carState, uint carObject)
        {
            byte[] attr = new byte[4];
            if (!ReadInto(carState + CsSurfaceAttr, attr, 4)) return 0.0;

            double worst = 0.0, sum = 0.0;
            for (int i = 0; i < 4; i++)
            {
                double v = RoughnessOf(attr[i]);
                if (v > worst) worst = v;
                sum += v;
            }
            // Worst wheel sets the character, the average lifts it when more wheels agree.
            double r = worst * 0.7 + (sum / 4.0) * 0.3;

            // The loop the game itself decided to play. A rumble strip is unambiguous and worth
            // trusting over the per-wheel codes when the two disagree.
            int loop;
            if (I32(carObject + CoSurfaceLoop, out loop))
            {
                switch (loop & 0xff)
                {
                    case 2: if (r < 0.85) r = 0.85; break;   // rumble strip
                    case 4: if (r < 0.75) r = 0.75; break;   // gutter
                    case 3: if (r < 0.45) r = 0.45; break;   // grass
                    case 1: if (r < 0.25) r = 0.25; break;   // leaves
                }
            }
            return r > 1.0 ? 1.0 : r;
        }

        /// <summary>One wheel's surface code turned into coarseness. Codes are the game's, read off
        /// its own consumers; see the wheel_surface_attr entry in the map.</summary>
        private static double RoughnessOf(byte code)
        {
            switch (code)
            {
                case 0x14: case 0x15: return 1.00;   // rumble strip
                case 0x20:            return 0.80;   // gutter
                case 0x1c:            return 0.55;   // jump ramp
                case 0x30:            return 0.35;   // drain cover
                case 0x18:            return 0.30;   // low grip
                case 0x2b: case 0x2e: case 0x2f: return 0.20;   // water
            }
            int low = code & 0x0f;
            if (low == 6 || low == 7) return 0.65;   // loose, dirt or gravel
            if (low == 0x0b) return 0.30;            // road joint
            return 0.0;                              // plain road
        }

        /// <summary>An angle difference brought back into -pi..pi, so a car pointing just past
        /// north and travelling just before it reads as a couple of degrees rather than as 358.</summary>
        private static double WrapRadians(double a)
        {
            const double TwoPi = 2.0 * Math.PI;
            a = a % TwoPi;
            if (a > Math.PI) a -= TwoPi;
            else if (a < -Math.PI) a += TwoPi;
            return a;
        }

        private static double Finite(float v)
        {
            return (float.IsNaN(v) || float.IsInfinity(v)) ? 0.0 : v;
        }

        private static double Clamp01(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0.0;
            if (v <= 0f) return 0.0;
            return v >= 1f ? 1.0 : v;
        }

        private void Emit(string msg)
        {
            Action<string> log = Log;
            if (log != null)
            {
                try { log("[ID8MEM] " + msg); } catch { }
            }
        }

        public void Dispose() { Detach(); }
    }
}
