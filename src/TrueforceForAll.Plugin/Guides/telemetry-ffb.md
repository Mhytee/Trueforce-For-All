Instead of passing the game's own force feedback through, the plugin can build the steering force itself out of telemetry. There is a real sense of grip in it: the wheel goes light as the front washes out, and pulls into a countersteer as the rear steps out.

It also frees your wheel's rev lights and screen. [Why that is](guide:wheel-lights).

## Where it works

- Forza Motorsport (2023) and Forza Horizon 4, 5 and 6. Opt-in, per game, and it needs Forza's telemetry pointed at the plugin first ([how](guide:forza-setup)).
- Farming Simulator 22 and 25, where the plugin entirely replaces the game's force feedback with its own. It engages by itself, with nothing to switch on and no mod required. [The telemetry mod](guide:farming-sim) is a separate thing: it adds ground texture, the implement thud and the airborne cut on top of the takeover.
- iRacing, where it is a different thing: the plugin carries iRacing's own solved forces rather than inventing any, so the feel stays the sim's.

## Before you turn it on, in Forza

Set that game's own force feedback and vibration to **0**, so the plugin is the only force on the wheel.

> [!WARNING]
> Two force streams fighting each other feels jumpy and buzzy. The FFB tab warns you when it detects the fight, but the problem is easier to avoid than to diagnose.

## In iRacing it is the opposite

Do **not** set iRacing's force feedback strength to 0. The plugin reads that number and scales the sim's own forces by it, so a 0 there leaves you with a dead wheel.

Turn iRacing's force feedback **off** instead, and leave its strength where it is. The [iRacing setup guide](guide:iracing-setup) walks through both settings.
