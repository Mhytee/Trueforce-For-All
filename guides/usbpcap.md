<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# Why does it need USBPcap, and is that safe?

USBPcap is an open-source USB capture driver, bundled with our installer.

The plugin uses it to read your wheel's own force-feedback traffic off the USB bus, so it can carry that force into the Trueforce stream. That is what keeps your normal force feedback alive while the plugin is driving the wheel. Without it the wheel goes limp in game and only the Trueforce effects are left.

Some sims hand the plugin their force directly and need no capture for it: iRacing, RaceRoom, Le Mans Ultimate, and Assetto Corsa with the TF4ALL CSP Bridge.

The capture also watches for a second Trueforce stream on the wheel, which is how the plugin knows to step aside for [a game's own Trueforce](native-trueforce.md).

It reads USB traffic on this PC, and nothing it captures leaves the machine. Normally it listens to your wheel alone. On some wheels the plugin has to widen the capture to the whole USB controller and pick the wheel's traffic out itself. The FFB pass-through status line says **(whole-bus)** when it has done that. USBPcap is widely used beyond this project, and you can uninstall it separately at any time.

If your wheel has gone limp in game, USBPcap is the usual reason: [Force feedback: limp, weak, or silent](ffb-not-working.md) walks through it.
