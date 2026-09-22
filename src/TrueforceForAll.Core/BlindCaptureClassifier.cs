// Detects the "interrupt-blind" USBPcap capture state.
//
// Background (confirmed 2026-08-30, RS50-in-G-PRO-compat report + owner rig +
// USBPcap driver source USBPcapPnP.c):
//
// USBPcap attaches a per-device capture filter only when the hub reports a
// child device object it has not seen before (its BusRelations handler skips
// PDOs already in `previousChildren`). At boot the wheel's device stack can be
// built before USBPcap's hub filter processes that first bus-relations query,
// so no filter is ever attached to the wheel. The capture then delivers the
// wheel's CONTROL transfers (ep0 chatter the hub emits) but none of its
// INTERRUPT traffic, where both the game's FFB and our own 1 kHz ep3 stream
// live. The wheel is limp in Trueforce mode because the tap mirrors nothing.
//
// The existing liveness watchdog cannot catch this: it watches PacketsParsed,
// which keeps advancing on that ep0 control chatter, so the capture looks
// "alive". The unambiguous signal is our OWN ep3 stream: a healthy capture
// always sees the interrupt-OUT writes we make to the wheel (observed climbing
// to tens of thousands within seconds), while a blind capture sees exactly
// zero of them. This is independent of any game, so it cannot be confused with
// a user who simply turned force feedback off in-game (that suppresses the
// game's ep0 writes, never our ep3 interrupt writes).
//
// The fix is a genuine re-enumeration (physical replug or a hub port cycle),
// which gives the wheel a fresh PDO that USBPcap does attach to. A PnP restart
// (pnputil / Device-Manager disable-enable) is NOT sufficient and is refused
// outright while SimHub holds the wheel's HID handle.

namespace TrueforceForAll.Core
{
    public static class BlindCaptureClassifier
    {
        // Default: over the probe window we must have clearly streamed to the
        // wheel (this many interrupt-OUT writes) before an all-zero captured
        // count is meaningful. Our pump runs at ~1 kHz, so a ~1.5 s probe
        // produces well over a thousand; 300 is a safe floor that still rules
        // out a probe that barely ran.
        public const long DefaultMinSends = 300;

        /// <summary>
        /// True when we streamed at least <paramref name="minSends"/> interrupt
        /// writes to the wheel over the probe window yet the capture saw none of
        /// them, i.e. USBPcap has no filter on the wheel's device (blind). Both
        /// deltas are measured across the same window: <paramref name="sentDelta"/>
        /// is our successful ep3 writes (TrueforceDevice.PacketsSent), and
        /// <paramref name="interruptSeenDelta"/> is the interrupt-OUT packets the
        /// capture actually delivered for the wheel (UsbPcapFfbTap.InterruptOutSeen).
        /// </summary>
        public static bool IsInterruptBlind(long sentDelta, long interruptSeenDelta, long minSends)
            => sentDelta >= minSends && interruptSeenDelta <= 0;

        /// <inheritdoc cref="IsInterruptBlind(long,long,long)"/>
        public static bool IsInterruptBlind(long sentDelta, long interruptSeenDelta)
            => IsInterruptBlind(sentDelta, interruptSeenDelta, DefaultMinSends);
    }
}
