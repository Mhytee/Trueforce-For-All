using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The iRacing soft lock's blend inputs (CSP's shape): where the band opens,
    // where it saturates, which way the wall points, and what speed does to it.
    public class IRacingSoftLockTests
    {
        // A 540 degree lock-to-lock car: 270 per side.
        private const float Lock = 270f;
        private const float Pad = IRacingSoftLock.PaddingDeg / Lock;

        [Fact]
        public void NothingWellInsideTheLimit()
        {
            IRacingSoftLock.Compute(0.5f, 3f, Lock, 1f, out var amount, out var target);
            Assert.Equal(0f, amount);
            Assert.Equal(0f, target);
        }

        [Fact]
        public void BandOpensJustBeforeTheLimit()
        {
            // Closed at 1 - 0.2 pad, open a hair past it.
            IRacingSoftLock.Compute(1f - 0.2f * Pad - 0.001f, 0f, Lock, 1f, out var closed, out _);
            IRacingSoftLock.Compute(1f - 0.2f * Pad + 0.001f, 0f, Lock, 1f, out var open, out _);
            Assert.Equal(0f, closed);
            Assert.True(open > 0f && open < 0.1f);
        }

        [Fact]
        public void SaturatesPastTheBand()
        {
            IRacingSoftLock.Compute(1f + 1.8f * Pad + 0.001f, 0f, Lock, 1f, out var amount, out _);
            Assert.Equal(1f, amount);
        }

        [Fact]
        public void TargetFollowsTheSteerSign()
        {
            IRacingSoftLock.Compute(1.2f, 0f, Lock, 1f, out _, out var left);
            IRacingSoftLock.Compute(-1.2f, 0f, Lock, 1f, out _, out var right);
            Assert.Equal(1f, left);
            Assert.Equal(-1f, right);
        }

        [Fact]
        public void StrengthSetsTheWallOnceSpeedHasFaded()
        {
            // Past two band widths the speed term is gone: even a fast push
            // into the wall leaves the target at strength.
            IRacingSoftLock.Compute(1.2f, 5f, Lock, 0.5f, out _, out var target);
            Assert.Equal(0.5f, target, 3);
        }

        [Fact]
        public void LeavingTheWallEasesIt()
        {
            // Just past the limit and coming back out: the target drops below
            // strength so the wall lets go rather than shoving the wheel back.
            IRacingSoftLock.Compute(1.02f, -1f, Lock, 1f, out var amount, out var target);
            Assert.True(amount > 0f);
            Assert.True(target < 1f && target > 0f);
        }

        [Fact]
        public void PushingInHoldsTheWallAtReducedStrength()
        {
            // Speed in the wall's direction adds to a partial strength.
            IRacingSoftLock.Compute(1.02f, 1f, Lock, 0.5f, out _, out var target);
            Assert.True(target > 0.5f);
        }

        [Fact]
        public void StrengthClampsToFullScale()
        {
            IRacingSoftLock.Compute(1.2f, 0f, Lock, 3f, out _, out var target);
            Assert.Equal(1f, target);
        }

        [Fact]
        public void NoLockWithoutARange()
        {
            IRacingSoftLock.Compute(1.5f, 0f, 0f, 1f, out var amount, out var target);
            Assert.Equal(0f, amount);
            Assert.Equal(0f, target);
        }

        [Fact]
        public void InvLerpSatRunsBothWays()
        {
            Assert.Equal(0.5f, IRacingSoftLock.InvLerpSat(5f, 0f, 10f), 5);
            Assert.Equal(0.5f, IRacingSoftLock.InvLerpSat(5f, 10f, 0f), 5);
            Assert.Equal(1f, IRacingSoftLock.InvLerpSat(20f, 0f, 10f));
            Assert.Equal(0f, IRacingSoftLock.InvLerpSat(-1f, 0f, 10f));
        }
    }
}
