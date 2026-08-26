No. SimHub is free, and the plugin works without a licence.

What a licence changes is the **telemetry rate**. In games the plugin does not read directly, the numbers arrive through SimHub, and unlicensed that pipe runs at 10 Hz. Every effect is built from those numbers, so at 10 Hz they arrive in visible steps and the feel comes out coarse. A licence lifts it to 60 Hz, which is a large step up.

Assetto Corsa, the Forza titles and Farming Simulator can be read directly by the plugin instead, at their own rate, and a licence makes no difference to those. Two of them need setting up first:

- Forza only sends its telemetry once [Data Out is switched on and pointed at the plugin](guide:forza-setup). Until then the plugin falls back to SimHub's pipe, licence rate and all.
- Farming Simulator needs [the TF4ALL Enhanced Telemetry mod](guide:farming-sim) installed, for the same reason.

Assetto Corsa needs nothing: it is read from shared memory as soon as it starts.
