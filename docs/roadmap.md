# Trueforce for All: Roadmap

A living view of where the project is heading. This is a direction doc, not a
promise of dates or order. Items move, get reordered, or get dropped as we
learn more.

---

## What we're working toward

### Force feedback detection that just works

*Status: in progress, validating on hardware.*

Your wheel's force feedback should be found automatically, with no hidden switch
to flip.

For most people it already just works. Some still have to switch on experimental
detection before their force feedback is picked up. More testing should let us
retire that toggle and make detection automatic for everyone.

### Airborne and traction-loss feel, consistent across games

*Status: in progress. Improving behavior that already exists.*

The car going light over a crest, or breaking grip in a slide, should
feel the same no matter which game you are driving in.

Right now the airborne and traction-loss effects can interfere, and they do it
differently from game to game. In Forza, the airborne effect works as intended.
In Assetto Corsa it fights the traction-loss detection: with the car airborne
the wheels spin up faster than the car is actually moving, and our traction-loss
heuristic reads that as a slide and fires when it should not. The goal is to get
airborne and traction-loss to agree across games, so leaving the ground never
gets misread as losing grip, with per-game tuning only where a title genuinely
needs it.

### Richer force feedback from telemetry

*Status: early experiment. Long-term.*

Some games send force feedback slower than the wheel can use it, so it feels
coarser than the car really is.

Logitech wheels can take FFB updates at around 1000hz, but many games send force
feedback near 60hz, and even the faster ones fall short of that (Assetto Corsa
runs about 333hz), so the wheel sits waiting between updates. Using the game's
telemetry, we could fill in the detail between those updates and drive the wheel
at its full rate, so a low-rate game feels richer without changing anything in it. A game-agnostic engine with a small per-game piece
would carry it, building on the per-game telemetry work already in the plugin. It could run on
what we ship today, since we already stream to the wheel's Trueforce endpoint at
full rate, so it doesn't need the FFB router driver. The driver would unlock it for
games with native Trueforce, where it would divert the game's signal so ours isn't
fighting it.

### Community and account features

*Status: implemented, in testing before release.*

A shared layer of car knowledge and tuning, plus the account that ties it together.

- **Community car facts.** Crowd-sourced names, engine types, and redlines fill in
  your cars automatically, cached locally so they keep working offline. You can
  confirm or correct any value, and optionally share your corrections back.
- **Engine-variant awareness.** The plugin notices when you switch to a different
  version of a car (an engine swap shows up as a new engine signature in the
  telemetry) and applies that variant's own redline and engine type, so the rev
  limiter and engine feel match the swapped engine, not the stock one.
- **Preset sharing.** Share the tuning you're driving, browse and download presets
  from other drivers, and optionally auto-update a downloaded preset when its
  author publishes a new version (unless you've edited your own copy).
- **Accounts.** Sign in and link Patreon and Discord, with a supporters wall and
  achievements you earn for in-plugin actions like contributing to the community,
  sharing presets, and submitting or correcting car facts. Supporter status
  unlocks cloud backup (below).

A lot was built at once, so it's getting a round of real-world testing and polish
before it reaches a release.

### Discord community

*Status: coming soon.*

A place to get help and connect with other drivers.

Built and getting its final tweaks before it goes live. When it launches, linking
your Discord will automatically grant roles for your supporter status and
achievements.

### Cloud backup and sync

*Status: implemented, in testing before release.*

Your settings and presets, backed up and carried between the machines you drive on.

An optional cloud backup for supporters: it saves your tuning, presets, and
settings and syncs them across your machines, so your setup follows you. Built and
in testing now, headed to a release once it's solid.

### Mappable gain

*Status: implemented, in testing before release.*

Adjust your force strength without stopping to open the app.

Bind master gain up and down to a wheel button or rotary through SimHub's Controls
to change force strength mid-session. Built and in testing.

### Home-screen gain widget

*Status: implemented, in testing before release.*

Quick master and audio gain, right on SimHub's home screen.

An optional gain widget on SimHub's home screen for master and audio gain, next to
the other feedback controls. Built and in testing.

### Easier to diagnose and fix on your own

*Status: in progress.*

When something is not working, the app should help you find out why
and fix it, without needing to ask anyone.

Two parts, both underway:

- Better troubleshooting and problem-detection dialogs that spot common issues
  (no force feedback, a game sending its data to the wrong place, a wheel that
  needs a one-time step in G HUB first) and walk you through the fix.
- A pass over the existing in-app text so instructions and tooltips are clearer
  about what to do.

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

### Lower-latency audio transport (shared-memory ring)

*Status: exploring, deferred. Small gain.*

Shave a little more delay off the audio path, so the wheel responds a hair faster
to the game's audio.

The audio helper hands samples to the plugin over a pipe today. A lock-free
shared-memory ring would trim another 1 to 2 ms, but we already captured most of
that win by shrinking the pipe read.

### LED support

*Status: in progress, via the FFB router driver.*

Your wheel's rev lights driven by the game, at the same time as force feedback.

Writing LEDs and force feedback to the wheel at once contends at the firmware and
can cut the force out, which is why it hasn't worked broadly. The FFB router driver
(below) fixes that by making us the only thing writing to the wheel, and early
testing already drives rev lights alongside force. Getting it into your hands
depends on the driver work below.

### Working in games that already have Trueforce

*Status: planned, via the FFB router driver.*

Use the plugin in games that already have their own Trueforce.

Today we step aside in those titles to avoid fighting the game for the wheel. The
FFB router driver (below) lets us take the game's Trueforce over cleanly, so we can
layer your effects on top or replace it, and reach these games with the
telemetry-driven feel above.

### FFB Router Driver

*Status: in active development.*

A Windows filter driver that sits between the game and the wheel, never touching
the game itself. It turns our passive read of the game's force feedback into an
active one, diverting the game's signals before they reach the wheel so we become
the only thing writing to it. That is what makes the rev lights and native-Trueforce work above possible.

The core loop is confirmed on hardware (a G923 in Assetto Corsa). It still needs
validation on more wheels and games, and shipping requires getting the driver
signed. That certificate runs about $400 a year, already paid out of pocket, with signing still in progress.

### Other wheel brands

*Status: stretch, far off.*

Bring our FFB enhancements to Fanatec, Moza, and other non-Logitech wheels.

Most wheels take force feedback through DirectInput, the standard channel, not
anything Logitech-specific. The router could do there what it does on Logitech:
route the FFB signal to the plugin, which modulates it and hands it back to be
re-applied, carrying force-feedback improvements like the telemetry-driven detail.
And it doesn't have to be our plugin: the router could feed any program that wants
to ingest or interact with the FFB signal, which makes it a general force-feedback
router, not just a Trueforce one.

Our Trueforce-style effects are Logitech-only for now, since they ride Logitech's
own haptic channel. Other brands have their own high-frequency channels, and
Fanatec's FullForce is where we want to start: there's an open call for captures
of it, the first step toward finding out whether the protocol can be
reverse-engineered. We think it's likely, but won't know until the captures are
in. If it can, our effects could reach Fanatec wheels, and other brands could
follow as their channels are reverse-engineered or opened up.

---

*This roadmap is a snapshot, not a commitment to timing or order. Feedback from
Patreon and Discord directly shapes what moves up.*
