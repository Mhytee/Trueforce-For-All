<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# Where your rev lights and screen work

Your wheel's rev lights and screen work in some games and not in others, and the plugin sometimes leaves them alone entirely. This is what decides it.

## Sharing the wheel with the game

Your wheel listens in two places. Force feedback, the light bar and the screen all arrive on one connection. Trueforce arrives on its own.

When a force value exists on the Trueforce connection, the wheel prefers it, ignoring any force being sent through the other channel. The game continues sending its own FFB and the wheel keeps receiving it, it just ignores it in favor of the Trueforce signal.

That is the whole trick. The plugin reads what the game is sending, folds it into Trueforce, adds its own effects on top, and never has to fight the game for the wheel.

This method works for all games, as long as they aren't sending Trueforce signal themselves.

## Why the lights are the hard part

The game's force is still being received by the wheel, even though it is being ignored. If we send a light or screen command while the wheel has force arriving, it cuts the force feedback for about a second and a half.

Trueforce being the force it prefers does not help here. What matters is that the game's force is still arriving at all.

So the question is not whether our force is winning. It is whether that connection is quiet.

## Where your lights will work

Two situations make that connection quiet, and they are the two where the plugin will drive your lights:

- **[Telemetry Based FFB](telemetry-ffb.md)**, where the plugin builds the force and the game is sending none.
- **[Force handover](force-handover.md)**, where the sim hands its force to the plugin instead of the wheel. iRacing, RaceRoom, Le Mans Ultimate, and Assetto Corsa through the CSP Bridge.

Anywhere else the plugin leaves your lights as they are rather than costing you the feel of the car. Setting a single pattern is different: that is one write rather than a constant stream, so a car's colors can still land in a game whose rev lights stay dark. Patterns need a wheel whose strip is programmable: the G PRO and the RS50 have one, the G923's strip has a fixed look.

## What we are exploring

Ways to reach the lights and screen without going down the busy connection at all, including a custom USB routing driver and a proxy wheel device. Nothing is ready to announce.
