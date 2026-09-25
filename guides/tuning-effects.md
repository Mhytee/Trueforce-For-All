<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# Tuning the effects

Every effect ships tuned, so gain is all you may want to move. Going through them one at a time is how you learn what each one feels like and find the few you want to change.

## One at a time

1. On the **Effects** tab, switch everything off except the one you are working on.
2. Trigger it. Many effects have a **Test** button beside them, though a test does not always land the way the effect does in motion. Driving is the truest way to feel an effect.
3. Move its **Gain** until it sits where you want it.
4. Switch that one off, switch the next one on, and go again.

Turn them all back on at the end.

## The other controls

Gain is how loud an effect is. The rest affect how it feels:

- **Frequency** is the pitch. Low feels like weight and thump; high feels like texture and buzz.
- **Waveform** is the character, and most effects have one. Sine is round and smooth, Square and Saw are hard edged and rasp, Triangle sits between them, and Noise is a rough churn with no pitch to it.
- **Lowpass** cuts the high frequencies out, leaving the body of the effect.
- **Highpass** cuts the low frequencies out.
- **Pulse rate** is how fast the pulse opens and closes, and **Pulse length** is how much of each one is filled. Slow and short is punctuated; fast and full is a buzz.
- **Duration** and **Amp**, on the effects that have them, set how long a one-shot lasts and how hard it hits.

## Engine pulse

Engine pulse is built from the car's firing order. Each cylinder fires at its own point in the engine cycle, so a cross-plane V8 lopes, a V-twin leaves its gap, and a rotary hums flat.

That makes the engine layout the first thing to get right. It is set at the top of the plugin, in the active car's **Car Facts**, so it follows the car wherever you take it. Most cars arrive already known. The list covers the layouts that fire differently enough to feel different: cylinder counts from one upward, the V angles and crank types that change the beat, boxers and rotaries, and the odd-fire layouts that lope rather than hum.

Past the gain and the waveform:

- **Pitch** is worked out from the cylinder count and the revs, so it needs no setting. The slider offsets it up or down if you want the engine higher or lower than life.
- **Low-end body** adds a layer that helps the low end rumble stay present at high RPM.
- **High-RPM boost** boosts the gain as the revs climb, from halfway up, so the top of the range hits as hard as the bottom.
- **Electric cars** get a muted hum or silence. Real EVs are not silent and many pump synthetic sound, so the hum is the usual choice.

## The redline buzz

The buzz fires when you enter the redline, so it is only as right as the redline is. If it arrives early or late, the car's redline is set in the same **Car Facts** as the engine layout.

**Redline offset** is the preference on top of that. Negative fires the buzz before the redline as an early shift cue, positive fires it after, and 0 sits right on it.

## Presets and sharing

**★ Save** at the top of the panel writes your tuning into the active game preset, and **Save as new…** keeps it under a name of its own. The car row beside it does the same for the car you are in, so one car can have its own feel without disturbing the game preset.

The share button next to them offers a preset to the community, and the **Presets** tab is where you browse what other people have shared. Your own presets live there too, ready to be renamed, duplicated, set as a game's default, or moved to and from a file.
