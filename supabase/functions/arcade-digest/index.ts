// Weekly arcade leaderboard digest for Discord.
//
// One message a week, not one per event. Per-event announcing would need an events claim, a posted
// message id, a leader-change state machine, a per-victim cooldown and a five-minute cron, all to
// solve problems (double posting, flapping, ping spam, three events fired by one lap) that a weekly
// message simply does not have. It would also post nothing at all right now: of the events on the
// table, none has a victim, because a board with one player has nobody to take a crown from.
//
// NO MENTIONS, deliberately, enforced with allowed_mentions {parse: []} rather than trusted to the
// copy. Two reasons, and both are about the person who LOST a record rather than the one who took
// it. A mention turns "somebody beat your time" into a notification, which is the difference
// between a scoreboard and a nudge. And resolving a TF4ALL username to a Discord account in a
// public channel joins two identities that nobody agreed to join: people link Discord for roles,
// not to be named in a leaderboard post. So this never reads discord_links at all.
//
// Names are already scrubbed at the source: get_arcade_week drops events whose actor account is
// gone, and a deleted account's name is nulled by the trigger in 0113. A null victim renders as
// "(anonymous)" rather than being hidden, because the steal still happened.
//
// Follows the report-notify shape: service_role gate in-function, a service-key RPC helper, a
// DRY-RUN when the secrets are missing, and a Discord post that returns 502 rather than throwing.

// The renderer is shared with report-action's /id8 digest, so a moderator's preview and the
// posted message are the same code rather than two copies of it. See _shared/id8.ts.
import { buildEmbed } from "../_shared/id8.ts";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const BOT_TOKEN    = Deno.env.get("DISCORD_BOT_TOKEN") || "";
const CHANNEL_ID   = Deno.env.get("DISCORD_ARCADE_CHANNEL_ID") || "";

const GAME = "ID8";

// ---- slash command registration --------------------------------------------
//
// A maintenance operation rather than part of the weekly post, but it lives here because this is
// the only arcade function holding the bot token behind a service_role gate, and because the
// definitions have to be re-PUT every time they change. ?op=register is idempotent: the PUT
// replaces the whole guild command set, so running it twice leaves exactly one copy.
//
// Both ids are public. The application id IS the bot's user id, which anyone in the server can
// read, and the guild id likewise; neither is a credential, so neither is a secret.
const APP_ID   = "1514823225656213574";
const GUILD_ID = "1513932714913566812";

// GUILD scoped, not global. Guild commands appear the instant they are registered, where global
// ones take up to an hour to propagate, and this is a community server's command rather than
// something other servers should see.
//
// Option types: 1 SUB_COMMAND, 3 STRING, 5 BOOLEAN. course and car MUST be STRING with
// autocomplete, because the handler emits string values; registering them as INTEGER leaves the
// picker permanently empty while the command still works if you type a raw id, which is the most
// confusing possible failure. direction is static choices, never autocomplete, because the handler
// only autocompletes course and car. Required options must precede optional ones or the PUT 400s.
const COMMANDS = [{
  name: "id8",
  description: "Initial D: Arcade Stage 8 Infinity leaderboards",
  options: [
    {
      type: 1, name: "board", description: "Show a course leaderboard",
      options: [
        { type: 3, name: "course", description: "Which course", required: true, autocomplete: true },
        { type: 3, name: "direction", description: "Which way", required: true,
          choices: [{ name: "Downhill", value: "0" }, { name: "Hill climb", value: "1" }] },
        { type: 3, name: "car", description: "Show one car's board instead", required: false, autocomplete: true },
        { type: 5, name: "private", description: "Only you see the reply", required: false },
      ],
    },
    { type: 1, name: "me", description: "Your own record (only you see this)" },
    {
      type: 1, name: "ranking", description: "The overall ranking",
      options: [{ type: 5, name: "private", description: "Only you see the reply", required: false }],
    },
    { type: 1, name: "digest", description: "Preview this week's digest (moderators, only you see it)" },
  ],
}];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status, headers: { "Content-Type": "application/json" },
  });
}

function jwtClaims(token: string): any {
  try {
    const p = token.split(".")[1];
    return JSON.parse(atob(p.replace(/-/g, "+").replace(/_/g, "/")));
  } catch { return null; }
}

async function callRpc(fn: string, args: unknown): Promise<any> {
  try {
    const r = await fetch(`${SUPABASE_URL}/rest/v1/rpc/${fn}`, {
      method: "POST",
      headers: {
        apikey: SERVICE_KEY,
        Authorization: `Bearer ${SERVICE_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(args ?? {}),
    });
    if (!r.ok) {
      console.error(`[arcade-digest] ${fn} failed ${r.status}: ${(await r.text()).slice(0, 300)}`);
      return null;
    }
    return await r.json();
  } catch (e) {
    console.error(`[arcade-digest] ${fn} threw: ${e}`);
    return null;
  }
}

Deno.serve(async (req) => {
  const url = new URL(req.url);
  // Defaults to PREVIEW, so posting to a public channel has to be asked for. The other way round,
  // a forgotten query parameter is a message in front of the whole server, and "I meant to preview
  // it" is not a thing you can take back once names are in a channel. The cron passes ?op=post.
  const op = url.searchParams.get("op") || "preview";

  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  if (jwtClaims(token)?.role !== "service_role") return json({ error: "forbidden" }, 403);

  // ?op=check: prove the wiring without publishing anything. The only code path that reads the
  // channel id is the posting path, so without this the first proof that the secret is right is a
  // message appearing in front of the whole server, and the first proof that it is WRONG is nothing
  // appearing at all a week later. Returns whether each secret is present, never its value, and
  // asks Discord to describe the channel so a wrong id or an invisible channel shows up here.
  // ?op=register replaces the guild's command set with COMMANDS above. Safe to re-run.
  if (op === "register") {
    if (!BOT_TOKEN) return json({ error: "DISCORD_BOT_TOKEN not set" }, 400);
    try {
      const r = await fetch(
        `https://discord.com/api/v10/applications/${APP_ID}/guilds/${GUILD_ID}/commands`,
        { method: "PUT",
          headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" },
          body: JSON.stringify(COMMANDS) });
      const text = await r.text();
      if (!r.ok) {
        // 403 here almost always means the bot was invited with the "bot" scope but not
        // "applications.commands", which is fixed by re-inviting rather than by a new token.
        console.error(`[arcade-digest] register ${r.status}: ${text.slice(0, 400)}`);
        return json({ ok: false, status: r.status, detail: text.slice(0, 400) }, 502);
      }
      const registered = JSON.parse(text);
      return json({ ok: true, registered: registered.map((c: any) =>
        ({ id: c.id, name: c.name, options: (c.options ?? []).map((o: any) => o.name) })) });
    } catch (e) {
      console.error(`[arcade-digest] register threw: ${e}`);
      return json({ error: "discord unreachable" }, 502);
    }
  }

  if (op === "check") {
    const out: any = {
      ok: true,
      botToken: BOT_TOKEN ? "set" : "MISSING",
      channelId: CHANNEL_ID ? "set" : "MISSING",
    };
    // Does the key this function is HANDED look like the key its own gate demands? A
    // function-to-function call sends SERVICE_KEY as the bearer, so if that is not a JWT carrying
    // role=service_role, the gate rejects its own project's calls. Reports shape only: the prefix
    // distinguishes a legacy JWT ("eyJ") from a new-style secret ("sb_") and is not a credential.
    const skClaims = jwtClaims(SERVICE_KEY);
    out.serviceKey = {
      prefix: SERVICE_KEY.slice(0, 3),
      parsesAsJwt: !!skClaims,
      role: skClaims?.role ?? null,
    };

    if (BOT_TOKEN && CHANNEL_ID) {
      try {
        const r = await fetch(`https://discord.com/api/v10/channels/${CHANNEL_ID}`, {
          headers: { Authorization: `Bot ${BOT_TOKEN}` },
        });
        out.discordStatus = r.status;
        if (r.ok) {
          const c = await r.json();
          out.channel = { id: c.id, name: c.name, guildId: c.guild_id, type: c.type };
          // Honest limit: a 200 proves the token works, the id exists and the bot can SEE the
          // channel. It does not prove Send Messages, which cannot be tested without sending.
          out.note = "bot can see this channel; Send Messages is not provable without posting";
        } else {
          out.detail = (await r.text()).slice(0, 200);
        }
      } catch (e) {
        out.discordStatus = "unreachable";
        out.detail = String(e).slice(0, 200);
      }
    }
    return json(out);
  }

  const week = await callRpc("get_arcade_week", { p_game: GAME });
  if (!week) return json({ error: "could not read the week" }, 500);

  const embed = buildEmbed(week);

  // Preview: render the message and return it without posting or consuming anything, so the copy
  // can be approved before it is ever seen in a channel.
  if (op === "preview") return json({ ok: true, preview: true, week, embed });

  const nothingHappened = (week.times_set ?? 0) === 0 && (week.steals?.length ?? 0) === 0;
  if (nothingHappened) {
    // A weekly "nobody played" post is how a channel teaches people to ignore it.
    return json({ ok: true, posted: false, reason: "a quiet week is not worth a message" });
  }

  if (!BOT_TOKEN || !CHANNEL_ID) {
    console.log(`[arcade-digest] DRY-RUN: ${JSON.stringify(embed).slice(0, 500)}`);
    return json({ ok: true, dryRun: true, reason: "DISCORD_BOT_TOKEN / DISCORD_ARCADE_CHANNEL_ID not set", embed });
  }

  let resp: Response;
  try {
    resp = await fetch(`https://discord.com/api/v10/channels/${CHANNEL_ID}/messages`, {
      method: "POST",
      headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" },
      // parse: [] disables every mention, including @everyone, whatever ends up in a username.
      body: JSON.stringify({ embeds: [embed], allowed_mentions: { parse: [] } }),
    });
  } catch (e) {
    console.error(`[arcade-digest] Discord post threw: ${e}`);
    return json({ error: "discord unreachable" }, 502);
  }

  if (!resp.ok) {
    console.error(`[arcade-digest] Discord post failed ${resp.status}: ${(await resp.text()).slice(0, 300)}`);
    return json({ error: "discord post failed", status: resp.status }, 502);
  }

  // Only now. Marking before the post would lose a week's events to a failed send, and there is no
  // way to get them back: consumed_at is the only record that they were told.
  const consumed = await callRpc("mark_arcade_events_consumed", {
    p_game: GAME, p_until: new Date().toISOString(),
  });

  return json({ ok: true, posted: true, consumed, steals: week.steals?.length ?? 0 });
});
