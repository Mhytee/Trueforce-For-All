// The blind-capture signal that drives the auto re-enumeration self-heal.
// A capture is "interrupt-blind" when we streamed clearly to the wheel yet the
// capture delivered none of those interrupt writes: USBPcap has no filter on
// the wheel's device. This must NOT trip when the game simply sent no FFB
// (that only suppresses ep0 game writes, never our ep3 interrupt stream), nor
// when the probe barely ran.

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class BlindCaptureClassifierTests
    {
        [Fact]
        public void Blind_when_we_streamed_but_capture_saw_nothing()
        {
            // ~1.5 s of our 1 kHz pump, zero of it captured: the reporter's case.
            Assert.True(BlindCaptureClassifier.IsInterruptBlind(sentDelta: 1500, interruptSeenDelta: 0));
        }

        [Fact]
        public void Healthy_when_capture_saw_our_stream()
        {
            // The owner-rig baseline: our ep3 writes show up on the capture.
            Assert.False(BlindCaptureClassifier.IsInterruptBlind(sentDelta: 1500, interruptSeenDelta: 1490));
        }

        [Fact]
        public void Healthy_when_capture_saw_even_a_few()
        {
            // A single captured interrupt packet proves the filter is attached;
            // never re-enumerate in that case.
            Assert.False(BlindCaptureClassifier.IsInterruptBlind(sentDelta: 1500, interruptSeenDelta: 1));
        }

        [Fact]
        public void Inconclusive_when_we_barely_streamed()
        {
            // If the pump barely ran (paused, torn down, race at the window
            // edge) an all-zero captured count is not evidence of blindness.
            Assert.False(BlindCaptureClassifier.IsInterruptBlind(sentDelta: 10, interruptSeenDelta: 0));
        }

        [Fact]
        public void Boundary_at_min_sends()
        {
            Assert.True(BlindCaptureClassifier.IsInterruptBlind(BlindCaptureClassifier.DefaultMinSends, 0));
            Assert.False(BlindCaptureClassifier.IsInterruptBlind(BlindCaptureClassifier.DefaultMinSends - 1, 0));
        }

        [Fact]
        public void Custom_min_sends_threshold_is_honored()
        {
            Assert.True(BlindCaptureClassifier.IsInterruptBlind(sentDelta: 200, interruptSeenDelta: 0, minSends: 150));
            Assert.False(BlindCaptureClassifier.IsInterruptBlind(sentDelta: 200, interruptSeenDelta: 0, minSends: 300));
        }

        [Fact]
        public void Negative_seen_delta_is_not_positive_evidence_of_health()
        {
            // A counter reset/wrap should read as "saw nothing", i.e. blind if
            // we streamed enough. (<= 0 guards this.)
            Assert.True(BlindCaptureClassifier.IsInterruptBlind(sentDelta: 1500, interruptSeenDelta: -5));
        }
    }
}
