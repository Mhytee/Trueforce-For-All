## Your top five patterns are saved to the wheel itself

Move a pattern into the top five, reorder them, or edit one already there, and the wheel is updated to match. The patterns stay on the wheel with SimHub closed and in other games.

## The rest of the library needs SimHub open

The wheel only holds five patterns on its own. To cycle the extended library, bind **Rev pattern next** to any control SimHub can see; the binding lives on the [Controls tab](tab:controls) with [everything else worth binding](guide:bindings). Unbound, the library still works. You just change patterns from the panel instead.

## The wheel can automatically match the car you're driving

Tick **Match my wheel to the car I'm driving** and, for covered cars, your wheel takes on the real car's colors and fill direction as you get in it. A car without an entry keeps the pattern you chose. [Where the data comes from, and what else it sets](guide:lovely-car-data).

## Color tuning trades brightness for accuracy

The three colors inside an LED are not equally bright, so a color that looks right on screen can arrive on the rim looking like something else. The plugin corrects that using the [Color Trim](tab:color-trim) section. It is pretuned but can be adjusted manually for your wheel. It works by dimming color channels rather than boosting, so the accuracy costs some brightness.

## Tuning it for your wheel

The [Color Trim](tab:color-trim) section has **Show yellow** and **Show white** buttons that light all ten LEDs in the two colors that give a tint away soonest. Judge it on the rim rather than on screen, and move a few percent at a time.

1. Press **Show yellow**. Looking green or lime? Bring the **green** slider down until it reads as yellow.
2. Press **Show white**. Hold a sheet of paper next to the rim and compare.
3. White looking blue or cyan? Bring the **blue** slider down.
4. White looking pink or warm? Bring the **red** slider down. If that leaves another channel sitting highest, that is fine; it just moves which colors pay for the correction.
5. Recheck after each move. A channel that is only slightly too strong needs a small change, and it is easy to overshoot into the opposite tint.

To switch the correction off, level all three sliders. To go back to the tuning the plugin ships with, use **Reset** rather than dragging them to 100%: a value you set by hand is remembered as your deliberate choice, and 100% is a choice like any other.

## Each pattern chooses whether tuning applies

- **On.** For colors you picked on screen, so they reach the wheel looking the way they look here.
- **Off.** For a pattern you took off the wheel, or tuned by eye on the rim. Those already allow for the wheel's color balance, and tuning them twice would change them.
