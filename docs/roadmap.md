# Trueforce for All: Roadmap

A living view of where the project is heading. This is a direction doc, not a
promise of dates or order. Items move, get reordered, or get dropped as we
learn more.

New features land on the beta update channel first, and the beta is open to
everyone: switch channels in the plugin's settings to try things early.

*Last updated: August 2026.*

---

## On the beta channel now

The 0.2 line has been on the beta channel since July and is nearing its move
to stable. It has had extensive testing and bug reports have slowed a lot; we
are letting it live in the wild a while longer so people have more time to
find what is left.

What it carries:

- **iRacing support.** The sim's own steering force carried to the wheel with
  TF4ALL's effects layered on top, and the rev lights and wheel screen working
  at the same time.
- **Telemetry Based FFB** for the Forza titles and Farming Simulator 22 and 25:
  the steering force built entirely from telemetry, with Auto strength to land
  every car at the heaviness you set. The Farming Simulator side comes with an
  Enhanced Telemetry mod the plugin installs for you.
- **The wheel's OLED screen** (G PRO and RS50), with ready-made screens or a
  layout of your own.
- **LIGHTSYNC.** A tab for the wheel's lights: a pattern library bigger than
  the wheel's five slots, and colors that match the car you are driving.
- **TF4ALL Dash.** A phone or tablet dashboard that also controls the plugin
  while you drive.
- **Guides in the app.** Setup for the games that need it and answers to the
  questions people ask most, behind the ? in the header.
- **Community and accounts.** Community car facts, preset sharing, accounts
  with Patreon and Discord linking, achievements, and cloud backup and sync for
  supporters.
- **Mappable gain** and the **home-screen gain widget**, for changing strength
  without opening the app.

---

## What we're working toward

### Support for more wheels

*Status: direction set. First steps underway.*

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
  constant force and rumble that channel offers. Community work on a G29/G920
  backend has already started.
- **Telemetry Based FFB on all of them.** Where TF4ALL builds the whole
  steering force from telemetry, the wheel only needs a way to receive force.
  Farming Simulator is the obvious first stop: TF4ALL is the only force
  feedback mod the game has, and today it is locked to Trueforce-enabled
  Logitech wheels. That should not stay true.

The name changes with it. Trueforce For All describes what the plugin does
today, and stops fitting once it drives wheels that have no Trueforce. A
rebrand comes once that support is real, not before.

### Telemetry Based FFB in more games

*Status: on the beta channel for Forza and Farming Simulator. More games
planned.*

Real force feedback for games whose own is thin or missing.

Telemetry Based FFB builds the steering force from the game's telemetry
instead of passing the game's own force through. It replaced Farming
Simulator's centering spring with weight, ground texture and implement load,
and it is an opt-in replacement for the Forza titles' own force. The engine is
game-agnostic with a small per-game piece, so each new game is mostly a
question of what its telemetry offers. Games with basic or absent force
feedback are the first candidates.

### Force feedback detection that just works

*Status: in progress, validating on hardware.*

Your wheel's force feedback should be found automatically, with no hidden switch
to flip.

For most people it already just works. Some still have to switch on experimental
detection before their force feedback is picked up. More testing should let us
retire that toggle and make detection automatic for everyone.

### Airborne and traction-loss feel, consistent across games

*Status: in progress. Live on the beta channel in Assetto Corsa, the Forza
titles and Farming Simulator.*

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

*Status: live for iRacing. More games planned.*

The sim's own force, with TF4ALL's effects on top and the wheel's lights and
screen free to work.

Some sims publish the force they want the wheel to hold as part of their
telemetry. iRacing does: each 60hz packet carries several torque samples, 360hz
of force in all, and TF4ALL carries that to the wheel with the effects on top,
adding nothing the sim does not already provide. Other games publish their
force targets the same way, and support for those is planned.

### Richer force feedback from telemetry

*Status: early experiment. Long-term.*

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

*Status: mostly on the beta channel.*

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

*Status: planned. Designed in concept, not started.*

Kill the specific buzz or rattle your rig makes at certain
frequencies, without dulling the detail you actually want to feel.

Some frequencies make a wheelbase or rig resonate, causing unwanted tones and rattling. A
parametric EQ lets you pull those problem bands down (frequency, width, and amount
per band) while leaving the rest of the feel intact. We would ship default curves
per wheelbase and let you fine-tune for your own rig on top. Since the resonances
are mostly a wheelbase trait, your EQ settings would travel in cloud backup and
could be shared like presets.

### Localization

*Status: planned. Large effort, high reach.*

Use Trueforce for All in your own language.

Translate the interface, tooltips, effect names and descriptions, and the
what's-new notes. Given the project's community roots, translations would most
likely be community-contributed, with the app built to swap languages cleanly.

### Rev lights in games without native LED support

*Status: on the beta channel wherever TF4ALL carries the force. The driver
brings it to everyone.*

Your wheel's rev lights driven from telemetry, in games that never lit them.

This is about the games themselves, not the lights: the LIGHTSYNC tab, pattern
creation and per-car colors already work in every game. Some games light the
wheel's rev strip natively. In the ones that do not, TF4ALL can drive the
lights from telemetry, but writing lights and force feedback to the wheel at
once contends at the firmware and can cut the force out, which is why it hasn't
worked broadly. Today it works wherever the plugin is the only thing talking to
the wheel: with Telemetry Based FFB, and in iRacing, where TF4ALL carries the
sim's force. Games that drive the wheel's force themselves, Assetto Corsa among
them, need the FFB router driver (below) to make that true no matter where the
force comes from.

### Working in games that already have Trueforce

*Status: iRacing first, live on the beta channel. The rest via the FFB router
driver.*

Use the plugin in games that already have their own Trueforce.

iRacing is the first: its Trueforce is switched off in the sim's own config,
and TF4ALL carries the sim's force instead, with the effects on top. Other
titles offer no such switch, so we step aside in them today rather than fight
the game for the wheel. The FFB router driver (below) lets us take the game's
Trueforce over cleanly in those, to layer your effects on top or replace it.

### FFB Router Driver

*Status: built and signed on our side. Waiting on Microsoft.*

A Windows filter driver that sits between the game and the wheel, never touching
the game itself. It turns our passive read of the game's force feedback into an
active one, diverting the game's signals before they reach the wheel so we become
the only thing writing to it. That is what makes the rev lights and native-Trueforce work above possible.

The core loop is confirmed on hardware (a G923 in Assetto Corsa). The driver is
signed with our own certificate (about $400 a year, paid out of pocket), but
loading on an ordinary PC needs Microsoft's countersignature, and that requires
admission to the Microsoft hardware partner program. The application is in,
and it is the last gate before the driver can go out. Validation on more wheels
and games follows once it can be installed normally.

---

*This roadmap is a snapshot, not a commitment to timing or order. Feedback from
Patreon and Discord directly shapes what moves up.*
