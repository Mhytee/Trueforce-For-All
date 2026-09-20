The TF4ALL CSP Bridge is a small script that runs inside Assetto Corsa and hands the game's force feedback to the plugin directly. [Installing it](guide:assetto-corsa-setup) takes one click.

## Why a script is needed

Assetto Corsa does not publish its finished steering force anywhere the plugin can read. Custom Shaders Patch has one place where that number exists: the FFB post-processing slot, which CSP offers to scripts that want to shape the force on its way to the wheel.

The bridge sits in that slot. It does not shape anything. It reads the force at the moment the game has finished with it and hands it to the plugin.

## What that gets you

- **The wheel feels exactly as you tuned it.** The force the bridge hands over is the finished one, after your in-game gain and every CSP FFB tweak, so your tuning carries through untouched.
- **The Dynamic OLED and LED pattern changes stop costing force.** With the plugin carrying the force, the wheel's FFB does not cut out when we write to the LEDs or Dynamic OLED. [Why](guide:force-and-lights).
- **Your lights match the car.** The bridge also reads the car's shift light data out of its files: the revs each light comes on at, the colors it uses, and how fast it flashes at the redline.

## What it does not do

It does not change your game's force feedback settings, in the game or in CSP, and removing it puts Assetto Corsa back exactly as it was.

## Keeping it current

The plugin ships the script and updates an installed copy by itself. The first time you enter Assetto Corsa after a CSP Bridge update Assetto Corsa needs a restart, because CSP only reads its scripts at startup.
