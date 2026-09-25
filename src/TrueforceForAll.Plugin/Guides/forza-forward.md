## Keeping SimHub working as well

Because Forza only sends to one place, pointing it at the plugin means the telemetry stops reaching SimHub. Dashboards, ShakeIt bass shakers, anything SimHub drives from Forza telemetry goes quiet.

Forwarding is what gives that back. The plugin relays every packet it receives straight on to SimHub, so you end up with the direct feed **and** everything SimHub was doing before.

One thing is not a straight copy. Forza blanks its packets during replays, rewinds and menu blips, and SimHub reads that as the game disconnecting: dashboards reset and ShakeIt rebuilds itself mid-session. So for gaps shorter than fifteen seconds the plugin holds the last known car and engine details in place on the forwarded copy, and SimHub carries on as though nothing happened.

1. In SimHub, click **Home** at the top of the left sidebar. It should show Forza as the active game; if not, click **Change game**. Open **Game config** and note the UDP port it shows, often `8000`. Read it only, change nothing.
2. {{guide:Tick **Also forward to SimHub** in the plugin's UDP telemetry settings|panel:Tick the box above}}, set the forward host to `127.0.0.1`, and set the forward port to that number.
3. Drive for a moment and watch the **Forwarded** line{{panel: below}}. Once it counts packets, SimHub's dashboards and shakers are back.

Leave forwarding off if you do not use SimHub for anything in Forza.
