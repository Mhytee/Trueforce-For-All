Forza sends its telemetry to **one place only**. For the plugin to do its best work, that place has to be the plugin rather than SimHub.

Telemetry that arrives secondhand through SimHub is thinner: less road texture detail, no airborne feel, and [Telemetry Based FFB](guide:telemetry-ffb) cannot run on it at all. The plugin falls back to that feed so the wheel is never dead, but it is not the intended path.

## Point Forza at the plugin

1. In Forza, open **Settings**, then **HUD and Gameplay**, and find **UDP Race Telemetry**.
2. Turn **Data Out** ON.
3. Set **DATA OUT IP ADDRESS** to `127.0.0.1`.
4. Set **DATA OUT IP PORT** to match {{guide:the **Port** box in the plugin's UDP telemetry settings, on the Settings tab|panel:the **Port** box below}}. It is `5300` unless you changed it.

Once it works, the **Status** line starts counting packets. If it stays at zero, open **Not receiving packets?** for the fixes in order.
