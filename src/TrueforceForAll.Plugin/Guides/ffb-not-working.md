Start with **Run self-test** on the Settings tab. It sends a test buzz, reports what is reaching the wheel, and ends by naming the single most blocking issue. What you feel during that buzz tells you which of these you are in.

## You feel our effects, but the wheel is limp in the game

That points at [USBPcap](guide:usbpcap). It reads the game's force feedback off the USB bus and hands it to the plugin, which inserts it into the Trueforce stream. When it is not capturing, the game's force never reaches the wheel and only our effects do.

1. Run SimHub as administrator, using SimHub's own **Run as administrator** setting rather than right-clicking the exe, then restart SimHub.
2. Restart the PC if USBPcap was only just installed. Its capture driver attaches at boot.
3. Quit G HUB from the system tray. It can intercept the wheel's force feedback.
4. Check the game's own force feedback is on, and that this wheel is the one selected in it.
5. Replug the wheel, or move it to a USB 2.0 port on the back of the motherboard rather than a hub or a front-panel port.

If the Settings tab says USBPcap is missing entirely, the **Reinstall** button beside it runs the bundled installer.

## The wheel is limp and [Telemetry Based FFB](guide:telemetry-ffb) is on for this game

Then the force is ours to build, and telemetry is what we build it from. If that telemetry is not reaching the plugin there is nothing to send, and the wheel goes light along with the effects.

The Telemetry FFB tab says so when it happens and offers a **Set up** button. Either point the game's telemetry at the plugin, or turn Telemetry Based FFB off for that game and let the game's own force feedback through.

## You feel nothing at all

Then the plugin is not driving the wheel.

1. Check the mode at the top of the panel is **Normal**, not Off or Lightsync only.
2. Quit G HUB completely from the system tray. It claims the wheel while it is open.
3. Unplug the wheel and plug it back in.
4. On a PlayStation G923, open G HUB once to put the wheel back into PC mode, then close it again. That wheel can drop out of PC mode after a restart or a replug.
5. Restart SimHub, then check Diagnostics on the Settings tab says the wheel is detected.

If the wheel responds but everything simply feels faint, that is a different problem: see [Effects feel weak, and the dial on the wheel does nothing](guide:weak-effects).
