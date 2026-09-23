using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Verifies our reader against the block layout the publishing FFBArcadePlugin
    // build writes.
    //
    // This seam cannot be checked any other way. The publisher is a C++ DLL inside
    // the game's process; we are a C# reader inside SimHub. The only thing joining
    // them is a byte layout written down in two places, and if those drift the
    // symptom is not an exception, it is force feedback computed from the wrong
    // fields. So these build the block exactly as the C++ struct declares it.
    //
    // Layout, from Common Files/SharedMemoryOutput.h (pack(4), all members 4 bytes):
    //   header 64 bytes: 0 magic, 4 version, 8 seq, 12 frame, 16 gameId,
    //                    20 feedbackLengthMs, 24 flags, 28 slotCount, 32..63 reserved
    //   then 16 slots of 84 bytes each, from offset 64:
    //     0 kind, 4 running, 8 lengthMs, 12 direction, 16 level, 20 level2,
    //     24 periodMs, 28 offset, 32 phase, 36 attackLevel, 40 attackMs,
    //     44 fadeLevel, 48 fadeMs, 52 rightSat, 56 leftSat, 60 rightCoeff,
    //     64 leftCoeff, 68 deadband, 72 center, 76 updatedFrame, 80 reserved
    public class FfbArcadePluginSourceTests : IDisposable
    {
        private const uint Magic = 0x4F424646;   // 'FFBO'
        private const int Header = 64, SlotBytes = 84;

        private const uint KConstant = 1, KSine = 2, KSpring = 6, KDamper = 7, KFriction = 9;

        private const int SKind = 0, SRunning = 4, SLength = 8, SDirection = 12;
        private const int SLevel = 16, SPeriod = 24, SOffset = 28, SPhase = 32;
        private const int SRightSat = 52, SLeftSat = 56, SRightCoeff = 60, SLeftCoeff = 64;
        private const int SDeadband = 68, SCenter = 72, SUpdatedFrame = 76;

        private readonly string _mapName = "TF4ALL_Test_" + Guid.NewGuid().ToString("N");
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private FfbArcadePluginSource _src;
        private uint _frame;
        private Action _lastBody;

        public FfbArcadePluginSourceTests()
        {
            _mmf = MemoryMappedFile.CreateNew(_mapName, FfbArcadePluginSource.BlockBytes);
            _view = _mmf.CreateViewAccessor(0, FfbArcadePluginSource.BlockBytes);
            _view.Write(0, Magic);
            _view.Write(4, 2u);       // version 2
            _view.Write(28, 16u);     // slotCount
        }

        public void Dispose()
        {
            try { if (_src != null) _src.Stop(); } catch { }
            try { if (_view != null) _view.Dispose(); } catch { }
            try { if (_mmf != null) _mmf.Dispose(); } catch { }
        }

        private static int Slot(int i) => Header + i * SlotBytes;

        private void Publish(Action body)
        {
            uint seq = _view.ReadUInt32(8);
            _view.Write(8, seq + 1);                       // odd: writing
            for (int b = Header; b < FfbArcadePluginSource.BlockBytes; b += 4) _view.Write(b, 0u);
            _view.Write(12, ++_frame);
            if (body != null) body();
            _view.Write(8, seq + 2);                       // even: done
        }

        private void SetFrame(Action body) { _lastBody = body; Publish(body); }

        // Default matches the shipped default, so a test that says nothing about
        // the damper is testing what a user actually gets.
        private FfbArcadePluginSource Start(bool acceptDamper = true)
        {
            _src = new FfbArcadePluginSource { MapName = _mapName, AcceptPublishedDamper = acceptDamper };
            _src.Start();
            return _src;
        }

        private ArcadeFfbState WaitFor(Func<ArcadeFfbState, bool> until)
        {
            for (int i = 0; i < 200; i++)
            {
                Publish(_lastBody);
                var st = _src.TryGetFreshState(500);
                if (st != null && until(st)) return st;
                Thread.Sleep(5);
            }
            return null;
        }

        // A running constant in slot 0, direction as SDL passes it.
        private void WriteConstant(int dir, short level, uint lengthMs = 0xFFFFFFFF)
        {
            int b = Slot(0);
            _view.Write(b + SKind, KConstant);
            _view.Write(b + SRunning, 1u);
            _view.Write(b + SLength, lengthMs);
            _view.Write(b + SDirection, dir);
            _view.Write(b + SLevel, (int)level);
            _view.Write(b + SUpdatedFrame, _frame);
        }

        [Fact]
        public void Present_FindsABlockWithTheRightMagic()
        {
            Assert.True(FfbArcadePluginSource.Present(_mapName));
            Assert.False(FfbArcadePluginSource.Present(_mapName + "_nope"));
        }

        [Fact]
        public void ReaderEventName_MatchesThePublishersConvention()
        {
            Assert.Equal("FFBArcadePlugin_Output_Reader", FfbArcadePluginSource.ReaderEventName(null));
            Assert.Equal("thing_Reader", FfbArcadePluginSource.ReaderEventName("thing"));
        }

        [Fact]
        public void BlockSize_MatchesTheDeclaredLayout()
        {
            // 64-byte header plus 16 slots of 84. If the C++ struct grows and this
            // does not, every slot after the first is read from the wrong offset.
            Assert.Equal(64 + 16 * 84, FfbArcadePluginSource.BlockBytes);
        }

        // SDL names a direction by where the force COMES FROM, so dir[0] = -1 is a
        // force from the left, which drives the wheel RIGHT. Our convention is
        // positive = right. Getting this backwards makes the wheel fight the driver.
        [Theory]
        [InlineData(-1, 16384,  0.5)]    // from left  -> wheel right -> positive
        [InlineData(1,  16384, -0.5)]    // from right -> wheel left  -> negative
        public void ConstantForce_DirectionResolvesToSignedStrength(int dir, short level, double expected)
        {
            Start();
            SetFrame(() => WriteConstant(dir, level));
            var st = WaitFor(s => s.Constant.Present);
            Assert.NotNull(st);
            Assert.Equal(expected, st.Constant.Strength, 3);
        }

        // A from-the-left SDL direction (dir -1) is +1.0 in the IR and reaches the
        // wheel POSITIVE. This is the shipped convention and it is rig-backed:
        // Initial D's race force is the constant, and it reads correct.
        //
        // It was briefly flipped here on a misreading and flipped straight back.
        // Worth recording so nobody repeats it: "menu forces feel reversed" is the
        // SPRING, which is what the game uses for the detents as you turn through
        // options, NOT the constant. Flipping this broke the race force that was
        // already right and did nothing for the menu. If a future rig test does
        // contradict it, change it HERE and in the two ToLsb methods together;
        // they are deliberately identical.
        [Fact]
        public void ScalarLatchTracksTheConstant()
        {
            Start();
            SetFrame(() => WriteConstant(-1, 32767));
            short? got = null;
            for (int i = 0; i < 200 && got == null; i++)
            {
                Publish(_lastBody);
                got = _src.TryGetFreshFfbTarget(500);
                if (got == null) Thread.Sleep(5);
            }
            Assert.NotNull(got);
            Assert.Equal(32767, got.Value);
        }

        // The per-cabinet override still flips it, because one cabinet's
        // protocol could disagree with the rest and the escape hatch is the point.
        [Fact]
        public void InvertConstantDirection_FlipsItBack()
        {
            Start();
            _src.InvertConstantDirection = true;   // Start() constructs _src
            SetFrame(() => WriteConstant(-1, 32767));
            short? got = null;
            for (int i = 0; i < 200 && got == null; i++)
            {
                Publish(_lastBody);
                got = _src.TryGetFreshFfbTarget(500);
                if (got == null) Thread.Sleep(5);
            }
            Assert.NotNull(got);
            Assert.Equal(-32767, got.Value);
        }

        // A slot that was downloaded but never started must not produce force.
        [Fact]
        public void EffectNotRunning_IsIgnored()
        {
            Start();
            SetFrame(() =>
            {
                int b = Slot(0);
                _view.Write(b + SKind, KConstant);
                _view.Write(b + SRunning, 0u);          // never started
                _view.Write(b + SDirection, -1);
                _view.Write(b + SLevel, 32767);
                _view.Write(b + SUpdatedFrame, _frame);
            });
            Thread.Sleep(120);
            for (int i = 0; i < 20; i++) { Publish(_lastBody); Thread.Sleep(5); }
            var st = _src.TryGetFreshState(500);
            Assert.True(st == null || !st.Constant.Present);
        }

        // THE POINT OF THE WHOLE REDESIGN: the user's tuning arrives intact.
        // Saturation, deadband and centre are carried, not flattened to one number.
        [Fact]
        public void ConditionParameters_ArrivePreserved_NotFlattened()
        {
            Start();
            SetFrame(() =>
            {
                int b = Slot(1);
                _view.Write(b + SKind, KSpring);
                _view.Write(b + SRunning, 1u);
                _view.Write(b + SLength, 0xFFFFFFFF);
                _view.Write(b + SLeftCoeff, 16384);      // 0.5
                _view.Write(b + SRightCoeff, 8192);      // 0.25, deliberately different
                _view.Write(b + SLeftSat, 32767);        // 0.5 of 65535
                _view.Write(b + SRightSat, 65535);       // full
                _view.Write(b + SDeadband, 6553);        // 0.1
                _view.Write(b + SCenter, 3276);          // 0.1
                _view.Write(b + SUpdatedFrame, _frame);
            });

            var st = WaitFor(s => s.Spring.Present);
            Assert.NotNull(st);
            Assert.Equal(0.5,  st.Spring.CoeffLeft,  3);
            Assert.Equal(0.25, st.Spring.CoeffRight, 3);
            Assert.Equal(0.5,  st.Spring.SatLeft,    3);
            Assert.Equal(1.0,  st.Spring.SatRight,   3);
            Assert.Equal(0.1,  st.Spring.Deadband,   3);
            Assert.Equal(0.1,  st.Spring.Center,     3);
        }

        [Fact]
        public void Periodic_CarriesMagnitudePeriodOffsetAndPhase()
        {
            Start();
            SetFrame(() =>
            {
                int b = Slot(2);
                _view.Write(b + SKind, KSine);
                _view.Write(b + SRunning, 1u);
                _view.Write(b + SLength, 0xFFFFFFFF);
                _view.Write(b + SLevel, 16384);      // 0.5
                _view.Write(b + SPeriod, 40u);
                _view.Write(b + SOffset, 3276);      // 0.1
                _view.Write(b + SPhase, 9000u);      // SDL: hundredths of a degree -> 0.25
                _view.Write(b + SUpdatedFrame, _frame);
            });

            var st = WaitFor(s => s.Periodic.Present);
            Assert.NotNull(st);
            Assert.Equal(0.5, st.Periodic.Strength, 3);
            Assert.Equal(40, st.Periodic.PeriodMs);
            Assert.Equal(0.1, st.Periodic.Offset, 3);
            Assert.Equal(0.25, st.Periodic.Phase, 3);
        }

        // The damper the publisher reports is usually that plugin's own ini
        // preference, and we have our own, so it must not arrive unasked.
        [Fact]
        public void PublishedDamper_IsIgnoredUnlessAccepted()
        {
            Start(acceptDamper: false);   // explicit opt-out
            SetFrame(() =>
            {
                int b = Slot(3);
                _view.Write(b + SKind, KDamper);
                _view.Write(b + SRunning, 1u);
                _view.Write(b + SLength, 0xFFFFFFFF);
                _view.Write(b + SLeftCoeff, 16384);
                _view.Write(b + SRightCoeff, 16384);
                _view.Write(b + SUpdatedFrame, _frame);

                int c = Slot(4);                       // something to wait on
                _view.Write(c + SKind, KFriction);
                _view.Write(c + SRunning, 1u);
                _view.Write(c + SLength, 0xFFFFFFFF);
                _view.Write(c + SLeftCoeff, 8192);
                _view.Write(c + SRightCoeff, 8192);
                _view.Write(c + SUpdatedFrame, _frame);
            });

            var st = WaitFor(s => s.Friction.Present);
            Assert.NotNull(st);
            Assert.False(st.Damper.Present);
        }

        // FeedbackLength is how the reference decays a force the game stopped
        // refreshing, and it is per game: 80 ms on WMMT3, 5000 ms on Initial D.
        // Reproducing it is what keeps the feel the user tuned.
        [Fact]
        public void EffectExpires_WhenItsLengthPassesWithoutAnUpdate()
        {
            Start();
            // A 50 ms effect, stamped at frame 1 and never refreshed. The reader
            // measures elapsed frames at ~60 Hz, so a handful of frames buries it.
            SetFrame(() => WriteConstant(-1, 32767, lengthMs: 50));
            var live = WaitFor(s => s.Constant.Present);
            Assert.NotNull(live);

            // Advance frames without touching updatedFrame.
            for (int i = 0; i < 40; i++)
            {
                uint seq = _view.ReadUInt32(8);
                _view.Write(8, seq + 1);
                _view.Write(12, ++_frame);
                _view.Write(8, seq + 2);
                Thread.Sleep(3);
            }
            var st = _src.TryGetFreshState(500);
            Assert.True(st == null || !st.Constant.Present,
                "an effect past its length must stop, or a stale force plays forever");
        }

        private const int HRumbleLow = 32, HRumbleHigh = 36, HRumbleFrame = 44;

        // Rumble is only independent information on cabinets with no force at all.
        // Where a game emits force, those same numbers ARE the force re-encoded for
        // a gamepad, so surfacing both would deliver one signal twice.
        [Fact]
        public void Rumble_IsIgnored_WhenTheGameAlsoEmitsForce()
        {
            Start();
            SetFrame(() =>
            {
                WriteConstant(-1, 16384);
                _view.Write(HRumbleLow, 40000u);
                _view.Write(HRumbleHigh, 40000u);
                _view.Write(HRumbleFrame, _frame);
            });

            var st = WaitFor(s => s.Constant.Present);
            Assert.NotNull(st);
            Assert.False(st.Rumble.Present);
        }

        // And on a rumble-only cabinet it is the whole signal: ten of the supported
        // games never call a force trigger at all.
        [Fact]
        public void Rumble_IsCarried_WhenThereIsNoForceAtAll()
        {
            Start();
            SetFrame(() =>
            {
                _view.Write(HRumbleLow, 32768u);
                _view.Write(HRumbleHigh, 16384u);
                _view.Write(HRumbleFrame, _frame);
            });

            var st = WaitFor(s => s.Rumble.Present);
            Assert.NotNull(st);
            Assert.Equal(0.5, st.Rumble.RumbleLow, 2);
            Assert.Equal(0.25, st.Rumble.RumbleHigh, 2);
        }

        // A damper only reaches us when somebody enabled one, so the shipped
        // default honours it rather than dropping a setting they turned on.
        [Fact]
        public void PublishedDamper_IsRenderedByDefault()
        {
            Start();
            SetFrame(() =>
            {
                int b = Slot(3);
                _view.Write(b + SKind, KDamper);
                _view.Write(b + SRunning, 1u);
                _view.Write(b + SLength, 0xFFFFFFFF);
                _view.Write(b + SLeftCoeff, 16384);
                _view.Write(b + SRightCoeff, 16384);
                _view.Write(b + SUpdatedFrame, _frame);
            });

            var st = WaitFor(s => s.Damper.Present);
            Assert.NotNull(st);
            Assert.Equal(0.5, st.Damper.CoeffLeft, 3);
        }

        [Fact]
        public void OddSequence_IsNotReadAsData()
        {
            Start();
            SetFrame(() => WriteConstant(-1, 16384));
            Assert.NotNull(WaitFor(s => s.Constant.Present));

            long before = _src.TornReadCount;
            _view.Write(8, _view.ReadUInt32(8) + 1);   // leave it odd
            Thread.Sleep(60);
            Assert.True(_src.TornReadCount > before);
        }

        [Fact]
        public void UnknownLayoutVersion_IsRefused()
        {
            _view.Write(4, 99u);
            Start();
            SetFrame(() => WriteConstant(-1, 32767));
            Thread.Sleep(150);
            Assert.Null(_src.TryGetFreshState(500));
            Assert.False(_src.MapOpen);
        }
    }
}
