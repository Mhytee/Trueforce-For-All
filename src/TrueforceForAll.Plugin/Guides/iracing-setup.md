This turns on the plugin's effects, rev lights and wheel screen in iRacing. The force stays iRacing's own: the sim keeps computing it, and the plugin delivers it to the wheel.

Until it is set up, the plugin sits on **Lightsync only** in iRacing and leaves the sim alone. On a wheel whose lights can take a pattern, it still sets that pattern to match the car you are in. Four steps, once:

1. With iRacing closed, open `Documents\iRacing\app.ini` and set `loadTrueForceAPI=0`.
2. Start iRacing and turn its force feedback **off** in the options. Leave its strength number where it is: the plugin reads that number and scales the sim's forces by it, so a 0 there gives you a dead wheel.
3. Set the mode at the top of the panel to **Normal**.
4. On the **FFB** tab, tick **Take over force feedback for iRacing**. ([What it does](guide:telemetry-ffb).)

If the wheel stays quiet afterwards, one of the two iRacing switches (steps 1 and 2) is still on.

## Running MAIRA

Running MAIRA and TF4ALL at the same time is not supported. Close MAIRA, then set the mode to **Normal** when ready.

Making the two work together needs changes on MAIRA's side. We have offered them to MAIRA's author and are waiting to hear back. If you would like MAIRA and TF4ALL to be compatible, say so on [MAIRA's GitHub](https://github.com/mherbold/MarvinsAIRARefactored) or in [MAIRA's Discord](https://discord.gg/Y7JN3BAz72).
