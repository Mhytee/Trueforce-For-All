Yes. The plugin runs entirely outside the game. It **never** injects code, reads or writes game memory, or hooks the game in any way.

What it does instead:

- Talks to your wheel over USB.
- Reads telemetry the game already broadcasts, through SimHub, shared memory or UDP.
- Captures game audio through Windows' own loopback.

Switching a game's native Trueforce off, if you choose to, is done by editing a config file or flipping a setting before launch. Never by touching the running game.

The Assetto Corsa bridge is a Custom Shaders Patch script, loaded by CSP the same way its own FFB tweaks are. The plugin never touches the game process, and Assetto Corsa has no anti-cheat.
