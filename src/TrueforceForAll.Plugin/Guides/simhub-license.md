No. SimHub is free, and the plugin works without a license.

What a license changes is the **telemetry rate**. For games the plugin does not read directly, the numbers arrive through SimHub, and without a license that feed runs at 10 Hz. Every effect is built from those numbers, so at 10 Hz they arrive in visible steps and the feel comes out coarse. A license lifts it to 60 Hz, which is a large step up.

Assetto Corsa, the Forza titles and Farming Simulator can be read directly by the plugin instead, at their own rate, and a license makes no difference to those. Two of them need setting up first:

- Forza only sends its telemetry once [Data Out is switched on and pointed at the plugin](guide:forza-setup). Until then the plugin falls back to SimHub's feed, license rate and all.
- Farming Simulator needs [the TF4ALL Enhanced Telemetry mod](guide:farming-sim) installed, for the same reason.

Assetto Corsa needs nothing: it is read directly as soon as the game starts.

iRacing sits in between. The steering force it hands over reaches the plugin from the sim's own published torque, so the handover and the effects on top of it work either way. The rest of its numbers still arrive through SimHub, so a license lifts those exactly as it does anywhere else. The same is true of RaceRoom and Le Mans Ultimate.
