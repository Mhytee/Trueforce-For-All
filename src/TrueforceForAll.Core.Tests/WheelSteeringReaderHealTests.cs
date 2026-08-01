using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Self-heal gate for the physical steering reader: reopen only when a
    // session is wanted, dead, and the retry spacing has elapsed. The HID
    // plumbing itself needs hardware; the gate is the logic that decides
    // whether churn can ever happen, so it gets the tests.
    public class WheelSteeringReaderHealTests
    {
        private const long Spacing = 5_000;

        [Fact]
        public void HealthySession_NeverHeals()
        {
            // A live receiver must never be torn down, no matter how much
            // time has passed (a perfectly still wheel is NOT death).
            Assert.False(WheelSteeringReader.ShouldAttemptHeal(
                wantRunning: true, sessionAlive: true,
                nowTicks: 1_000_000, lastAttemptTicks: 0, spacingTicks: Spacing));
        }

        [Fact]
        public void Stopped_NeverHeals()
        {
            // Stop()/Dispose cleared the want flag: dead stays dead.
            Assert.False(WheelSteeringReader.ShouldAttemptHeal(
                wantRunning: false, sessionAlive: false,
                nowTicks: 1_000_000, lastAttemptTicks: 0, spacingTicks: Spacing));
        }

        [Fact]
        public void DeadSession_HealsOnlyAfterSpacing()
        {
            // Inside the spacing window: no attempt (bounds handle churn and
            // enumeration cost while e.g. G HUB holds the device).
            Assert.False(WheelSteeringReader.ShouldAttemptHeal(
                true, false, nowTicks: Spacing - 1, lastAttemptTicks: 0, spacingTicks: Spacing));
            // At/after the spacing boundary: one attempt is due.
            Assert.True(WheelSteeringReader.ShouldAttemptHeal(
                true, false, nowTicks: Spacing, lastAttemptTicks: 0, spacingTicks: Spacing));
            Assert.True(WheelSteeringReader.ShouldAttemptHeal(
                true, false, nowTicks: Spacing * 3, lastAttemptTicks: Spacing, spacingTicks: Spacing));
        }

        [Fact]
        public void FailedAttempt_BacksOffFromTheAttempt_NotFromDeath()
        {
            // lastAttemptTicks is stamped when an attempt STARTS, so a failed
            // reopen waits a full spacing before the next one instead of
            // retrying every poll tick.
            long attemptAt = 10 * Spacing;
            Assert.False(WheelSteeringReader.ShouldAttemptHeal(
                true, false, nowTicks: attemptAt + Spacing / 2, lastAttemptTicks: attemptAt, spacingTicks: Spacing));
            Assert.True(WheelSteeringReader.ShouldAttemptHeal(
                true, false, nowTicks: attemptAt + Spacing, lastAttemptTicks: attemptAt, spacingTicks: Spacing));
        }
    }
}
