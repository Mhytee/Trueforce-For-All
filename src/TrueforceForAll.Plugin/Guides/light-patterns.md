Your wheel holds **five** light patterns. (This is about the patterns themselves; [why the rev lights only follow your revs in some games](guide:wheel-lights) is a separate matter.) The library holds as many as you like, and the extras are swapped into a slot as you drive, so the wheel's five are a window onto the library rather than the whole of it.

## Your five slots are written to match your list

The top five patterns in your library are the wheel's five slots. The plugin
compares the wheel against that list and writes any slot that disagrees, and
those writes are permanent: nothing is kept, nothing is handed back, and they
stay on the wheel with SimHub closed. It compares when the settings page opens,
when a move or a delete changes which patterns are in the top five, and when
you edit a pattern that is already in a slot. Slot names are replaced with the
pattern names even where the colors already match.

The first time the tab is built, the plugin reads your five slots into the top
of the library, keeping each one's name, so they survive as patterns you can
move back up and your wheel's own menu still reads the way it did. A slot the
wheel will not answer for is left exactly as it is rather than guessed at, and
the panel says so, so nothing is written over a slot the plugin could not read
first.

Showing a pattern from further down the list borrows a slot instead: the first
one you have never programmed, or CUSTOM 5 when all five are in use. That one
is properly borrowed while the loan is open. Its colors and its name are saved
to disk before anything is written, the write is refused outright if that copy
cannot be made, and it goes back when you switch to something the wheel holds
itself, when SimHub closes, and at the next launch if SimHub was closed while
still holding it. If the wheel cannot be reached at that moment the copy is
kept and tried again. The one thing that ends a loan without returning it is
telling the plugin to write that same slot on purpose, by moving a pattern up
into the top five.

## Walking the library needs a button

The swapping happens while you are driving, so there has to be a way to step through without stopping. Any control SimHub can see will do: a wheel button, a button box, a keyboard key.

The LIGHTSYNC tab has SimHub's binder built into its opening hint, on a row called **Next pattern**, so you can set it without going looking. If you have already dismissed that hint, the same action lives on the **Controls** tab as **Rev pattern next**, alongside [everything else worth binding](guide:bindings).

Unbound, the library still works. You just change patterns from the panel instead.

## Color tuning trades brightness for accuracy

The three colors inside an LED are not equally bright, so a color that looks right on screen can arrive on the rim looking like something else. An even mix of red and green is the one people notice: it should be yellow and it comes out lime.

The plugin corrects that on the way out, and the correction ships already set, so it is on before you touch anything. The shipped values come from measurements on a G PRO, where red came out the dimmest of the three. Whether that holds on every wheel is something we would like to find out, so if yours looks off, retune it and tell us.

The correction only ever works on the **ratio** between the three channels, so what matters is which slider sits highest, not where any one of them sits. That highest channel is the reference: it keeps the value you authored, and the other two are cut relative to it.

With the shipped values the reference is red, so the cost lands on colors whose brightest channel is red. **Yellow and white come out around a quarter dimmer**, orange about a sixth. Colors peaked on green or blue are scaled back up afterwards, so pure green and pure blue are untouched, and a green-dominant mix can come out slightly brighter than you authored it.

Move the sliders and the cost moves with them. Pull red below the other two and whichever now sits highest becomes the reference instead, and the colors peaked on THAT channel start paying. From the shipped values the next highest is blue.

> [!NOTE]
> Pulling one slider down part way is not dimming that channel, it is weakening the whole correction. The sliders read as percentages, and ship at red 100%, green 61%, blue 65%. Taking red to 80% hands back roughly half the yellow correction. Take it to 60% and yellow is lime again, but the three are not level: blue is now the highest at 65%, so blue becomes the reference and white picks up a blue cast instead. Levelling all three is the off switch, whatever the number.

## Tuning it for your wheel

The shipped values suit the wheels we have looked at. If yours reads differently, the Color trim section has **Show yellow** and **Show white** buttons that light all ten LEDs in the two colors that give a tint away soonest. Judge it on the rim rather than on screen, and move a few percent at a time.

1. Press **Show yellow**. Looking green or lime? Bring the **green** slider down until it reads as yellow.
2. Press **Show white**. Hold a sheet of paper next to the rim and compare.
3. White looking blue or cyan? Bring the **blue** slider down.
4. White looking pink or warm? Bring the **red** slider down. Whichever channel is highest afterwards becomes the reference, which is fine; it just moves which colors pay for the correction.
5. Recheck after each move. A channel that is only slightly too strong needs a small change, and it is easy to overshoot into the opposite tint.

To switch the correction off, level all three sliders. To go back to the tuning the plugin ships with, use **Reset** rather than dragging them to 100%: a value you set by hand is remembered as your deliberate choice, and 100% is a choice like any other.

## Each pattern chooses whether tuning applies

- **On.** For colors you picked on screen, so they reach the wheel looking the way they look here.
- **Off.** For a pattern you took off the wheel, or tuned by eye on the rim. Those already allow for the wheel's color balance, and tuning them twice would change them.
