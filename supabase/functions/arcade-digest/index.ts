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

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const BOT_TOKEN    = Deno.env.get("DISCORD_BOT_TOKEN") || "";
const CHANNEL_ID   = Deno.env.get("DISCORD_ARCADE_CHANNEL_ID") || "";

const GAME = "ID8";

// The only public page the project maintains. It sits in the call to action rather than a footer
// because Discord renders footer text as plain text: a link there would not be one.
const INFO_URL = "https://github.com/Mhytee/Trueforce-For-All";

// From the exe's own course name table at 0x0121c9e0, so the digest calls a course what the game
// calls it. Direction 0 is downhill, confirmed on the cabinet.
const COURSES = [
  "Lake Akina", "Myogi", "Akagi", "Akina", "Irohazaka", "Tsukuba", "Happogahara", "Nagao",
  "Tsubaki Line", "Usui", "Sadamine", "Tsuchisaka", "Akina Snow", "Hakone", "Momiji Line",
  "Nanamagari",
];

// CarID -> the car name, generated from Id8CarTable.cs. CarID is (maker << 8) | member, which is
// why the ids jump.
//
// Romanised at generation time, not in the C# table: Id8CarNameMatch matches TeknoParrot rows
// against Id8Car.Name, so editing the table risks the car matching. Roman numerals move to ASCII
// because U+2160 is ambiguous-width and would misalign a monospace board, and the three kanji
// names go to Latin because most of the server cannot read them (幻気 GENKI, 改 Kai, ∞ Infini).
const CARS: Record<number, string> = {
  0: "TRUENO GT-APEX (AE86)", 1: "LEVIN GT-APEX (AE86)",
  2: "LEVIN SR (AE85)", 3: "MR2 G-Limited (SW20)",
  4: "ALTEZZA RS200 (SXE10)", 5: "MR-S (ZZW30)",
  6: "SUPRA RZ (JZA80)", 7: "86 GT (ZN6)",
  8: "PRIUS (ZVW30)", 9: "TRUENO 2door GT-APEX (AE86)",
  10: "CELICA GT-FOUR (ST205)", 256: "SKYLINE GT-R (BNR32)",
  257: "SKYLINE GT-R (BNR34)", 258: "SILVIA K's (S13)",
  259: "Silvia Q's (S14)", 260: "Silvia spec-R (S15)",
  261: "180SX TYPE II (RPS13)", 262: "FAIRLADY Z (Z33)",
  263: "GT-R NISMO (R35)", 264: "SKYLINE 25GT TURBO (ER34)",
  512: "Civic SiR·II (EG6)", 513: "CIVIC TYPE R (EK9)",
  514: "INTEGRA TYPE R (DC2)", 515: "S2000 (AP1)",
  516: "NSX (NA1)", 768: "RX-7 Infini III (FC3S)",
  769: "RX-7 Type R (FD3S)", 770: "RX-8 Type S (SE3P)",
  771: "ROADSTER (NA6CE)", 772: "ROADSTER RS (NB8C)",
  773: "RX-7 Type RS (FD3S)", 1024: "IMPREZA STi Ver.V (GC8)",
  1025: "IMPREZA STI (GDBF)", 1026: "IMPREZA STi (GDBA)",
  1027: "BRZ S (ZC6)", 1280: "LANCER Evolution III (CE9A)",
  1281: "LANCER EVOLUTION IV (CN9A)", 1282: "LANCER Evolution IX (CT9A)",
  1283: "LANCER EVOLUTION VII (CT9A)", 1284: "LANCER EVOLUTION X (CZ4A)",
  1285: "LANCER EVOLUTION V (CP9A)", 1286: "LANCER EVOLUTION VI (CP9A)",
  1536: "Cappuccino (EA11R)", 1792: "SILEIGHTY",
  2048: "GENKI-7 (FD3S)", 2049: "MONSTER CIVIC R (EK9)",
  2050: "S2000 GT1 (AP1)", 2051: "G-FORCE SUPRA (JZA80 Kai)",
  2052: "ROADSTER C-SPEC (NA8C Kai)", 2053: "NSX-R GT (NA2)",
};

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

function courseName(id: number, dir: number): string {
  const name = COURSES[id] ?? `course ${id}`;
  return `${name} ${dir === 0 ? "downhill" : "hill climb"}`;
}

/** What was actually taken. A car record and the any car record are different prizes, and reading
 *  the same sentence for both loses the distinction that makes one of them worth chasing: beating
 *  the AE86 time on Akina is not beating Akina.
 *
 *  "Any Car" rather than "overall" because that is what the cabinet calls the board. A reader who
 *  goes looking for the record they just lost should find it under the name they read here.
 *
 *  The record is bold and the course is not, deliberately. A reader scans these lines for their own
 *  name and for which record fell; the course is context. Bolding all three put four bold runs on
 *  one line, which cancels out, and it stops the Any Car and single-car entries lining up so you
 *  can tell them apart at a glance. */
function recordName(s: any): string {
  const where = courseName(s.course_id, s.direction);
  if (s.scope === "car") {
    const car = CARS[s.car_id];
    return car ? `the **${car}** record on ${where}` : `a car record on ${where}`;
  }
  return `the **Any Car** record on ${where}`;
}

/** 202918 -> "3:22.918". The game's own format, so a time reads the same here as on the cabinet. */
function lap(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return "-";
  const m = Math.floor(ms / 60000);
  const s = Math.floor((ms % 60000) / 1000);
  return `${m}:${String(s).padStart(2, "0")}.${String(ms % 1000).padStart(3, "0")}`;
}

/** A margin of victory. Under a minute reads as seconds, because "12.342" on its own could be read
 *  as hundredths; over a minute it has to fall back to the lap format, since "83.500s" is nonsense
 *  to anyone who thinks in lap times. */
function gap(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return "";
  return ms < 60000 ? `${(ms / 1000).toFixed(3)}s` : lap(ms);
}

/** Discord renders _ * ` ~ | as formatting. Usernames are [a-zA-Z0-9_]{3,32}, so an underscore in
 *  somebody's name would italicise half the line if it went through raw.
 *
 *  Truncate BEFORE escaping, never after: cutting an escaped string can leave a trailing backslash
 *  that then escapes whatever follows it in the line. */
function esc(name: string | null): string {
  if (!name) return "(anonymous)";
  return name.slice(0, 32).replace(/([_*`~|\\])/g, "\\$1");
}

function buildEmbed(week: any): any {
  const steals: any[] = Array.isArray(week?.steals) ? week.steals : [];
  const top: any[] = Array.isArray(week?.top) ? week.top : [];
  const fields: any[] = [];

  if (steals.length) {
    // Budget-aware, not a fixed count. Naming the car pushed a line to roughly 110 characters, and
    // ten of those overflow Discord's 1024 character field limit, so a flat slice(0, 1000) would
    // cut somebody's record in half mid-sentence in a public channel. Fit whole lines only, keep
    // room for the tally, and say how many did not fit: a reader who cannot see the rest should
    // know there is a rest.
    const BUDGET = 960;
    const lines: string[] = [];
    let used = 0;
    for (const s of steals) {
      // One sentence, not a name line with an orphaned time under it. The time IS the news, so
      // it belongs in the sentence rather than floating unlabelled below it.
      const by = s.previous_ms && s.goal_ms ? `, ${gap(s.previous_ms - s.goal_ms)} faster` : "";
      const line = `**${esc(s.actor)}** took ${recordName(s)} from ` +
                   `${esc(s.victim)} with **${lap(s.goal_ms)}**${by}`;
      if (used + line.length + 1 > BUDGET) break;
      lines.push(line);
      used += line.length + 1;
    }
    if (steals.length > lines.length) lines.push(`…and ${steals.length - lines.length} more`);
    fields.push({ name: "Records changed hands", value: lines.join("\n") });
  }

  if (top.length) {
    fields.push({
      name: "Overall ranking",
      value: top.map((t) =>
        `**${t.rank}.** ${esc(t.author)} · ${t.points} pts` +
        (t.course_crowns ? ` (${t.course_crowns} course record${t.course_crowns === 1 ? "" : "s"})` : "")
      ).join("\n").slice(0, 1000),
    });
  }

  // The call to action, now purely the invitation. The leader line that used to sit here restated
  // the ranking field directly above it, which is the same redundancy the summary had.
  //
  // The empty-state line only appears when there is no ranking to show, so a cold-start week still
  // says something rather than opening with instructions to nobody.
  //
  // No blank-board count. Every framing of it was a way of saying how empty the boards are, and a
  // reader who has just seen the records and the ranking does not need the gap measured for them.
  fields.push({
    name: "Set a time",
    value: [
      top.length ? "" : "Nobody has set a time here yet. Someone has to be first.",
      "Finish a Time Attack run with TF4ALL open and your time lands on the in-game leaderboards.",
      `[How it works](${INFO_URL})`,
    ].filter(Boolean).join("\n"),
  });

  const times = week?.times_set ?? 0;
  const drivers = week?.drivers ?? 0;

  return {
    title: "This week in Initial D: Arcade Stage 8 Infinity",
    // Only when there is something to count. "0 times set by 0 drivers" is reachable, because an
    // unconsumed steal from an earlier week can carry a post on its own, and it reads like a bot
    // that has broken rather than a quiet week.
    description: times > 0
      ? `${times} time${times === 1 ? "" : "s"} set by ${drivers} driver${drivers === 1 ? "" : "s"}.`
      : undefined,
    color: 0xE5C04A,
    fields,
  };
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
