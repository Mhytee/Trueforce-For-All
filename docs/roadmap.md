# Trueforce for All: Roadmap

A living view of where the project is heading. This is a direction doc, not a
promise of dates or order. Items move, get reordered, or get dropped as we
learn more. Clean enough to share with supporters on Patreon and Discord;
honest enough to say what is real, what is partial, and what is still just an
idea.

Each item carries a status: **in progress**, **partial**, **planned**, or
**exploring**.

---

## Recently shipped

The base is free and stays free. Recent work:

- Community preset sharing: upload, browse, vote, and download tuning presets
  made by other drivers.
- Per-car facts layer so the right character settings follow a car across
  setups, learned from the community.
- UI overhaul with tabbed layout, plus Account and Support tabs.
- Forza UDP telemetry source (FH6 and others) with road bumps, engine pulse,
  and airborne feel.
- Rev limiter and airborne effects, on by default where they help most.
- Account linking for Patreon and Discord, with a supporters wall.
- Achievements, including recognition for early and founding supporters.
- Supporter backup and sync: optional cloud backup of your settings and presets
  so your tuning travels between machines (supporter feature).

---

## What we're working toward

### Force feedback detection that just works

*Status: in progress, validating on hardware.*

When a game puts out its own force feedback, Trueforce for All should
find it and pass it straight through every time, on whatever Trueforce-enabled
Logitech wheel you own, without you flipping a hidden switch.

We already work with every game. The harder part is the wheel side: different
wheels report their force feedback on different paths and in different formats,
so reliably picking it up means recognizing every one of those. Some of that
detection currently sits behind an experimental toggle while we validate it on
real hardware (it reads more of the ways a wheel can report force feedback, and
it self-heals the tap if it drops instead of going quiet). The goal is to make
detection automatic for every wheel, so nobody has to find and flip that toggle.

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

### Polishing the new community and account features

*Status: in progress, ongoing.*

The new sharing, car facts, account, and supporter features should
feel smooth, not rough.

A lot landed at once. New surface area means new bugs, so expect a run of fixes
and small refinements as real-world use surfaces the rough edges.

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

Some frequencies make a wheelbase or rig ring and rattle, which shows up as an
unwanted buzz in the wheel. A parametric EQ lets you pull those problem bands
down (set the frequency, width, and amount for each band) while leaving the rest
of the feel intact.

How it would work:

- Ship sensible default curves per wheelbase, since a given wheelbase model
  tends to resonate at the same frequencies for everyone who owns one.
- Let you fine-tune the bands for your own rig on top of those defaults, since
  mounting and cockpit add resonances of their own.
- Sits as a filter on the final wheel output, after effects are mixed, before
  the signal reaches the wheel.

Because the resonances are mostly a property of the wheelbase, EQ curves travel
in your cloud backup and, down the line, can be shared in the community library
the way tuning presets are, keyed to the wheelbase they were made for.

### Localization

*Status: planned. Large effort, high reach.*

Use Trueforce for All in your own language.

Translate the interface, tooltips, effect names and descriptions, and the
what's-new notes. Given the project's community roots, translations would most
likely be community-contributed, with the app built to swap languages cleanly.

### Lower-latency audio transport (shared-memory ring)

*Status: exploring, deferred. Small gain, higher risk.*

Shave a little more delay off the audio path, so the wheel responds a
hair faster to what is happening in the game's audio.

The audio-capture helper runs as a separate process and currently hands samples
to the plugin over a simple pipe. Replacing that pipe with a lock-free
shared-memory ring would cut roughly 1 to 2 ms of typical latency and remove the
pipe's worst-case coalescing spike (about 2.7 ms). This only touches the
captured-audio path; telemetry effects like engine pulse and road bumps, and
force feedback passthrough, are unaffected.

It is deferred on purpose. We already shrank the pipe read to 1 KB, which
captured most of the win, so the remaining gain is small. A cross-process
lock-free ring is the riskiest change in this area (synchronization, buffer
wraparound, teardown) and needs on-wheel validation, while the pipe is simple
and proven. If we revisit it, the helper process stays (it exists for
COM-activation reliability) and the pipe stays as a fallback. Worth doing only
if later profiling shows the transport is still a meaningful part of the delay.

### LED support

*Status: partial, and a bigger lift to generalize.*

Your wheel's rev lights and other LEDs driven by the game, working
at the same time as force feedback.

Today rev lights work in one place: the Marvin's AIRA (MAIRA) iRacing
integration, tested on a G PRO. Making LEDs work broadly is the bigger lift.
Writing LEDs and force feedback to the wheel at the same time contends at the
firmware, which can cut the force feedback out. We have a clear picture of why,
but a clean general solution is still ahead of us. The filter driver (below) is
the most promising path to solving the contention properly.

### Filter driver

*Status: exploring. Long-term.*

A Windows filter driver that sits below the game and beside the wheel, never
inside the game. Two things it could unlock:

- Solve the LED-versus-force-feedback contention cleanly, so rev lights and
  other LEDs run without stealing force feedback.
- Broaden coverage for wheels and scenarios that the user-mode approach cannot
  reach reliably.

Driver work is careful, slow, and needs hardware validation, so it stays in the
exploring stage until the path is proven.

---

## Principles that shape the list

- Free base, forever. Support is patronage for the infrastructure and time, not
  a paywall on features.
- Describe effects by the feel, not the telemetry plumbing.
- Below the game (filter drivers, virtual devices) or beside it
  (cooperative integrations), never inside it. No injection, no anti-cheat risk.
- Honest status over hype. Partial is called partial.

---

*This roadmap is a snapshot, not a commitment to timing or order. Feedback from
Patreon and Discord directly shapes what moves up.*
