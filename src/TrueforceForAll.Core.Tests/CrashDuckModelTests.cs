// Crash duck (eleventh wheel test: wall hit buzzed the wheel). The
// contract: driving never triggers it, an impact ducks instantly, and
// force returns smoothly — never a snap back to full.

using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class CrashDuckModelTests
    {
        [Fact]
        public void HardDriving_NeverDucks()
        {
            var m = new CrashDuckModel();
            // Combined trail-braking at the physical ceiling of road racing.
            for (int i = 0; i < 600; i++)
                Assert.Equal(1.0, m.Tick(2.5, 16), 9);
        }

        [Fact]
        public void Impact_DucksInstantly()
        {
            var m = new CrashDuckModel();
            m.Tick(0.4, 16);
            double factor = m.Tick(8.0, 16);   // the wall
            Assert.Equal(1.0 - m.DuckDepth, factor, 6);
        }

        [Fact]
        public void Release_IsSmoothAndMonotonic_NoSnapBack()
        {
            var m = new CrashDuckModel();
            m.Tick(8.0, 16);
            double prev = m.Tick(0.2, 16);
            for (int i = 0; i < 200; i++)
            {
                double f = m.Tick(0.2, 16);
                Assert.True(f >= prev - 1e-12, "release must be monotonic");
                // No step larger than a few percent per 16 ms tick.
                Assert.True(f - prev < 0.05, $"snap-back step of {f - prev}");
                prev = f;
            }
            Assert.Equal(1.0, prev, 2);   // fully recovered after ~3 s
        }

        [Fact]
        public void SustainedScrape_StaysDucked()
        {
            // Grinding along the wall: repeated impact readings keep the
            // duck pinned instead of oscillating force back in and out.
            var m = new CrashDuckModel();
            for (int i = 0; i < 60; i++)
            {
                double f = m.Tick(i % 3 == 0 ? 5.0 : 1.0, 16);   // spikes every 3rd frame
                if (i > 0) Assert.True(f < 0.35, $"scrape must stay ducked, got {f}");
            }
        }

        [Fact]
        public void Reset_ClearsTheDuck()
        {
            var m = new CrashDuckModel();
            m.Tick(8.0, 16);
            m.Reset();
            Assert.Equal(1.0, m.Tick(0.0, 16), 9);
        }
    }
}
