using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Pins the reply-matching rules the conditional arm depends on. The frames
    // here are the real ones from the 2026-05-16 G PRO capture (gpro_leds.pcap,
    // replies decoded 2026-08-11): feature 0x807A at index 0x09, our sw-id 0x0D.
    // Matching must use the FULL function byte, because a reply carries no page,
    // broadcasts carry sw-id 0, and the OLED channel shares this pipe on sw-id
    // 0x0A. A loose matcher latched the display's index as revFeat on 2026-08-08.
    public class WheelLedFnReplyTests
    {
        private const byte Feat = 0x09;   // 0x807A on the captured wheel
        private const byte Fn0 = 0x0D;    // GET_INFO  | sw-id 0x0D
        private const byte Fn2 = 0x2D;    // GET_STATE | sw-id 0x0D

        private static byte[] Frame(params byte[] head)
        {
            var f = new byte[64];
            head.CopyTo(f, 0);
            return f;
        }

        [Fact]
        public void Fn0ReplyMatches_AndCarriesTheStripLength()
        {
            // Captured: 12 ff 09 0d 01 0a 0a 01 = version 1, strip length 10.
            var resp = Frame(0x12, 0xFF, 0x09, 0x0D, 0x01, 0x0A, 0x0A, 0x01);
            Assert.Equal(WheelLedChannel.FnReply.Match,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn0));
            Assert.Equal(10, WheelLedChannel.StripLengthFromInfo(resp, resp.Length));
        }

        [Fact]
        public void G923ShapedFn0Reply_YieldsFiveSteps()
        {
            // mescon 2026-08-11: fn0's second parameter is 0x05 on a G923.
            var resp = Frame(0x12, 0xFF, 0x09, 0x0D, 0x01, 0x05, 0x05, 0x00);
            Assert.Equal(5, WheelLedChannel.StripLengthFromInfo(resp, resp.Length));
        }

        [Fact]
        public void StripLength_ComesFromParamOne_NotTheDuplicate()
        {
            // The real frames duplicate the length at [5] and [6], so they
            // cannot tell the two slots apart; pin [5] as the authoritative
            // byte with a synthetic frame where the copies differ.
            var resp = Frame(0x12, 0xFF, 0x09, 0x0D, 0x01, 0x0A, 0x07, 0x00);
            Assert.Equal(10, WheelLedChannel.StripLengthFromInfo(resp, resp.Length));
        }

        [Theory]
        [InlineData(0x00)]   // zero steps is not a strip
        [InlineData(0x15)]   // 21: beyond any plausible strip
        public void ImplausibleStripLength_IsRejected(byte len)
        {
            var resp = Frame(0x12, 0xFF, 0x09, 0x0D, 0x01, len, len, 0x00);
            Assert.Equal(-1, WheelLedChannel.StripLengthFromInfo(resp, resp.Length));
        }

        [Fact]
        public void TruncatedFrame_IsNeitherMatchedNorParsed()
        {
            var resp = Frame(0x12, 0xFF, 0x09, 0x0D, 0x01, 0x0A);
            Assert.Equal(WheelLedChannel.FnReply.Skip,
                WheelLedChannel.ClassifyFnReply(resp, 5, Feat, Fn0));
            Assert.Equal(-1, WheelLedChannel.StripLengthFromInfo(resp, 5));
        }

        [Theory]
        [InlineData(0x00)]   // pre-arm: displaying nothing
        [InlineData(0x02)]   // post-arm: effect 2
        public void Fn2StateReply_Matches(byte effect)
        {
            var resp = Frame(0x12, 0xFF, 0x09, 0x2D, effect);
            Assert.Equal(WheelLedChannel.FnReply.Match,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn2));
            Assert.Equal(effect, resp[4]);
        }

        [Fact]
        public void EffectChangeBroadcast_IsSkipped()
        {
            // Captured right after fn3: 12 ff 09 00 02 - sw-id 0, a broadcast,
            // not the reply to anyone's request.
            var resp = Frame(0x12, 0xFF, 0x09, 0x00, 0x02);
            Assert.Equal(WheelLedChannel.FnReply.Skip,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn2));
        }

        [Fact]
        public void ForeignSwId_IsSkipped()
        {
            // Same feature, same function nibble, another requester's sw-id
            // (0x0A = our own OLED channel). Accepting it re-opens the race.
            var resp = Frame(0x12, 0xFF, 0x09, 0x2A, 0x02);
            Assert.Equal(WheelLedChannel.FnReply.Skip,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn2));
        }

        [Fact]
        public void RootGetFeatureReply_IsSkipped()
        {
            // Captured: 12 ff 00 0d 10 00 00, the DISPLAY's root resolve. Its
            // full fn byte happens to equal our Fn0, so only the feature-index
            // check keeps it out.
            var resp = Frame(0x12, 0xFF, 0x00, 0x0D, 0x10, 0x00, 0x00);
            Assert.Equal(WheelLedChannel.FnReply.Skip,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn0));
        }

        [Fact]
        public void WrongDeviceIndex_IsSkipped()
        {
            var resp = Frame(0x12, 0x01, 0x09, 0x2D, 0x02);
            Assert.Equal(WheelLedChannel.FnReply.Skip,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn2));
        }

        [Fact]
        public void ErrorFrameForOurFeature_IsSurfaced()
        {
            // The field-validated error shape: 0xFF in the feature slot, the
            // request echoed at [4..5], the code at [6]. Error 5 echoing fn6
            // is the "no active effect, level refused" dark-lights case.
            var resp = Frame(0x12, 0xFF, 0xFF, 0x00, 0x09, 0x6D, 0x05);
            Assert.Equal(WheelLedChannel.FnReply.Error,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn2));
            Assert.Equal(0x6D, resp[5]);
            Assert.Equal(0x05, resp[6]);
        }

        [Fact]
        public void ErrorFrameForAnotherFeature_IsSkipped()
        {
            // An error echoing the display's index must not fail a rev-light
            // request.
            var resp = Frame(0x12, 0xFF, 0xFF, 0x00, 0x10, 0x3A, 0x05);
            Assert.Equal(WheelLedChannel.FnReply.Skip,
                WheelLedChannel.ClassifyFnReply(resp, resp.Length, Feat, Fn2));
        }

        // ---- Arm-time effect planning (pending picks) -----------------------

        [Theory]
        [InlineData( 0, 0, true,  0x02, 0)]  // displaying nothing, no pick: legacy start
        [InlineData(-1, 0, true,  0x02, 0)]  // unreadable, no pick: legacy start
        [InlineData( 9, 0, false, 0x00, 9)]  // displaying, no pick: leave the selection alone
        [InlineData( 2, 0, false, 0x00, 2)]  // ditto for a built-in
        [InlineData( 9, 2, true,  0x02, 9)]  // pending pick over a custom slot: select it
        [InlineData( 9, 9, false, 0x00, 9)]  // pick already active: nothing to write
        [InlineData( 0, 5, true,  0x05, 0)]  // dead display + pick: the pick doubles as the start
        [InlineData(-1, 5, true,  0x05, 0)]  // unreadable + pick: send the pick
        public void PlanArmEffect_Matrix(
            int current, int pending, bool send, byte param, int known)
        {
            var plan = WheelLedChannel.PlanArmEffect(current, pending);
            Assert.Equal(send, plan.SendFn3);
            if (send) Assert.Equal(param, plan.Fn3Param);
            Assert.Equal(known, plan.WheelOwnEffect);
        }
    }
}
