On these wheels the light bar and the force feedback **share one channel**. While a game is driving the force itself, writing light levels makes that force cut out, so the plugin leaves the levels alone rather than costing you the feel of the car.

That leaves one condition, and the plugin checks it for you every session: the bar is ours to drive whenever the game's own force feedback is quiet. In practice that means the games where [the plugin produces the force itself](guide:telemetry-ffb), and [iRacing](guide:iracing-setup) once it hands its force over. Elsewhere the wheel keeps whatever lighting it came with.

> [!NOTE]
> [Colors and per-car patterns](guide:light-patterns) are a different matter. Setting a pattern is a single write rather than a continuous stream, so the plugin can place one in games where it will not drive the bar at all. That is why your car's colors can still turn up in a game whose rev lights stay dark. It needs a wheel whose strip is programmable: the G PRO and the RS50 have one, the G923's strip has a fixed look.

A driver that would free the bar in every game is in testing. It needs to be signed by Microsoft before it can ship.
