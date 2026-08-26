A few games ship Trueforce on PC themselves. The plugin defaults to **Lightsync only** in those, rather than fighting the game for the same channel on the wheel.

You can take it over if you would rather tune the feel yourself than take whatever the game hardcodes: switch the game's own Trueforce off, then set the mode at the top of the panel to **Normal**.

> [!WARNING]
> A slider at 0 is not off. Several of these games keep the Trueforce API live even at zero, so the plugin drives a channel the game is still holding and the wheel whines. Only a real switch or a config setting releases it.

Where a real off switch exists:

- iRacing: `loadTrueForceAPI=0` in `app.ini` ([full setup](guide:iracing-setup))
- Dirt Rally 2.0: an in-game Trueforce on/off switch
- GRID (2019): an in-game Trueforce on/off switch
- Automobilista 2: the `disableTF` Steam launch option

Assetto Corsa Competizione, EVO and Rally offer a slider and no off switch that anyone has found, so the plugin stays out of the way in those. Plain Assetto Corsa is not one of these games at all: it ships no Trueforce, so the plugin runs there normally and reads its telemetry directly.

The full table, including the titles nobody has tested yet, is in the README on GitHub.
