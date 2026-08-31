Start with **Run self-test** on the Settings tab. It sends a test buzz, reports what is reaching the wheel, and ends by naming the single most blocking issue. What you feel during that buzz tells you which of these you are in.

## You feel our effects, but the wheel is limp in the game

That points at [USBPcap](guide:usbpcap). It captures the game's force feedback off the USB bus. When it is not capturing, the game's force never reaches the wheel and only our effects do.

1. Run SimHub as administrator, using SimHub's own **Run as administrator** setting rather than right-clicking the exe, then restart SimHub.
2. Restart the PC if USBPcap was just installed. Its capture driver attaches at boot.
3. Quit G HUB from the system tray. It can intercept the wheel's force feedback.
4. Check the game's own force feedback is on, and that the game has this wheel selected.
5. Replug the wheel, or move it to a USB 2.0 port on the back of the motherboard rather than a hub or a front-panel port.

If the Settings tab says USBPcap is missing entirely, the **Reinstall** button beside it runs the bundled installer.

## The wheel is limp and [Telemetry Based FFB](guide:telemetry-ffb) is on for this game

Then the force is ours to build, and telemetry is what we build it from. If that telemetry is not reaching the plugin there is nothing to send, and the wheel goes light along with the effects.

When telemetry stops reaching the plugin, the FFB tab says so and offers a **Set up** button. Either point the game's telemetry at the plugin, or turn Telemetry Based FFB off for that game and let the game's own force feedback through.

## You feel nothing at all

Then the plugin is not driving the wheel.

1. Check the mode at the top of the panel is **Normal**, not Off or Lightsync only. The plugin drops to Lightsync only by itself when a game streams its own Trueforce beside it; a popup and the amber line under the status say so, and [Games with native Trueforce](guide:native-trueforce) has the cure.
2. Quit G HUB completely from the system tray. It claims the wheel while it is open.
3. Unplug the wheel and plug it back in.
4. On a PlayStation G923, open G HUB once to put the wheel back into PC mode, then close it again. That wheel can drop out of PC mode after a restart or a replug.
5. Restart SimHub, then check Diagnostics on the Settings tab says the wheel is detected.

If the wheel responds but everything feels faint, that is a different problem: see [Effects feel weak, and the dial on the wheel does nothing](guide:weak-effects).
