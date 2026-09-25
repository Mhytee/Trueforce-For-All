Le Mans Ultimate publishes the steering torque it computes in its shared memory. With this on, the plugin reads that torque and carries it to the wheel itself, with its effects on top, and your rev lights and the wheel's screen stop interrupting it.

Three steps:

1. In the game, **Settings > Controls > Force Feedback**: set **Vendor Specific Force Feedback** to Off. That is the game's own Trueforce, and the plugin steps aside to Lightsync only while it is on. The force feedback strength and effects can stay as they are.
2. **Settings > Wheel and Pedals > Calibration**: switch **Use LEDs** off, so the game stops driving the wheel's lights and the plugin's rev lights and screen can.
3. On the force feedback tab, tick **Take over force feedback for Le Mans Ultimate**. ([What it does](guide:force-handover).)

Start SimHub before the game. If the lights or screen do nothing, that is the usual cause.

SimHub has to be reading the game: on SimHub's Games page, Le Mans Ultimate must show as configured. SimHub reads it through a plugin it installs into the game, and without that the plugin holds the wheel released, as it does for a pause.

## Getting the strength right

Drive a couple of clean laps. When the **Auto** button beside **Car's peak force** on the force feedback tab shows a number, press it. That is the peak the car pushed, in Nm, and each car keeps its own. Nudge it down to make the car feel heavier, up to make it lighter.

From a wheel button: bind **AutoForce**, **IRacingMaxForceUp** and **IRacingMaxForceDown** in SimHub's Controls tab. They keep their iRacing names but work here too.

## Soft lock

The game does not report the wheel's rotation, so the plugin measures where the car's lock falls on the wheel's travel over the first seconds of steady cornering in each car. Until that settles the wheel turns freely past the lock.

## A parked wheel

This route drops the game's parking resistance, so the plugin adds its own: firm when parked, gone as you gain speed. Tune it under **Stationary friction** on the force feedback tab, and tick **Stationary spring** there for a centering pull as well.

## Going back

Untick the box. With Vendor Specific Force Feedback still off, Le Mans Ultimate goes back to the USB capture, as before.
