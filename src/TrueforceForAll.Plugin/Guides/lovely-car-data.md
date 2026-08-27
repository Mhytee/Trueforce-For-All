Some cars carry their own rev-light data: the RPM each gear really lights up at, how fast the redline warning blinks, and the colors and fill direction of the real car's strip. Trueforce For All reads that data whenever community features are on, since that is where it is fetched from.

**The redlines and the blink rate work on any wheel.** They set where the shift cue arrives and how the redline warning pulses, so they are worth having even on a wheel whose lights are a fixed look.

**Matching your wheel to the car** is the other half, for wheels that can show a pattern of their own. Tick **Match my wheel to the car I'm driving** on the LIGHTSYNC tab and your wheel takes on the car's own colors and fill direction as you get in it, lighting at the points that car really lights at. Real cars come up unevenly, so the last few LEDs often arrive in a rush as you approach the shift. That is the car, not a bug.

It works even in games that light the wheel themselves, because the colors are set once as the car loads and the game drives the lights from there. A car's data is fetched the first time you drive it and kept on this PC, then re-checked about once a week so corrections reach you.

## Where the data comes from

Per-car data for covered cars comes from [Lovely Sim Racing](https://github.com/Lovely-Sim-Racing/lovely-car-data), shared under the CC BY-NC-SA 4.0 licence.

## What that covers, and what it does not

The Lovely dataset supplies the **light patterns**, the **per-gear redlines** behind them, and each car's **redline blink rate**. Car names, engine details and the community redline consensus come from Trueforce For All's own car facts, which cover cars the pattern dataset does not, across every game the plugin supports.

The distinction matters both ways. Crediting them for all of it would overstate what they supply, and crediting them for none of it would understate it.

A car with no entry in the dataset keeps whatever pattern you have chosen and whatever redline the game reports, so nothing is lost by leaving this on.
