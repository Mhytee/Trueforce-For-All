<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# Force handover

Some sims publish the steering force they compute. Where they do, the plugin reads that force and carries it to the wheel over Trueforce, instead of capturing it off the USB connection. The feel stays the sim's own, with the plugin's effects on top.

This is the opposite of [Telemetry Based FFB](telemetry-ffb.md), where the plugin builds the steering force itself. Both live on the force feedback tab, and the tab changes its wording to match the game you are in.

## Where it works

- **iRacing**, which keeps publishing its steering torque even with its own force feedback switched off. [Setup](iracing-setup.md).
- **RaceRoom**, which publishes its steering force in telemetry. [Setup](raceroom-setup.md).
- **Le Mans Ultimate**, which publishes its steering shaft torque in its shared memory. [Setup](lmu-setup.md).
- **Assetto Corsa**, through [the TF4ALL CSP Bridge](csp-bridge.md), which hands over the game's finished force after your in-game gain and every CSP tweak. [Setup](assetto-corsa-setup.md).

## Why hand it over

It allows us to control the wheel's other surfaces. The rev lights and the Dynamic OLED share one pipe with the game's force, so using them normally interrupts it. With the plugin carrying the force there is nothing to interrupt. [Why](force-and-lights.md).

## The setup differs by sim, and does not carry across

In iRacing, do **not** set its strength to 0: the plugin reads that number and a 0 leaves you with a dead wheel. Turn its force feedback off and leave the number alone.

In RaceRoom, disable force feedback. Its intensity slider can stay where it is.

In Le Mans Ultimate, set Vendor Specific Force Feedback to Off and leave the strength where it is.

Follow that sim's own setup guide rather than reusing settings from another.
