# Trueforce for All: Roadmap

A living view of where the project is heading. This is a direction doc, not a
promise of dates or order. Items move, get reordered, or get dropped as we
learn more.

New features land on the beta update channel first, and the beta is open to
everyone: switch channels in the plugin's settings to try things early.

*Last updated: September 2026.*

---

## How the plugin reaches a game

Nearly everything below depends on how much a game gives the plugin to work
with, so it is worth naming the three cases up front.

- **Games the plugin reads directly.** iRacing, Assetto Corsa, RaceRoom, Le Mans
  Ultimate, and the Forza and Farming Simulator titles. The plugin talks to the
  game itself instead of inferring from the outside, so it knows things no
  amount of guessing would give it: whether the car is off the ground, in
  Assetto Corsa, the Forza titles and Farming Simulator, or the steering torque
  the physics engine computed, in the others.
  In most of them it sends the wheel the steering force as well, with the
  effects on top, and that is the best case there is: nothing else is writing to
  the wheel, so the rev lights and the wheel's screen are free while you drive.
- **Games the plugin reads over USB.** Everything else that has force feedback
  of its own. The plugin watches the force the game sends the wheel and adds its
  effects to it. The effects work, but the game is writing to the wheel too, so
  the rev lights and the screen are what suffer. The exception is a game that
  ships Trueforce of its own: there the plugin steps aside to the wheel's
  patterns and colors, leaving the rev lights and the screen to the game,
  unless the game lets you switch its Trueforce off.
- **Games read through SimHub's generic telemetry.** Anything SimHub supports.
  The plugin gets speed, revs, gear and the rest, enough for the effects built
  from telemetry, but it has no direct knowledge of the game.

Moving a game up that list is most of what the work below is about.

For what the plugin does today, feature by feature, the release notes are the
current list.

---

## What we're working toward

### Support for more wheels

*Status: planned. Groundwork underway, no second wheel supported yet.*

Bring TF4ALL to wheels beyond Logitech's Trueforce range.

Our effects ride Logitech's Trueforce haptic channel today, which is why the
plugin is limited to those wheels. The plan has three parts:

- **Fanatec FullForce.** Fanatec's haptic stream is the closest thing to
  Trueforce on another brand, and an SDK for it ships with Fanatec's own
  driver, so our effects can carry over close to as they are. This is where
  the second brand starts.
- **Wheels without a haptic channel.** The G29 and G920, Thrustmaster, Moza
  and the rest take force feedback through DirectInput. Those wheels get
  lower-frequency versions of our effects, built from the spring, damper,
  constant force and rumble that channel offers. Some of the groundwork is
  already in: the effects engine that shipped in v0.4.0 models those same effect
  types in order to render what a game sends, so the vocabulary those wheels
  need is written. Community work on a G29/G920 backend has already started.
- **Telemetry Based FFB on all of them.** Where TF4ALL builds the whole
  steering force from telemetry, the wheel only needs a way to receive force.
  Farming Simulator is the obvious first stop: TF4ALL is the only force
  feedback mod the game has, and today it is locked to Trueforce-enabled
  Logitech wheels. That should not stay true.

The name changes with it. Trueforce For All describes what the plugin does
today, and stops fitting once it drives wheels that have no Trueforce. A
rebrand comes once that support is real, not before.

### Telemetry Based FFB in more games

*Status: shipping for Forza and Farming Simulator. More games to come.*

Real force feedback for games whose own is thin or missing.

Telemetry Based FFB builds the steering force from the game's telemetry
instead of passing the game's own force through. It replaced Farming
Simulator's centering spring with weight, ground texture and implement load,
and it is an opt-in replacement for the Forza titles' own force. The engine is
game-agnostic with a small per-game piece, so each new game is mostly a
question of what its telemetry offers. Games with basic or absent force
feedback are the first candidates, and we would rather hear which ones those
are from the people playing them than guess.

### More effects, and better ones

*Status: shipping, and never closed.*

The list of things you can feel through the wheel keeps growing, and the ones
already there keep getting closer to the real thing.

The effects are what the plugin adds on top of whatever force reaches the
wheel, and they are never really finished. Some of the work is new sensations
the wheel has never given you. Most of it is quieter: an effect that fires in
the wrong place, or feels like the idea of something rather than the thing
itself, being reworked until it reads correctly. Every game the plugin learns
to read directly feeds this, because an effect grounded in what the car is
actually doing beats one inferred from the little a game gives away.

### Airborne and traction-loss feel, consistent across games

*Status: in progress. Shipping in Assetto Corsa, the Forza titles and Farming Simulator.*

The car going light over a crest, or breaking grip in a slide, should
feel the same no matter which game you are driving in.

In the games the plugin reads directly, going airborne no longer sets off
traction loss (the wheels spinning up freely over a jump used to read as a
slide), the wheel goes quiet when the tires leave the ground, and grip effects
weigh each tire's real load so unloaded wheels stay quiet.

Still to do: bring real airborne detection to games read through SimHub's
generic telemetry, and stop the road-bump effects from reacting while the car
is off the ground. The goal stays the same: airborne and traction loss agreeing
in every game, with per-game tuning only where a title genuinely needs it.

### Force feedback from the sim's own torque telemetry

*Status: shipping for iRacing, RaceRoom and Le Mans Ultimate. Any sim that
publishes its steering torque can follow.*

The sim's own force, with TF4ALL's effects on top and the wheel's lights and
screen free to work.

Some sims publish the force they want the wheel to hold as part of their
telemetry. iRacing does: each 60hz packet carries several torque samples, 360hz
of force in all, and TF4ALL carries that to the wheel with the effects on top,
adding nothing the sim does not already provide. RaceRoom publishes its
steering torque in its telemetry, and Le Mans Ultimate publishes it in the
shared memory its developers document, so both are carried the same way now.
Others expose the same thing: if a sim you play publishes the torque it wants
the wheel to hold, and either ships no Trueforce of its own or lets you switch
it off, it can join this list, and telling us is the fastest way to get it
looked at.

### Richer force feedback from telemetry

*Status: exploring. Long-term.*

Some games send force feedback slower than the wheel can use it, so it feels
coarser than the car really is.

Logitech wheels can take FFB updates at around 1000hz, but many games send force
feedback near 60hz, and even the faster ones fall short of that (Assetto Corsa
runs about 333hz), so the wheel sits waiting between updates. Using the game's
telemetry, we could fill in the detail between those updates and drive the wheel
at its full rate, so a low-rate game feels richer without changing anything in
it. A game-agnostic engine with a small per-game piece would carry it, building
on the per-game telemetry work already in the plugin. It could run on what we
ship today, since we already stream to the wheel's Trueforce endpoint at full
rate, so it doesn't need the FFB router driver. The driver would unlock it for
games with native Trueforce, where it would divert the game's signal so ours
isn't fighting it.

This is separate from Telemetry Based FFB, which builds the force entirely from
telemetry, and from carrying a sim's published torque (above). The idea here
keeps the game's own force feedback and fills in the detail between its updates.

### Make it easier to diagnose and fix problems on your own

*Status: in progress. What is built is shipping.*

When something is not working, the app should help you find out why
and fix it, without needing to ask anyone.

The guides browser, a self-test that ends by naming the single most blocking
issue, and banners that catch a game sending its data to the wrong place are
all live. What remains is catching more problems before you have to go looking
for them.

### The wheelbase's own settings, from the plugin

*Status: planned. LIGHTSYNC was the first piece.*

Change what the wheelbase itself does, without touching the wheel or opening
G HUB.

G HUB has to be closed while the plugin runs, so the settings it manages
(range, strength, damping and the rest) are left to the wheelbase's own
on-wheel menu. Those settings travel over the same protocol the plugin already
speaks to the wheel, and mescon's Linux driver, which documents the protocol
the OLED and lighting work were built on, is a near-complete G HUB replacement
on Linux. The plan is to bring more of that in: bindings that change wheelbase
settings mid-session from a button or rotary, and per-game wheelbase profiles
that apply as the game starts, the way G HUB's do. LIGHTSYNC, which took over
the lighting side, was the first piece.

### Advanced EQ (rig resonance control)

*Status: in progress. Built, reaching the beta channel next.*

Kill the specific buzz or rattle your rig makes at certain
frequencies, without dulling the detail you actually want to feel.

Some frequencies make a wheelbase or rig resonate, causing unwanted tones and rattling. A
parametric EQ lets you pull those problem bands down (frequency, width, and amount
per band) while leaving the rest of the feel intact. That editor is built, and
since the resonances are mostly a wheelbase trait, your EQ settings already
travel in cloud backup. Default curves per wheelbase, and sharing a curve the
way presets are shared, are what is still ahead.

### Localization

*Status: planned. Large effort, high reach.*

Use Trueforce for All in your own language.

Translate the interface, tooltips, effect names and descriptions, and the
what's-new notes. Given the project's community roots, translations would most
likely be community-contributed, with the app built to swap languages cleanly.

### Rev lights in games without native LED support

*Status: shipping wherever TF4ALL carries the force. The driver brings it to
everyone else.*

Your wheel's rev lights driven from telemetry, in games that never lit them.

This is about the games themselves, not the lights: the LIGHTSYNC tab, pattern
creation and per-car colors already work in every game. Some games light the
wheel's rev strip natively. In the ones that do not, TF4ALL can drive the
lights from telemetry, but writing lights and force feedback to the wheel at
once contends at the firmware and can cut the force out, which is why it hasn't
worked broadly. It works wherever the plugin is the only thing talking to the
wheel, which is now most of the games it reads directly: Telemetry Based FFB,
iRacing, RaceRoom, Le Mans Ultimate, and Assetto Corsa, where the plugin took
the lights over outright and fills them from the car's own shift light data.
What is left is the games the plugin reads over USB without carrying the force
itself, and that set shrinks with every release: each game the plugin learns to
read directly takes its lights with it. The FFB router driver (below) is how the
rest get there in one go.

### Working in games that already have Trueforce

*Status: shipping for iRacing and Le Mans Ultimate. The rest via the FFB
router driver.*

Use the plugin in games that already have their own Trueforce.

iRacing was the first: its Trueforce is switched off in the sim's own config,
and TF4ALL carries the sim's force instead, with the effects on top. Le Mans
Ultimate followed, through its own Vendor Specific Force Feedback switch, and
Dirt Rally 2.0, GRID (2019) and Automobilista 2 have a real off switch too. The
titles that give you a slider and no off switch are the ones we step aside in
today, rather than fight the game for the wheel. The FFB router driver (below) lets us take the game's
Trueforce over cleanly in those, to layer your effects on top or replace it.

### FFB Router Driver

*Status: in progress. Works on hardware, not yet signed for general use.*

A Windows filter driver that sits between the game and the wheel, never touching
the game itself. It turns our passive read of the game's force feedback into an
active one, diverting the game's signals before they reach the wheel so we become
the only thing writing to it. That is what makes the rev lights and native-Trueforce work above possible.

It is not the only road to that result. Every game the plugin learns to read
directly gets there without any driver at all, which is how Assetto Corsa,
RaceRoom and Le Mans Ultimate got their lights back. The driver is the general
answer: one mechanism that works whatever the game does, instead of one game at
a time.

The core loop is confirmed on hardware (a G923 in Assetto Corsa). What stands
between that and something you could install is signing. Loading a driver on an
ordinary PC needs Microsoft's countersignature, which goes through the Microsoft
hardware partner program. We hold our own EV code signing certificate for the
project (about $400 a year, paid out of pocket), so that side is in place, but
the driver itself has not been through the process yet. Validation on more
wheels and games follows once it can be installed normally.

---

*This roadmap is a snapshot, not a commitment to timing or order. Feedback from
Patreon and Discord directly shapes what moves up.*
