// Initial D 8 leaderboard slash commands.
//
// A MODULE, not an endpoint. A Discord application has exactly one Interactions Endpoint URL,
// and this application's is report-action, because that is where the moderation card buttons
// arrive. Registering /id8 on the same application therefore delivers command interactions to
// that same URL, so the handler has to live behind it. index.ts verifies the Ed25519 signature
// once, for everything, and routes here for the two interaction types it does not handle.
//
// The moderation code is not touched by any of this: the two paths are disjoint by interaction
// type, MESSAGE_COMPONENT and MODAL_SUBMIT there, APPLICATION_COMMAND and AUTOCOMPLETE here.
//
// /id8 board <course> <direction> [car]   public     Any Car top ten AND every car with a time
// /id8 me                                 ephemeral  your own record, needs a linked Discord
// /id8 ranking                            public     the overall ranking
// /id8 digest                             ephemeral, moderators, this week's message unposted
//
// THE THREE SECOND RULE. Discord discards an interaction not answered in three seconds, so every
// handler does the fewest round trips it can and runs them with Promise.all. If cold starts ever
// push this over, the fix is a deferred response (type 5) plus a webhook PATCH, not more
// parallelism.

// The digest embed, and the course/car/time helpers, shared with arcade-digest so a preview and
// the posted message cannot drift apart. See _shared/id8.ts.
import { buildEmbed, COURSES, courseName, lap, esc } from "../_shared/id8.ts";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const MOD_ROLE_ID  = Deno.env.get("DISCORD_MOD_ROLE_ID") || "";

const GAME = "ID8";
// Interaction types, then response types. Same numbers report-action uses.
const PING = 1, APPLICATION_COMMAND = 2, AUTOCOMPLETE = 4;
const PONG = 1, CHANNEL_MESSAGE = 4, AUTOCOMPLETE_RESULT = 8;
const EPHEMERAL = 64;

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}

async function callRpc(fn: string, body: unknown): Promise<any | null> {
  try {
    const r = await fetch(`${SUPABASE_URL}/rest/v1/rpc/${fn}`, {
      method: "POST",
      headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`,
                 "Content-Type": "application/json" },
      body: JSON.stringify(body ?? {}),
    });
    if (!r.ok) {
      console.error(`[arcade] ${fn} ${r.status}: ${(await r.text()).slice(0, 200)}`);
      return null;
    }
    return await r.json();
  } catch (e) { console.error(`[arcade] ${fn} threw: ${e}`); return null; }
}

async function restRows(path: string): Promise<any[]> {
  try {
    const r = await fetch(`${SUPABASE_URL}/rest/v1/${path}`, {
      headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}` },
    });
    if (!r.ok) {
      console.error(`[arcade] GET ${path.split("?")[0]} ${r.status}: ${(await r.text()).slice(0, 200)}`);
      return [];
    }
    const a = await r.json();
    return Array.isArray(a) ? a : [];
  } catch (e) { console.error(`[arcade] GET ${path.split("?")[0]} threw: ${e}`); return []; }
}

// The car list, from arcade_cars rather than a second hardcoded copy. That table exists precisely
// so the allowlist that bounds submissions and the list that feeds autocomplete cannot disagree.
// Cached in module scope because autocomplete fires on every keystroke and the list changes when a
// migration runs, not when a player drives.
let carCache: { at: number; rows: any[] } | null = null;
const CAR_TTL_MS = 10 * 60 * 1000;

async function cars(): Promise<any[]> {
  if (carCache && Date.now() - carCache.at < CAR_TTL_MS) return carCache.rows;
  const rows = await restRows(
    `arcade_cars?game=eq.${GAME}&select=car_id,code,name&order=car_id.asc`);
  if (rows.length) carCache = { at: Date.now(), rows };
  return rows.length ? rows : (carCache?.rows ?? []);
}

/** The display label inside a monospace table: the model name without the trailing chassis, which
 *  the row does not need and which costs seven characters that mobile does not have. */
function shortCar(name: string): string {
  return name.replace(/\s*\([^)]*\)\s*$/, "").trim();
}

/** Names are capped so a row cannot wrap, because a wrapped row destroys the alignment that is the
 *  only reason these are code blocks at all. 14 is measured, not guessed: of the 107 usernames on
 *  the service the longest is 19 and the average is 9.3, so this leaves about nine people shortened
 *  and holds every row to 46 characters, inside what a phone shows. The marker is ASCII on purpose,
 *  for the same width reason the car names were romanised. */
const NAME_W = 14;
function padName(s: string | null): string {
  const n = String(s ?? "?");
  return (n.length > NAME_W ? n.slice(0, NAME_W - 1) + "+" : n).padEnd(NAME_W);
}

function ephemeralText(content: string) {
  return json({ type: CHANNEL_MESSAGE, data: { flags: EPHEMERAL, content: content.slice(0, 1900) } });
}

function embed(e: any, flags = 0) {
  return json({ type: CHANNEL_MESSAGE, data: { flags, embeds: [e], allowed_mentions: { parse: [] } } });
}

/** Split pre-built lines into code blocks that fit Discord's 1024 character field limit, never
 *  cutting a line in half. Returns one field per chunk, numbered only when there is more than one. */
function codeFields(name: string, lines: string[]): any[] {
  const out: any[] = [];
  let buf: string[] = [];
  let len = 0;
  const FENCE = 8;                       // ```\n ... \n```
  for (const line of lines) {
    if (len + line.length + 1 + FENCE > 1024 && buf.length) {
      out.push(buf); buf = []; len = 0;
    }
    buf.push(line); len += line.length + 1;
  }
  if (buf.length) out.push(buf);
  return out.map((chunk, i) => ({
    name: out.length > 1 ? `${name} (${i + 1}/${out.length})` : name,
    value: "```\n" + chunk.join("\n") + "\n```",
  }));
}

// ---- the commands ----------------------------------------------------------

/** Strict, because these options are free text. Discord shows a picker but does not force the user
 *  to use it: whatever they type is sent as the value. Number() is far too generous for that job.
 *  Number(" ") and Number("") are both 0, so a single space in the course box would have passed
 *  every range check and publicly rendered Lake Akina downhill, answering a question nobody asked.
 *  "0x0e" is 14, "1e1" is 10, "+2" is 2 and " 3 " is 3, all of which reach a real board too. */
function optInt(raw: string | undefined): number | null {
  if (raw === undefined || !/^\d+$/.test(raw)) return null;
  const n = Number(raw);
  return Number.isSafeInteger(n) ? n : null;
}

/** "Only you can see this". Discord sends a BOOLEAN option as true/false, which optionMap
 *  stringifies, so compare against the string. Defaults to public: a leaderboard is a thing people
 *  are supposed to see, and a private-by-default board would quietly stop the commands doing the
 *  one job the digest is also trying to do. */
function privateFlag(opts: Map<string, string>): number {
  return opts.get("private") === "true" ? EPHEMERAL : 0;
}

async function cmdBoard(opts: Map<string, string>) {
  const flags = privateFlag(opts);
  const course = optInt(opts.get("course"));
  const dir = optInt(opts.get("direction"));
  const carRaw = opts.get("car");
  const car = carRaw === undefined || carRaw === "" ? null : optInt(carRaw);

  if (course === null || course < 0 || course > 15 || dir === null || dir < 0 || dir > 1) {
    return ephemeralText("Pick a course and a direction from the list rather than typing them.");
  }
  if (carRaw !== undefined && carRaw !== "" && car === null) {
    return ephemeralText("Pick a car from the list rather than typing it.");
  }

  const carRows = await cars();
  if (!carRows.length) {
    return ephemeralText("I could not read the car list just now. Try again in a moment.");
  }
  const carName = car === null ? null : (carRows.find((c) => c.car_id === car)?.name ?? null);
  if (car !== null && carName === null) return ephemeralText("That car is not in this game.");

  const where = courseName(course, dir);

  // One car: the ranked board for that car, which is a different question from the course board.
  if (car !== null) {
    const rows = await callRpc("get_arcade_leaderboard",
      { p_game: GAME, p_course_id: course, p_direction: dir, p_car_id: car, p_limit: 10 });
    if (rows === null) return ephemeralText("Could not read the leaderboards right now.");
    if (!rows.length) {
      return embed({
        title: `${where} · ${carName}`,
        description: "Nobody has set a time in this car here yet. Someone has to be first.",
        color: 0xE5C04A,
      }, flags);
    }
    const lines = rows.map((r: any) =>
      String(r.rank).padStart(2) + "  " + padName(r.author) + "  " + lap(r.goal_ms).padStart(8));
    return embed({ title: `${where} · ${carName}`, color: 0xE5C04A, fields: codeFields("Times", lines) }, flags);
  }

  // No car: both boards, because they are the two the cabinet itself shows.
  const [anyRows, perRows] = await Promise.all([
    callRpc("get_arcade_leaderboard",
      { p_game: GAME, p_course_id: course, p_direction: dir, p_car_id: null, p_limit: 10 }),
    callRpc("get_arcade_course_cars", { p_game: GAME, p_course_id: course, p_direction: dir }),
  ]);
  if (anyRows === null || perRows === null) {
    return ephemeralText("Could not read the leaderboards right now.");
  }
  const any: any[] = anyRows;
  const per: any[] = perRows.slice().sort((a: any, b: any) => a.best_ms - b.best_ms);

  if (!any.length) {
    return embed({
      title: where,
      description: "No times on this board yet. Drive it with TF4ALL open and it is yours.",
      color: 0xE5C04A,
    }, flags);
  }

  const byId = new Map(carRows.map((c: any) => [c.car_id, c]));
  const anyLines = any.map((r: any) =>
    String(r.rank).padStart(2) + "  " + padName(r.author) + "  " +
    lap(r.goal_ms).padStart(8) + "  " + (byId.get(r.car_id)?.code ?? "?"));

  // SKYLINE GT-R (BNR32) and (BNR34) are the only pair whose short names collide, and two rows
  // labelled identically is worse than two characters of width. Disambiguate just those.
  const shortCounts = new Map<string, number>();
  for (const c of carRows) {
    const k = shortCar(c.name);
    shortCounts.set(k, (shortCounts.get(k) ?? 0) + 1);
  }
  const label = (id: number): string => {
    const full = byId.get(id)?.name;
    if (!full) return "?";
    const short = shortCar(full);
    if ((shortCounts.get(short) ?? 0) < 2) return short;
    const chassis = /(([^)]*))s*$/.exec(full);
    return chassis ? `${short} ${chassis[1]}` : short;
  };
  const carW = Math.max(...per.map((p: any) => label(p.car_id).length));
  const perLines = per.map((p: any) =>
    label(p.car_id).padEnd(carW) + "  " +
    lap(p.best_ms).padStart(8) + "  " + padName(p.best_author).trimEnd());

  return embed({
    title: where,
    color: 0xE5C04A,
    fields: [
      ...codeFields("Any Car", anyLines),
      ...codeFields(`Per car (${per.length} of ${carRows.length} cars)`, perLines),
    ],
  }, flags);
}

async function cmdRanking(opts: Map<string, string>) {
  const flags = privateFlag(opts);
  const rows = await callRpc("get_arcade_overall_ranking", { p_game: GAME, p_limit: 10 });
  if (rows === null) return ephemeralText("Could not read the ranking right now.");
  if (!rows.length) {
    return embed({ title: "Overall ranking", color: 0xE5C04A,
      description: "Nobody has set a time yet. Someone has to be first." }, flags);
  }
  const lines = rows.map((r: any) =>
    `**${r.rank}.** ${esc(r.author)} · ${r.points} pts` +
    (r.course_crowns ? ` (${r.course_crowns} course record${r.course_crowns === 1 ? "" : "s"})` : ""));
  return embed({
    title: "Overall ranking",
    color: 0xE5C04A,
    description: lines.join("\n").slice(0, 4000),
    // Both rules the RPC actually applies, not a simplification of them. 0111 caps car crowns at
    // ten (`least(a.carc, 10) * 5`) and only counts one where somebody else has driven that car on
    // that board (`b.players >= 2`), so an uncontested car is worth nothing.
    footer: { text: "Course record 25, car record 5 for up to 10 cars, a top ten place 11 minus " +
                    "its rank. A car record counts once someone else has driven that car there." },
  }, flags);
}

async function cmdMe(discordId: string) {
  // The only place this function joins a Discord identity to a TF4ALL one, and it does it for the
  // person asking about themselves, into an ephemeral reply nobody else sees.
  const link = (await restRows(`discord_links?discord_id=eq.${encodeURIComponent(discordId)}&select=user_id`))[0];
  if (!link?.user_id) {
    return ephemeralText(
      "I cannot tell which account is yours. Link Discord in the plugin's Account tab and try again.");
  }
  const rows = await callRpc("get_arcade_player_stats", { p_game: GAME, p_user_id: link.user_id });
  if (rows === null) return ephemeralText("Could not read your record right now.");
  const s = rows[0];
  if (!s || !s.boards_entered) {
    return json({ type: CHANNEL_MESSAGE, data: { flags: EPHEMERAL, embeds: [{
      title: "Your Initial D 8 record",
      color: 0xE5C04A,
      description: "No times yet. Finish a Time Attack run with TF4ALL open and you are on the boards.",
    }], allowed_mentions: { parse: [] } }});
  }
  const carRows = await cars();
  const fav = carRows.find((c: any) => c.car_id === s.favourite_car_id);
  const lines = [
    `**${s.points} pts**, ranked **${s.best_rank ?? "-"}** overall`,
    `${s.course_crowns} course record${s.course_crowns === 1 ? "" : "s"} · ` +
      `${s.car_crowns} car record${s.car_crowns === 1 ? "" : "s"} · ` +
      `${s.course_top_ten} top ten place${s.course_top_ten === 1 ? "" : "s"}`,
    `On ${s.boards_entered} of 32 boards, in ${s.cars_driven} car${s.cars_driven === 1 ? "" : "s"}`,
  ];
  if (fav) lines.push(`Most driven: ${fav.name} (${s.favourite_car_runs} run${s.favourite_car_runs === 1 ? "" : "s"})`);
  return json({ type: CHANNEL_MESSAGE, data: { flags: EPHEMERAL, embeds: [{
    title: `${s.author}, in Initial D 8`,
    color: 0xE5C04A,
    description: lines.join("\n"),
  }], allowed_mentions: { parse: [] } }});
}

/** The weekly message, exactly as it would post, shown only to the person who asked.
 *
 *  Renders with the SAME buildEmbed the digest posts with, imported from _shared, so this is the
 *  message rather than a lookalike of it.
 *
 *  It used to fetch arcade-digest?op=preview over HTTP. That failed with a 403, and the reason is
 *  worth keeping: the edge runtime injects SUPABASE_SERVICE_ROLE_KEY as a new-style sb_secret_ key,
 *  not a JWT, so a function-to-function call carries no role claim and a gate that reads claims
 *  rejects its own project. PostgREST accepts that key perfectly well, which is why every other
 *  command worked and only this one did not. Importing the renderer removes the call, the auth
 *  problem, and a second cold start out of Discord's three second budget. */
async function cmdDigest() {
  const week = await callRpc("get_arcade_week", { p_game: GAME });
  if (week === null) return ephemeralText("Could not read this week's numbers right now.");
  return json({ type: CHANNEL_MESSAGE, data: {
    flags: EPHEMERAL,
    content: "This is what the weekly digest would post right now. Nobody else can see this.",
    embeds: [buildEmbed(week)],
    allowed_mentions: { parse: [] },
  }});
}

// ---- autocomplete ----------------------------------------------------------

function choices(list: { name: string; value: string }[]) {
  return json({ type: AUTOCOMPLETE_RESULT, data: { choices: list.slice(0, 25) } });
}

async function autocomplete(name: string, typed: string) {
  const q = typed.trim().toLowerCase();
  if (name === "course") {
    const all = COURSES.map((c, i) => ({ name: c, value: String(i) }));
    return choices(q ? all.filter((c) => c.name.toLowerCase().includes(q)) : all);
  }
  if (name === "car") {
    const rows = await cars();
    // Match the code as well as the name, so "AE86" finds both Truenos and the Levin.
    const all = rows.map((c: any) => ({ name: c.name, value: String(c.car_id), code: String(c.code) }));
    if (q) {
      const hit = all.filter((c) => c.name.toLowerCase().includes(q) || c.code.toLowerCase().includes(q));
      return choices(hit.map((c) => ({ name: c.name.slice(0, 100), value: c.value })));
    }
    // Empty box: 25 of 50 have to be dropped, so drop them fairly. car_id is (maker << 8) | member,
    // so taking them in car_id order shows Toyota, Nissan and Honda and hides Mazda, Subaru,
    // Mitsubishi, Suzuki and every tuned car. Round-robin by maker instead.
    const byMaker = new Map<number, typeof all>();
    for (const c of all) {
      const maker = Number(c.value) >> 8;
      if (!byMaker.has(maker)) byMaker.set(maker, []);
      byMaker.get(maker)!.push(c);
    }
    const spread: typeof all = [];
    for (let i = 0; spread.length < all.length; i++) {
      let placed = false;
      for (const list of byMaker.values()) if (list[i]) { spread.push(list[i]); placed = true; }
      if (!placed) break;
    }
    return choices(spread.map((c) => ({ name: c.name.slice(0, 100), value: c.value })));
  }
  return choices([]);
}

// ---- entry point -----------------------------------------------------------

function optionMap(sub: any): Map<string, string> {
  const m = new Map<string, string>();
  for (const o of (sub?.options ?? [])) m.set(o.name, String(o.value ?? ""));
  return m;
}

function focusedOption(sub: any): { name: string; value: string } | null {
  for (const o of (sub?.options ?? [])) if (o.focused) return { name: o.name, value: String(o.value ?? "") };
  return null;
}

function isMod(member: any): boolean {
  const roles: string[] = Array.isArray(member?.roles) ? member.roles : [];
  return !!MOD_ROLE_ID && roles.includes(MOD_ROLE_ID);
}

/** Handles APPLICATION_COMMAND and APPLICATION_COMMAND_AUTOCOMPLETE. The caller has already
 *  verified the signature and parsed the body, so this never sees an unsigned request. */
export async function handleArcade(body: any): Promise<Response> {
  // One command with subcommands, so the options live one level down.
  const sub = body?.data?.options?.[0];
  const name = String(sub?.name ?? "");

  try {
    if (body?.type === AUTOCOMPLETE) {
      const f = focusedOption(sub);
      return f ? await autocomplete(f.name, f.value) : choices([]);
    }

    switch (name) {
      case "board":   return await cmdBoard(optionMap(sub));
      case "ranking": return await cmdRanking(optionMap(sub));
      case "me": {
        const id = body?.member?.user?.id || body?.user?.id || "";
        return id ? await cmdMe(String(id)) : ephemeralText("I could not read your Discord id.");
      }
      case "digest":
        if (!isMod(body.member)) return ephemeralText("That one is for moderators.");
        return await cmdDigest();
      default:
        return ephemeralText("Unrecognised command.");
    }
  } catch (e) {
    // Never let an exception become a silent timeout: Discord shows "the application did not
    // respond", which tells the user nothing and tells us nothing either.
    console.error(`[arcade] ${name} threw: ${e}`);
    // An autocomplete has to be answered with an autocomplete, not a message: Discord silently
    // drops a mismatched response type and the picker hangs instead of simply coming back empty.
    if (body?.type === AUTOCOMPLETE) return choices([]);
    return ephemeralText("Something went wrong reading the leaderboards. It has been logged.");
  }
}