iRacing keeps working out what the car is doing. It just stops driving the wheel itself and hands those forces to the plugin, so the feel stays the sim's own, and your rev lights and wheel screen come back with it.

The plugin starts on **Lightsync only** in iRacing, which leaves the sim completely alone and sets the wheel's pattern to match the car you are in. For the rest, four steps, once:

1. With iRacing closed, open `Documents\iRacing\app.ini` and set `loadTrueForceAPI=0`.
2. Start iRacing and turn its force feedback **off** in the options. Leave its strength number where it is: the plugin reads that number and scales the sim's forces by it, so a 0 there gives you a dead wheel.
3. Set the mode at the top of the panel to **Normal**.
4. On the **FFB** tab, tick **Take over force feedback for iRacing**. ([What it does](guide:telemetry-ffb).)

If the wheel stays quiet afterwards, one of the two iRacing switches is still on.
