Some sims publish the steering force they compute. Where they do, the plugin reads that force and carries it to the wheel over Trueforce, instead of capturing it off the USB connection. The feel stays the sim's own, with the plugin's effects on top.

This is the opposite of [Telemetry Based FFB](guide:telemetry-ffb), where the plugin builds the steering force itself. Both live on the FFB tab, and the tab changes its wording to match the game you are in.

## Where it works

- **iRacing**, which keeps publishing its steering torque even with its own force feedback switched off. [Setup](guide:iracing-setup).
- **RaceRoom**, which publishes its steering force in telemetry. [Setup](guide:raceroom-setup).
- **Assetto Corsa**, through [the TF4ALL CSP Bridge](guide:csp-bridge), which hands over the game's finished force after your in-game gain and every CSP tweak. [Setup](guide:assetto-corsa-setup).

## Why hand it over

It allows us to control the wheel's other surfaces. The rev lights and the Dynamic OLED share one pipe with the game's force, so using them normally interrupts it. With the plugin carrying the force there is nothing to interrupt. [Why](guide:force-and-lights).

## The setup differs by sim, and does not carry across

In iRacing, do **not** set its strength to 0: the plugin reads that number and a 0 leaves you with a dead wheel. Turn its force feedback off and leave the number alone.

In RaceRoom, disable force feedback and set its intensity to 0.

Follow that sim's own setup guide rather than reusing settings from another.
