// A daily copy of TeknoParrot's public leaderboard, so the server can answer "where do I stand
// against the real cabinets".
//
// The plugin has parsed this page for a while, but only into memory on one machine, so nothing
// server side has ever been able to rank anybody against it. /id8 me wants two numbers, a standing
// in the whole field and a standing among tf4all users, and the first needs their board here.
//
// THE PARSER IS A PORT, and that is the risk in this file. TeknoParrotLeaderboard.cs is the proven
// one; this is a second copy of the same regexes and the same course and time rules, and two copies
// drift. It is deliberately the SMALLEST port that answers the question: no car matching, because a
// course standing does not need a CarID and mapping their free text onto one takes a 154-line
// matcher that would be the most drift-prone thing here by far. The car text is stored raw.
//
// POLITE BY DEFAULT. Once a day, one request, matching the 24 hour cache the plugin already keeps,
// so this does not hit their server harder than we already do. Anything that fails leaves the last
// good copy in place rather than emptying the table.

import "jsr:@supabase/functions-js/edge-runtime.d.ts";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const SOURCE = "teknoparrot";
const GAME = "ID8";
const TP_GAME_ID = "ID8";

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b, null, 2), {
    status: s, headers: { "Content-Type": "application/json" },
  });
}

// ---- the port ---------------------------------------------------------------

const BOARD_RX = /<tbody\s+id="track-records-([^"]+)"\s*>([\s\S]*?)<\/tbody>/gi;
const ROW_RX = /<tr\b[\s\S]*?<\/tr>/gi;
const PLAYER_RX = /highscore-player-link"[^>]*>([^<]*)<\/a>/i;
const CAR_RX = /class="detail-col"\s*>([^<]*)</i;
const TIME_RX = /class="time-col"\s*>([^<]*)</i;
const DATE_RX = /class="date-single-line[^"]*"\s*>([^<]*)</i;
const ENTRY_RX = /entryId=(\d+)/i;

const DIRECTIONS = [
  "Counterclockwise", "Clockwise", "Hill_Climb", "Downhill", "Outbound", "Inbound", "Reverse",
];

/** "Akagi_Downhill" -> course "Akagi", direction "Downhill". */
function splitCourse(key: string): { course: string; direction: string } {
  const k = key ?? "";
  for (const d of DIRECTIONS) {
    const suffix = "_" + d;
    if (k.toLowerCase().endsWith(suffix.toLowerCase())) {
      return {
        course: k.slice(0, k.length - suffix.length).replace(/_/g, " ").trim(),
        direction: d.replace(/_/g, " "),
      };
    }
  }
  return { course: k.replace(/_/g, " ").trim(), direction: "" };
}

function normaliseCourse(name: string): string {
  return (name ?? "").replace(/[^a-zA-Z0-9]/g, "").toUpperCase();
}

function courseId(name: string): number {
  switch (normaliseCourse(name)) {
    case "AKINALAKE": case "LAKEAKINA": return 0;
    case "MYOGI": return 1;
    case "AKAGI": return 2;
    case "AKINA": return 3;
    case "IROHAZAKA": return 4;
    case "TSUKUBA": return 5;
    case "HAPPO": case "HAPPOGAHARA": return 6;
    case "NAGAO": return 7;
    case "TSUBAKI": case "TSUBAKILINE": return 8;
    case "USUI": return 9;
    case "SADAMINE": return 10;
    case "TSUCHISAKA": return 11;
    case "AKINASNOW": return 12;
    case "HAKONE": return 13;
    case "MOMIJI": case "MOMIJILINE": return 14;
    case "NANAMAGARI": return 15;
    default: return -1;
  }
}

function directionIndex(direction: string): number {
  const d = (direction ?? "").replace(/_/g, " ").replace(/\s+/g, "").toUpperCase();
  switch (d) {
    case "DOWNHILL": case "OUTBOUND": case "CLOCKWISE": return 0;
    case "HILLCLIMB": case "INBOUND": case "REVERSE": case "COUNTERCLOCKWISE": return 1;
    default: return -1;
  }
}

/** Their times read "2:11:053", minutes:seconds:milliseconds. The LAST part is always ms and the
 *  ones before it are read from the right, so "11:053" is a sub-minute time. Returns null rather
 *  than a wrong duration, which on a leaderboard is worse than a missing row. */
function parseTimeMs(text: string): number | null {
  const t = (text ?? "").trim();
  if (!t) return null;
  const parts = t.split(":");
  if (parts.length < 2 || parts.length > 4) return null;
  const n: number[] = [];
  for (const p of parts) {
    if (!/^\d+$/.test(p.trim())) return null;
    n.push(Number(p.trim()));
  }
  const ms = n[n.length - 1];
  const seconds = n[n.length - 2] ?? 0;
  const minutes = n.length >= 3 ? n[n.length - 3] : 0;
  const hours = n.length >= 4 ? n[n.length - 4] : 0;
  if (ms > 999 || seconds > 59 || minutes > 59) return null;
  return ((hours * 60 + minutes) * 60 + seconds) * 1000 + ms;
}

/** Day-first, as the site writes it: "27-04-2025 22:37:49". Parsed explicitly rather than handed
 *  to Date(), which reads that as December on a US locale and lands a year adrift. */
function parseSetAt(text: string): string | null {
  const m = /^(\d{2})-(\d{2})-(\d{4})(?:\s+(\d{2}):(\d{2}):(\d{2}))?/.exec((text ?? "").trim());
  if (!m) return null;
  const [, dd, mm, yyyy, h, mi, s] = m;
  const iso = `${yyyy}-${mm}-${dd}T${h ?? "00"}:${mi ?? "00"}:${s ?? "00"}Z`;
  return Number.isNaN(Date.parse(iso)) ? null : iso;
}

function decodeEntities(s: string): string {
  return (s ?? "")
    .replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&nbsp;/g, " ")
    .trim();
}

type Row = {
  source: string; game: string; course_id: number; direction: number;
  player_name: string; car_text: string; goal_ms: number;
  set_at: string | null; external_id: string | null;
};

export function parseBoards(html: string): Row[] {
  const out: Row[] = [];
  if (!html) return out;

  // Best per player per car per board, mirroring the shape of arcade_lap_times so the two can be
  // ranked as one field. A page listing somebody twice in the same car is one record, their faster.
  const best = new Map<string, Row>();

  for (const m of html.matchAll(BOARD_RX)) {
    const key = m[1];
    const body = m[2];
    if (!key || !body) continue;

    const { course, direction } = splitCourse(key);
    const cid = courseId(course);
    const did = directionIndex(direction);
    if (cid < 0 || did < 0) continue;

    for (const rm of body.matchAll(ROW_RX)) {
      const tr = rm[0];
      const player = decodeEntities(PLAYER_RX.exec(tr)?.[1] ?? "");
      const ms = parseTimeMs(TIME_RX.exec(tr)?.[1] ?? "");
      if (!player || ms === null || ms <= 0 || ms >= 360000) continue;

      const row: Row = {
        source: SOURCE, game: GAME, course_id: cid, direction: did,
        player_name: player.slice(0, 64),
        car_text: decodeEntities(CAR_RX.exec(tr)?.[1] ?? "").slice(0, 96),
        goal_ms: ms,
        set_at: parseSetAt(DATE_RX.exec(tr)?.[1] ?? ""),
        external_id: ENTRY_RX.exec(tr)?.[1] ?? null,
      };
      const k = `${cid}|${did}|${row.car_text}|${row.player_name}`;
      const held = best.get(k);
      if (!held || row.goal_ms < held.goal_ms) best.set(k, row);
    }
  }
  for (const r of best.values()) out.push(r);
  return out;
}

// ---- the endpoint -----------------------------------------------------------

function isServiceRole(auth: string | null): boolean {
  const token = (auth ?? "").replace(/^Bearer\s+/i, "");
  if (!token) return false;
  if (token === SERVICE_KEY) return true;           // the injected sb_secret_ key is not a JWT
  try {
    const p = JSON.parse(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
    return p?.role === "service_role";
  } catch { return false; }
}

Deno.serve(async (req) => {
  if (!isServiceRole(req.headers.get("Authorization"))) {
    return json({ error: "forbidden" }, 403);
  }

  const op = new URL(req.url).searchParams.get("op") || "preview";

  let html = "";
  try {
    const r = await fetch(
      `https://teknoparrot.com/en/Highscore/GameSpecific/${encodeURIComponent(TP_GAME_ID)}`,
      { headers: { "User-Agent": "TrueforceForAll/1.0 (+https://github.com/Mhytee/Trueforce-For-All)" } });
    if (!r.ok) return json({ error: `fetch ${r.status}` }, 502);
    html = await r.text();
  } catch (e) {
    return json({ error: `fetch threw: ${e}` }, 502);
  }

  const rows = parseBoards(html);

  // A page that parses to nothing is a failure, not an empty leaderboard: a login wall, an error
  // page or a redesign all look like this. Writing it would replace a good copy with none.
  if (rows.length === 0) {
    return json({ error: "parsed no rows, leaving the existing copy alone", bytes: html.length }, 502);
  }

  const boards = new Set(rows.map((r) => `${r.course_id}|${r.direction}`));
  const summary = {
    op, rows: rows.length, boards: boards.size,
    players: new Set(rows.map((r) => r.player_name)).size,
    sample: rows.slice(0, 3),
  };
  if (op !== "sync") return json({ ...summary, note: "preview only, nothing written. Use ?op=sync" });

  const res = await fetch(
    `${SUPABASE_URL}/rest/v1/arcade_external_times` +
    `?on_conflict=source,game,course_id,direction,car_text,player_name`,
    {
      method: "POST",
      headers: {
        apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`,
        "Content-Type": "application/json",
        Prefer: "resolution=merge-duplicates,return=minimal",
      },
      body: JSON.stringify(rows.map((r) => ({ ...r, fetched_at: new Date().toISOString() }))),
    });

  if (!res.ok) return json({ ...summary, error: `upsert ${res.status}: ${(await res.text()).slice(0, 300)}` }, 502);
  return json({ ...summary, written: true });
});
