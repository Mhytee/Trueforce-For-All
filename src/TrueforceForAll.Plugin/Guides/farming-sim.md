Farming Simulator does not publish the physics the plugin needs, so we ship a small mod that does. It adds ground texture through the wheel, the implement thud as your equipment drops into work, and the airborne cut.

1. Install it for your game. It copies straight into the game's mods folder.
2. Restart Farming Simulator if it is running. It only reads its mods folder at startup.
3. Tick **TF4ALL Enhanced Telemetry** in the mod list when you load your save.

**Remove** takes it back out again. Your force feedback keeps working without
it: the plugin builds Farming Simulator's steering force itself, and the mod is
what adds ground texture, the implement thud and the airborne cut on top.
Uninstalling the plugin from Windows removes the mod as well.

The plugin replaces Farming Simulator's centering spring with a steering force of its own whether or not this mod is installed{{guide: ([what that is](guide:telemetry-ffb))|panel: (see the Telemetry FFB tab)}}. What the mod adds is the physics underneath everything else: ground texture, the implement thud, and the airborne cut.
