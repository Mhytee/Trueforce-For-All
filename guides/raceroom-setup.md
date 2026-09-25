<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# RaceRoom

RaceRoom publishes the steering force it computes in its telemetry. With this on, the plugin reads that force and carries it to the wheel itself, with its effects on top, and your rev lights and the wheel's screen stop interrupting it.

Two steps:

1. In RaceRoom, disable force feedback. The intensity slider does not matter.
2. On the force feedback tab, tick **Take over force feedback for RaceRoom**. ([What it does](force-handover.md).)

Step 1 is also what frees the rev lights and the screen: with its force feedback off, RaceRoom stops driving the wheel's lights.

Start SimHub before RaceRoom. If the lights or screen do nothing, that is the usual cause.

## Getting the strength right

Drive a couple of clean laps. When the **Apply** button beside **Car's max** on the force feedback tab shows a number, press it. Each car keeps its own number. Nudge **Car's max** down for heavier, up for lighter.

From a wheel button: bind **AutoForce**, **IRacingMaxForceUp** and **IRacingMaxForceDown** in SimHub's Controls tab. They keep their iRacing names but work here too.

## A parked wheel

This route drops RaceRoom's parking resistance, so the plugin adds its own: firm when parked, gone as you gain speed. Tune it under **Stationary friction** on the force feedback tab, and tick **Stationary spring** there for a centering pull as well.

## If the wheel pulls the wrong way

RaceRoom's **Invert FFB** option flips the force the plugin reads. Keep it on. If the wheel pulls into corners instead of centering, that is the cause.

## Going back

Untick the box and turn RaceRoom's force feedback back on. RaceRoom returns to the USB capture, as before.
