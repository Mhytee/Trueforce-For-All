<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# Effects feel weak

The dial on the wheel stops doing anything while the plugin is driving Trueforce. Once we take that stream over, the wheel's own intensity scaling is out of the loop, so turning it up changes nothing and turning it down changes nothing either.

All of the intensity is set in the plugin instead, and it can be bound to any control so you never open the panel for it: see [what you can bind](bindings.md).

In the panel itself:

- **Master gain** first, since it lifts everything at once.
- Then the **gain on the individual effects** you want more of.

The G923 is gear driven and quieter by nature than the G PRO and the RS50, so it usually wants more gain than they do.

If it still feels flat with Master gain high, the problem may not be gain at all: see [Force feedback: limp, weak, or silent](ffb-not-working.md) for the case where the game's own force is missing rather than quiet.
