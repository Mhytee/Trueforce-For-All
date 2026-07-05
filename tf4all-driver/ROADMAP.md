# TF4ALL FFB Router Driver: Roadmap

**Why it exists:** today the plugin sniffs the game's force feedback with a passive
USBPcap tap and re-applies it through the wheel's Trueforce endpoint. What it can't do is stop the
game's own signals from reaching the wheel. The wheel ignores the game's native force feedback while our stream is
running, but those signals still arrive and cause trouble: LED writes contend with
them and cut the force out, and in a native-Trueforce game the game's own Trueforce
reaches the wheel and clashes with what we send, with no way for us to divert it.
This roadmap replaces the passive tap with the FFB Router Driver, an active interceptor and router: it diverts
those signals before they reach the wheel and hands them to the plugin, which
applies them, so we become the only thing writing to the wheel. That unlocks a lot:
rev lights without the force cutting out, and running in games that already have
Trueforce, replacing the game's Trueforce or blending our effects on top.

## At a glance

- **Stage 1** · Prove the core loop on real hardware · *in progress*
- **Stage 2** · Sign it (in parallel; critical path) · *in progress*
- **Stage 3** · LED support · *built; needs signing to test in Forza*
- **Stage 4** · Games that already have Trueforce · *later*
- **Stage 5** · Generic force-feedback router · *stretch*
- **Stage 6** · General-purpose USB router · *furthest horizon*

## Stages

### Stage 1: Prove the core loop on real hardware
*Status: in progress.*

FFB capture and routing confirmed via Assetto Corsa on @andrewboersma's Xbox
edition G923. Further confirmation is needed on other wheelbases and games.

### Stage 2: Sign it (runs in parallel; on the critical path)
*Status: in progress.*

Get the driver Microsoft-signed so it loads on a normal machine with Secure Boot
on and test mode off. This is more than a ship requirement: the main LED-support
target, the Forza series, runs kernel anti-cheat that won't launch in test mode,
so we can't test the original goal in the game that needs it most until the
driver is signed, and users can't install it either.

The code-signing certificate is paid for, and signing is still in progress.

### Stage 3: LED support
*Status: built; needs signing to test in Forza.*

Drive the wheel's rev lights alongside force feedback, cleanly, with no contention
cutting the force out. This is the original goal, and it works: rev lights drive
over HID++ on the G923, and as the sole writer we can run LEDs and force together
on the wheel's one control pipe. The step that remains is proving it in Forza, whose anti-cheat won't run in test
mode, so it can't be tested there until the driver is signed (Stage 2).

### Stage 4: Games that already have Trueforce
*Status: later.*

Extend the same capture-and-route trick to the game's own Trueforce signal, not
just its force feedback: drop the game's native Trueforce, route it to us, and
layer our effects on top of it (or replace it). Today we switch off in those
titles to avoid fighting the game; this lets us work in them instead. And because
we write the signal ourselves, we can drive the wheel above the game's native
update rate, which opens the door to a richer, telemetry-driven signal (plugin
work, on the plugin roadmap).

### Stage 5: Generic force-feedback router (other wheel brands)
*Status: stretch. The far horizon.*

Make the router work beyond Logitech. Most wheels take their force feedback over
standard DirectInput, not anything Logitech-specific, so the router could do there
what it does on a Logitech wheel: divert the FFB, hand it to the plugin to
modulate, and re-apply it, carrying our FFB enhancements (like the telemetry-driven
detail) to Fanatec, Moza, and the rest. And the consumer needn't be our plugin: the
router could feed any program that wants to read or reshape the FFB, which makes it
a general force-feedback router.

Our Trueforce-style effects are the exception: they stay Logitech-only until an
equivalent high-frequency haptic channel (Fanatec's FullForce, for example) is
reverse-engineered or opened by the wheel manufacturer.

### Stage 6: General-purpose USB router
*Status: stretch. The furthest horizon.*

The furthest reach: generalize the router beyond force feedback to any USB signal.
It could take everything a USB device sends, or filter down to specific signals,
and route them to any target, or duplicate them to several at once, whether a
file, shared memory, another USB device, or a specific endpoint. A GUI would let users pick which signals to forward, from
presets or a hand-built filter for advanced users, and choose where they go. An API
would let other programs set up their own filters and targets on the fly. At that
point it is no longer a Trueforce tool at all, but a general USB routing layer that
anything on the machine can use. In effect, an active, general-purpose evolution
of USBPcap, the passive capture tap this project started on: one that can rewrite
and re-route USB traffic, not just observe it.

## Guardrails

- We sit between the game and the wheel, never modifying the game itself. No
  injection, no anti-cheat risk by design.
- Transparent when idle: no owner means every write reaches the wheel exactly as
  before.
