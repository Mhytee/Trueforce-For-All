<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# Telemetry Based FFB

Instead of passing the game's own force feedback through, the plugin can build the steering force itself out of telemetry. There is a real sense of grip in it: the wheel goes light as the front washes out, and pulls into a countersteer as the rear steps out.

It also frees your wheel's rev lights and screen. [Why](force-and-lights.md).

## Where it works

- Forza Motorsport (2023) and Forza Horizon 4, 5 and 6. Opt-in, per game, and it needs Forza's telemetry pointed at the plugin first ([how](forza-setup.md)).
- Farming Simulator 22 and 25, where the plugin entirely replaces the game's force feedback with its own. It engages by itself. Installing [our enhanced telemetry mod](farming-sim.md) is optional, but adds ground texture, implement reactive effects, and airborne ducking.

## Before you turn it on, in Forza

Set that game's own force feedback and vibration to **0**, so the plugin is the only force on the wheel.

> [!WARNING]
> Two force streams fighting each other feels jumpy and buzzy. The force feedback tab warns you when it detects the fight, but the problem is easier to avoid than to diagnose.
