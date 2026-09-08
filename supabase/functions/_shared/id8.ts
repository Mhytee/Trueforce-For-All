// Shared Initial D 8 rendering: the course and car tables, the time formatting, and the weekly
// digest embed itself.
//
// It lives here because TWO functions need the same output and a copy in each would drift the
// first time the copy changed. arcade-digest posts the weekly message; report-action's /id8 digest
// shows a moderator exactly what would post.
//
// It used to be one function calling the other over HTTP, which was wrong twice over. It failed
// outright, because the edge runtime injects SUPABASE_SERVICE_ROLE_KEY as a new-style sb_secret_
// key rather than a JWT, so a function-to-function call carries no role claim and arcade-digest's
// own service_role gate refused its own project's request with a 403. And even working it would
// have spent a second cold start and an HTTP round trip out of Discord's three second budget.
// Importing the renderer costs neither.

// From the exe's own course name table at 0x0121c9e0, so the digest calls a course what the game
// calls it. Direction 0 is downhill, confirmed on the cabinet.
// The only public page the project maintains. It sits in the call to action rather than a footer
// because Discord renders footer text as plain text: a link there would not be one.
const INFO_URL = "https://github.com/Mhytee/Trueforce-For-All";

/** The game's full title, as SEGA writes it. Here rather than typed at each surface, because the
 *  digest and /id8 me both say it and a shortened "Initial D 8" in one of them reads as a different
 *  product. */
export const GAME_NAME = "Initial D: Arcade Stage 8 Infinity";

export const COURSES = [
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
export const CARS: Record<number, string> = {
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

export function courseName(id: number, dir: number): string {
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
export function recordName(s: any): string {
  const where = courseName(s.course_id, s.direction);
  if (s.scope === "car") {
    const car = CARS[s.car_id];
    return car ? `the **${car}** record on ${where}` : `a car record on ${where}`;
  }
  return `the **Any Car** record on ${where}`;
}

/** 202918 -> "3:22.918". The game's own format, so a time reads the same here as on the cabinet. */
export function lap(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return "-";
  const m = Math.floor(ms / 60000);
  const s = Math.floor((ms % 60000) / 1000);
  return `${m}:${String(s).padStart(2, "0")}.${String(ms % 1000).padStart(3, "0")}`;
}

/** A margin of victory. Under a minute reads as seconds, because "12.342" on its own could be read
 *  as hundredths; over a minute it has to fall back to the lap format, since "83.500s" is nonsense
 *  to anyone who thinks in lap times. */
export function gap(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return "";
  return ms < 60000 ? `${(ms / 1000).toFixed(3)}s` : lap(ms);
}

/** Discord renders _ * ` ~ | as formatting. Usernames are [a-zA-Z0-9_]{3,32}, so an underscore in
 *  somebody's name would italicise half the line if it went through raw.
 *
 *  Truncate BEFORE escaping, never after: cutting an escaped string can leave a trailing backslash
 *  that then escapes whatever follows it in the line. */
export function esc(name: string | null): string {
  if (!name) return "(anonymous)";
  return name.slice(0, 32).replace(/([_*`~|\\])/g, "\\$1");
}

/** 1 -> "1st". Ranks read as places in prose, and "moved from 71 to 65" is a subtraction where
 *  "moved from 71st to 65th" is a result. */
function ordinalise(n: number): string {
  if (!Number.isFinite(n)) return String(n);
  const t = n % 100;
  if (t >= 11 && t <= 13) return `${n}th`;
  return `${n}${["th", "st", "nd", "rd"][n % 10] ?? "th"}`;
}

/** A field whose first line says what the section covers.
 *
 *  Scope kept cramming itself into the titles, which turned headers into sentences: "Records
 *  changed hands, in this server" reads as a caption, not a heading. A short italic line under the
 *  title says the same thing and leaves the title a title.
 *
 *  Italics rather than Discord's -# subtext: that renders smaller and greyer where it is supported
 *  and as a literal "-#" where it is not, and an embed nobody can fix after posting is the wrong
 *  place to bet on client support. */
function scoped(name: string, scope: string, body: string): any {
  // Blank line under the subtext. Without it the scope reads as the first entry rather than as a
  // label for the section, and five sections of that is the wall of text this became.
  //
  // And a trailing zero-width space on its own line, which is the only way to put air between
  // consecutive embed fields: Discord stacks them tight, so the last line of one section sits hard
  // against the bold title of the next and the two read as one block. An empty string will not do
  // it, since Discord trims trailing whitespace; the character has to be there but invisible.
  //
  // Budget 1015 rather than 1024 to leave room for it, because the cap is enforced on the final
  // value and a section truncated to exactly 1024 would lose the spacer it needs most.
  return { name, value: `*${scope}*\n\n${body}`.slice(0, 1015) + "\n\u200b" };
}

/** One item in a section. A literal bullet rather than markdown "- ": list markdown in embeds is
 *  recent and renders as a stray hyphen on clients that do not have it, and an embed cannot be
 *  corrected once posted. */
function bullets(lines: string[]): string {
  return lines.map((l) => `• ${l}`).join("\n");
}

/** The default scope for almost everything here.
 *
 *  NOT "in this server". These boards cover every TF4ALL user who has submitted a time, whether or
 *  not they are in this Discord, and saying otherwise excluded most of the people on them.
 *
 *  Deliberately identical on every section that uses it. A uniform label teaches a reader what the
 *  default is, so the one section that says something else stands out on sight rather than having
 *  to be read for. */
const TF4ALL_SCOPE = "Among TF4ALL drivers";

export function buildEmbed(week: any): any {
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
    // SCOPE NAMED, like every other section. crown_taken only ever fires when one tf4all
    // submission beats another tf4all time, so this has always been a local table; the worldwide
    // half is the World records section. Calling it just "Records changed hands" beside a worldwide
    // standing invited the wrong reading.
    fields.push(scoped("Records changed hands", TF4ALL_SCOPE, bullets(lines)));
  }

  // PROGRESS FIRST, because it is the only section that has something to say in an ordinary week.
  // Records change hands rarely on a small server and a standing barely moves, but somebody takes
  // a few seconds off a time most weeks, and that is the story a weekly summary is for.
  const gains: any[] = Array.isArray(week?.gains) ? week.gains : [];
  const moves: any[] = Array.isArray(week?.moves) ? week.moves : [];
  if (gains.length || moves.length) {
    const gainLines: string[] = [];
    const moveLines: string[] = [];
    for (const g of gains.slice(0, 4)) {
      // The next rung, not a list of everyone passed. A run that overtakes six people is still one
      // line, and the only other name in it is the one still ahead, which is the useful one.
      // Nothing when there is no next rung. Saying "that is the fastest on the board" here repeats
      // what the world records section is about to say in full, and a summary that states the same
      // fact twice reads as padding.
      // The rival on its own indented line. On one line the whole thing runs past 120 characters
      // and wraps mid-sentence on a phone, which is most of why this section read as a wall.
      const next = g.next_ms
        ? `\n↳ Current rival: **${lap(g.next_ms)}**${g.next_author ? ` by ${esc(g.next_author)}` : ""}`
        : "";
      gainLines.push(`**${esc(g.actor)}** took **${gap(g.gain_ms)}** off ${courseName(g.course_id, g.direction)}, ` +
                     `now **${lap(g.goal_ms)}**` + next);
    }
    // CLIMBS ONLY. A drop is a public message telling somebody they got worse, and it is often not
    // even their doing: the daily TeknoParrot sync can surface a driver who was always faster and
    // had simply never been scraped, which moves everyone below them down without anybody having
    // driven. We cannot tell that apart from being genuinely overtaken, so the honest thing is to
    // report the half we can stand behind.
    for (const m of moves.filter((x) => x.rank_then > x.rank_now).slice(0, 4)) {
      moveLines.push(`**${esc(m.author)}** climbed from **${ordinalise(m.rank_then)}** to ` +
                     `**${ordinalise(m.rank_now)}** of ${m.of} worldwide`);
    }
    // Two groups, blank line between. Times off a lap and places climbed are different facts, and
    // running them together was the section's other wall-of-text problem.
    const parts = [gainLines.length ? bullets(gainLines) : "", moveLines.length ? bullets(moveLines) : ""];
    fields.push(scoped("Progress this week", TF4ALL_SCOPE,
                       parts.filter(Boolean).join("\n\n").slice(0, 900)));
  }

  // The ladder itself moving. Reported because a target that shifts is news to anyone climbing
  // toward it, and because one of these might one day be somebody here.
  //
  // Only records actually SET in the window. TeknoParrot rows carry the date the time was set, so a
  // record can be told apart from one the daily sync merely read for the first time; announcing the
  // latter as this week's news would be false. A row with no date is excluded, because not knowing
  // when something happened is not evidence that it happened recently.
  const wrs: any[] = Array.isArray(week?.world_records) ? week.world_records : [];
  if (wrs.length) {
    fields.push(scoped("World records", "Between both the TF4ALL and TeknoParrot leaderboards",
      // "Took it from" wherever somebody actually lost it, matching the crown lines, because that
      // is the sentence a record changing hands deserves.
      bullets(wrs.slice(0, 5).map((w) => {
        const where = courseName(w.course_id, w.direction);
        // Not "the first". A missing predecessor means we could not identify one, usually because
        // the runner-up's row carries no date, and that is not the same as there never having been
        // a record here. Claiming a first would be asserting something the data cannot support.
        if (!w.prev_author) {
          return `**${esc(w.author)}** set the world record on ${where}: **${lap(w.goal_ms)}**`;
        }
        const by = w.prev_ms && w.prev_ms > w.goal_ms ? `, ${gap(w.prev_ms - w.goal_ms)} faster` : "";
        return `**${esc(w.author)}** took the world record on ${where} from ` +
               `${esc(w.prev_author)} with **${lap(w.goal_ms)}**${by}`;
      })).slice(0, 900)));
  }

  if (top.length) {
    // The worldwide number sits inside the row as the interesting fact, rather than competing with
    // the header for what the section is about.
    fields.push(scoped("Top ranked players", TF4ALL_SCOPE,
      top.map((t) =>
        `**${t.rank}.** ${esc(t.author)} · ${t.points} pts` +
        (t.merged_rank && t.merged_of ? ` · **${ordinalise(t.merged_rank)} of ${t.merged_of}** worldwide` : "")
      ).join("\n").slice(0, 900)));
  }

  // The invitation. Every line says "here" on purpose: these boards are unclaimed IN THIS SERVER,
  // and the worldwide rank beside each one is what keeps that honest. "2:19.932, unchallenged"
  // sounds untouchable; "2:19.932, 17th of 61 worldwide" reads as a target somebody can take.
  const held: any[] = Array.isArray(week?.unclaimed?.held) ? week.unclaimed.held : [];
  const undriven: number = week?.unclaimed?.undriven ?? 0;
  if (held.length || undriven) {
    const openLines: string[] = [];
    if (held.length) {
      const h = held[0];
      // No "nobody else here has driven it" tail and no "here" on the list: the subtext under the
      // title already says both, and a section that restates its own heading on every line reads
      // as padding. The worldwide rank stays, because that is the part the subtext cannot carry
      // and the part that turns a held board into a target.
      openLines.push(`**${esc(h.author)}** holds ${courseName(h.course_id, h.direction)} at **${lap(h.goal_ms)}**` +
                     (h.world_rank && h.world_of ? `, ${ordinalise(h.world_rank)} of ${h.world_of} worldwide` : ""));
      // One board per bullet rather than three run together on a "Also open:" line. Three courses
      // and three times in one sentence is the densest thing in the message.
      for (const x of held.slice(1, 4)) {
        openLines.push(`${courseName(x.course_id, x.direction)} · **${lap(x.goal_ms)}**`);
      }
    }

    // TOTALS, NOT A REMAINDER. "and 14 more" next to "14 of the 32 have no time" was arithmetically
    // right and unreadable: 18 held minus 4 shown, and 32 minus 18 undriven, both happen to be 14,
    // and nothing on screen let a reader tell that apart from the same number printed twice by
    // mistake. Counting the whole thing removes the coincidence and says more anyway.
    //
    // Note these need not sum to 32: a board two or more drivers here have taken on is in neither
    // count, which is the healthy case and the one this section is trying to bring about.
    const tail: string[] = [];
    if (held.length) {
      tail.push(`**${held.length}** board${held.length === 1 ? " is" : "s are"} held by a single TF4ALL driver`);
    }
    if (undriven >= 32) {
      // "32 of the 32" is arithmetic where a sentence belongs. Only reachable through the
      // moderator preview, since a server with no times anywhere never posts.
      tail.push("no TF4ALL driver has set a time yet");
    } else if (undriven) {
      tail.push(`**${undriven}** of the 32 have no time at all`);
    }
    // The totals are a footnote to the list, not another entry in it, so they sit unbulleted below
    // a blank line rather than reading as a fourth open board.
    const summary = tail.length ? `\n\n${tail.join(" · ")}.` : "";
    // Scope only. "Boards nobody else here has driven" described the first kind of line and was
    // flatly contradicted by it: the very next line names somebody holding one. The section carries
    // two different things, boards held by exactly one driver and boards driven by nobody, and no
    // single sentence covers both without lying about one of them. The lines say what they are.
    fields.push(scoped("Open for a challenge", TF4ALL_SCOPE,
                       (bullets(openLines) + summary).slice(0, 900)));
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
