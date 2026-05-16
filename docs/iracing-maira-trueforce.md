# Restore Trueforce in iRacing when using MAIRA

**Bring Logitech Trueforce haptics back to iRacing on a G PRO or RS50 while running Marvin's Awesome iRacing App (MAIRA).**

## The problem

iRacing ships native Logitech Trueforce. But to run MAIRA (Marvin's
Awesome iRacing App) on a Logitech Trueforce wheel, MAIRA's own
documentation tells you to turn iRacing's Trueforce off: you edit
`app.ini` in your iRacing documents folder and change
`loadTrueForceAPI=1` to `loadTrueForceAPI=0`. Without that change MAIRA
will not take over the wheel's force feedback.

The trade-off: you get MAIRA's reworked force feedback, but iRacing's
Trueforce textural layer (the surface, kerb, lockup, and engine detail
that plays through the wheel's Trueforce haptic motor) is now completely
off. There is no way to keep both with MAIRA alone, because the same
`loadTrueForceAPI=0` switch that lets MAIRA run is the one that kills
Trueforce.

## What this plugin does about it

Trueforce For All drives the wheel's Logitech Trueforce haptic stream
directly, independent of iRacing's Trueforce API. It synthesizes the
textural layer from live telemetry and game audio instead of relying on
the in-game Trueforce SDK that MAIRA forces you to disable.

That makes the two complementary:

- **MAIRA** owns the main force feedback (cornering load, weight
  transfer, the steering forces it was built to rework).
- **Trueforce For All** adds the textural haptic layer back on the
  wheel's separate Trueforce stream: engine pulse, kerbs and road bumps,
  ABS, traction loss, gear shifts, collisions, plus an audio-derived
  channel.

They run at the same time on the same wheel without fighting, because
they use different paths to it. This has been confirmed on hardware:
loading MAIRA and iRacing with the plugin enabled, the reworked force
feedback and the Trueforce haptic layer both came through together with
no extra steps beyond the setup below.

## Setup

You need the plugin installed and a supported wheel (Logitech G PRO or
RS50). See the [main README](../README.md) for install steps and
requirements.

1. **Configure MAIRA as normal.** Follow MAIRA's own setup, including
   setting `loadTrueForceAPI=0` in iRacing's `app.ini`. MAIRA's docs
   also recommend `resetWhenFFBLost=0` so iRacing does not snatch force
   feedback back between sessions. Confirm MAIRA's force feedback works
   on its own first.
2. **Close Logitech G HUB.** Both MAIRA and this plugin need G HUB
   closed: it claims the wheel's HID interface. Keep it closed for the
   whole session, not just at launch.
3. **Launch SimHub and start an iRacing session.**
4. **Enable the plugin for iRacing.** In the plugin's SimHub settings
   page, turn its master enable on. The choice is saved per game, so
   iRacing stays enabled from then on.
5. **Tune to taste.** The on-wheel Trueforce intensity dial does not
   apply while the plugin is driving the Trueforce stream. Use the
   in-plugin Master Gain and per-effect gains instead.

## Notes

- This is the one supported way to use this plugin with iRacing. Stock
  iRacing already has native Trueforce and the plugin deliberately stays
  out of its way; the MAIRA setup above is the exception, because MAIRA
  has already turned native Trueforce off.
- The FFB spike softener iRacing ships does not run when MAIRA owns the
  force feedback, but spike behaviour is then MAIRA's domain, not this
  plugin's.
- If you stop using MAIRA and set `loadTrueForceAPI=1` again, disable
  this plugin for iRacing so it does not layer on top of iRacing's
  native Trueforce.

## See also

- [Trueforce For All README](../README.md)
- [MAIRA force feedback docs](https://herboldracing.com/marvins-awesome-iracing-app-maira/force-feedback/)
- [MAIRA troubleshooting (Logitech / Trueforce)](https://herboldracing.com/marvins-awesome-iracing-app-maira/troubleshooting/)
