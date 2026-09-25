<!-- Generated from src/TrueforceForAll.Plugin/Guides by scripts/gen_public_guides.py. Edit the source, not this file. -->

# Is this anti-cheat safe?

Yes. For every sim TF4ALL supports, the plugin runs entirely outside the game. It **never** injects code, reads or writes game memory, or hooks the game in any way.

What it does instead:

- Talks to your wheel over USB.
- Reads telemetry the game already broadcasts, through SimHub, shared memory or UDP.
- Captures game audio through Windows' own loopback.

Switching a game's native Trueforce off, if you choose to, is done by editing a config file or flipping a setting before launch. Never by touching the running game.

The Assetto Corsa bridge is a Custom Shaders Patch script, loaded by CSP the same way its own FFB tweaks are. The plugin never touches the game process, and Assetto Corsa has no anti-cheat.

The Farming Simulator telemetry mod works the same way: a normal mod you tick in the game's own mod list, sending physics the game does not otherwise publish. Neither one is injected by the plugin: the game loads the mod from its mod list, CSP loads the script, and nothing reaches into a running process.
