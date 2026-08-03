// Regression tests for the enhanced-source overlay in FrameEnricher.
//
// The overlay exists for AC, whose shared-memory source deliberately leaves
// MaxRpm at 0 because SimHub already derives it correctly. It must NOT throw
// away a value an enhanced source did read for itself: Forza's sled carries
// EngineMaxRpm, and SimHub's own copy is zero whenever we own the Forza Data
// Out port with forwarding off. Clobbering there collapsed the variant
// signature to cyl-only, which blocked variant creation and made every
// redline / engine-type save fail with "couldn't identify this car's engine
// variant yet".

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class FrameEnricherTests
    {
        // Forza with forwarding off: our source read a real MaxRpm, SimHub saw
        // nothing. The frame must keep OUR value.
        [Fact]
        public void EnhancedSourceMaxRpm_SurvivesEmptyOverlay()
        {
            var frame = new TelemetryFrame { MaxRpm = 8000, Rpms = 4200 };
            var overlay = new SimHubOverlay { MaxRpm = 0 };

            FrameEnricher.Enrich(ref frame, sourceIsEnhanced: true, overlay,
                                 suppressRedlineOverlay: true);

            Assert.Equal(8000, frame.MaxRpm);
        }

        // Forwarding on (or any game where SimHub is also fed): both sides have
        // a value. The source is closer to the physics, so it still wins; the
        // point is only that neither path can zero it.
        [Fact]
        public void EnhancedSourceMaxRpm_WinsOverPopulatedOverlay()
        {
            var frame = new TelemetryFrame { MaxRpm = 8000 };
            var overlay = new SimHubOverlay { MaxRpm = 7500 };

            FrameEnricher.Enrich(ref frame, sourceIsEnhanced: true, overlay,
                                 suppressRedlineOverlay: true);

            Assert.Equal(8000, frame.MaxRpm);
        }

        // AC's contract: the source leaves MaxRpm at 0 on purpose and expects
        // the overlay to fill it. Must keep working.
        [Fact]
        public void EnhancedSourceWithoutMaxRpm_TakesOverlay()
        {
            var frame = new TelemetryFrame { MaxRpm = 0 };
            var overlay = new SimHubOverlay { MaxRpm = 7600 };

            FrameEnricher.Enrich(ref frame, sourceIsEnhanced: true, overlay,
                                 suppressRedlineOverlay: false);

            Assert.Equal(7600, frame.MaxRpm);
        }

        // Forza's all-zero keepalive packets carry MaxRpm 0. With forwarding
        // off the overlay is zero too, so the frame stays zero and the
        // plugin's ">100" guard leaves the last observed value alone.
        [Fact]
        public void Keepalive_WithEmptyOverlay_StaysZero()
        {
            var frame = new TelemetryFrame { MaxRpm = 0 };
            var overlay = new SimHubOverlay { MaxRpm = 0 };

            FrameEnricher.Enrich(ref frame, sourceIsEnhanced: true, overlay,
                                 suppressRedlineOverlay: true);

            Assert.Equal(0, frame.MaxRpm);
        }

        // SimHub-fallback frames are already complete; the overlay must not run
        // at all, so a stale cached MaxRpm can't overwrite the real one.
        [Fact]
        public void FallbackSource_IgnoresOverlayEntirely()
        {
            var frame = new TelemetryFrame { MaxRpm = 6500 };
            var overlay = new SimHubOverlay { MaxRpm = 9000, AbsActive = 1 };

            FrameEnricher.Enrich(ref frame, sourceIsEnhanced: false, overlay,
                                 suppressRedlineOverlay: false);

            Assert.Equal(6500, frame.MaxRpm);
            Assert.Equal(0, frame.AbsActive);
        }
    }
}
