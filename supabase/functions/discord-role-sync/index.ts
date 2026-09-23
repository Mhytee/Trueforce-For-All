import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Achievement -> Discord role reconciler. Reads compute_member_metrics() (per-member
// metrics for ALL contributors) + the achievements table (rule definitions + role ids),
// evaluates each enabled achievement (threshold OR top-X% percentile), and adds/removes the
// mapped Discord roles via the REST API. Idempotent, and cheap when nothing changed: it reads
// each member's current roles once and writes only the differences.
//
//   op=diagnose (service role) : go-live preflight (lists guild roles, matches by label,
//                                checks bot hierarchy). Read-only.
//   op=sync                    : reconcile roles. SCOPE depends on the caller:
//      * authenticated user JWT -> syncs ONLY that user (claims.sub). This is the immediate
//        "I just earned something / I just linked" path the plugin fires; a user can never
//        sync anyone but themselves.
//      * service role, ?user=<uid> -> syncs that one user (server-side per-user trigger).
//      * service role, no user -> syncs ALL linked members (cron backstop).
//    The percentile POPULATION is always the whole contributor set; only the assignment is
//    scoped. Runs DRY-RUN until DISCORD_BOT_TOKEN + DISCORD_GUILD_ID + role ids exist.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const BOT_TOKEN    = Deno.env.get("DISCORD_BOT_TOKEN") || "";
const GUILD_ID     = Deno.env.get("DISCORD_GUILD_ID") || "";

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
// DECODES ONLY, it does not verify the signature. The service_role / authenticated gate below
// is built on this, so it is sound only because the function is deployed verify_jwt = true and
// the gateway has already checked the signature. Do not turn verify_jwt off.
function jwtClaims(t: string): any {
  try { return JSON.parse(atob(t.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))); } catch { return null; }
}
function num(v: unknown): number { return typeof v === "number" ? v : Number(v || 0); }
async function rest(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${SUPABASE_URL}/rest/v1/${path}`, {
    ...init,
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`, "Content-Type": "application/json", ...(init?.headers || {}) },
  });
}
async function discord(method: string, path: string): Promise<{ ok: boolean; status: number }> {
  for (let i = 0; i < 2; i++) {
    const r = await fetch(`https://discord.com/api/v10${path}`, { method, headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" } });
    if (r.status === 429) { const ra = Number(r.headers.get("retry-after") || "1"); await new Promise((res) => setTimeout(res, Math.min(ra * 1000, 5000))); continue; }
    return { ok: r.ok, status: r.status };
  }
  return { ok: false, status: 429 };
}
// The member read is the hot path of op=sync, so it carries its own deadline and one
// rate-limit retry. status 0 means the request never completed (deadline or socket error).
// The per-member loops in op=sync and op=entitlements treat a non-ok, non-404 result as "leave
// this member alone this run", and both also require the roles array before trusting a 2xx,
// because the deadline can cut a body after the headers have landed. op=diagnose does NOT check
// either way; it is a manual read-only endpoint and a null body there is a loud 500.
async function discordGet(path: string): Promise<{ ok: boolean; status: number; body: any; headers: Headers | null }> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const r = await fetch(`https://discord.com/api/v10${path}`, { headers: { Authorization: `Bot ${BOT_TOKEN}` }, signal: AbortSignal.timeout(10000) }).catch(() => null);
    if (!r) return { ok: false, status: 0, body: null, headers: null };
    if (r.status === 429 && attempt === 0) {
      const ra = Number(r.headers.get("retry-after") || "1");
      try { await r.body?.cancel(); } catch { /* already drained */ }
      await new Promise((res) => setTimeout(res, Math.min(Math.max(ra, 0) * 1000, 10000)));
      continue;
    }
    let body: any = null; try { body = await r.json(); } catch { /* non-json */ }
    return { ok: r.ok, status: r.status, body, headers: r.headers };
  }
  return { ok: false, status: 429, body: null, headers: null };
}

type Member = { user_id: string; discord_id: string | null; discord_username: string | null; upload_count: number; total_downloads: number; best_wilson: number; consensus_facts: number; votes_cast: number; founder: number; supporter_months: number; founding_supporter: number };
type Ach = { key: string; metric: string; kind: string; threshold: number; min_floor: number; min_population: number; discord_role_id: string | null };

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  const claims = jwtClaims(token);
  const role = claims?.role ?? null;
  if (role !== "service_role" && role !== "authenticated") return json({ error: "forbidden" }, 403);

  const url = new URL(req.url);
  const op = url.searchParams.get("op") || "sync";

  // ---- op=diagnose: read-only go-live preflight (service role only) ----
  if (op === "diagnose") {
    if (role !== "service_role") return json({ error: "forbidden (service role required)" }, 403);
    const secrets = { DISCORD_BOT_TOKEN: !!BOT_TOKEN, DISCORD_GUILD_ID: !!GUILD_ID };
    if (!BOT_TOKEN || !GUILD_ID) return json({ op, secrets, error: "set DISCORD_BOT_TOKEN and DISCORD_GUILD_ID as Edge Function secrets first" }, 400);

    const me = await discordGet("/users/@me");
    const botId: string | null = me.body?.id ?? null;
    const rolesResp = await discordGet(`/guilds/${GUILD_ID}/roles`);
    if (!rolesResp.ok) return json({ op, secrets, error: "could not list guild roles (is the bot in the server? is GUILD_ID right?)", status: rolesResp.status, body: rolesResp.body }, 502);
    const guildRoles = (rolesResp.body as any[]).map((r) => ({ id: r.id as string, name: r.name as string, position: r.position as number, managed: !!r.managed }));

    const botMember = botId ? await discordGet(`/guilds/${GUILD_ID}/members/${botId}`) : { body: null };
    const botRoleIds = new Set<string>((botMember.body?.roles as string[]) || []);
    const botMaxPosition = guildRoles.filter((r) => botRoleIds.has(r.id)).reduce((m, r) => Math.max(m, r.position), 0);

    // Effective guild permissions for the bot = OR of @everyone (role id == guild id) + every role
    // the bot holds. guilds.join (auto-join on link) needs CREATE_INSTANT_INVITE (bit 0);
    // ADMINISTRATOR (bit 3) implies it. permissions can exceed 2^53, so accumulate with BigInt.
    let botPerms = 0n;
    for (const r of (rolesResp.body as any[])) {
      if (r.id === GUILD_ID || botRoleIds.has(r.id)) {
        try { botPerms |= BigInt(r.permissions ?? "0"); } catch { /* skip unparseable */ }
      }
    }
    const hasAdministrator = (botPerms & 8n) !== 0n;
    const hasCreateInvite  = hasAdministrator || (botPerms & 1n) !== 0n;
    const botPermissions = { canAutoJoin: hasCreateInvite, hasCreateInvite, hasAdministrator };

    const aResp = await rest("achievements?select=key,label,discord_role_id&order=sort");
    const achs = (aResp.ok ? await aResp.json() as any[] : []);
    const norm = (s: string) => (s || "").trim().toLowerCase();
    const mapping = achs.map((a) => {
      const match = guildRoles.find((r) => !r.managed && norm(r.name) === norm(a.label));
      return {
        key: a.key, label: a.label, current_role_id: a.discord_role_id,
        matched_role_id: match?.id ?? null, matched_role_name: match?.name ?? null,
        role_position: match?.position ?? null,
        assignable: match ? match.position < botMaxPosition : null,
      };
    });
    const unmatched = mapping.filter((m) => !m.matched_role_id).map((m) => m.label);
    const hierarchy_blocked = mapping.filter((m) => m.matched_role_id && m.assignable === false).map((m) => m.label);

    return json({
      op, secrets, botId, botMaxPosition, guildRoleCount: guildRoles.length,
      botPermissions, canAutoJoin: botPermissions.canAutoJoin,
      all_matched: unmatched.length === 0,
      ready: unmatched.length === 0 && hierarchy_blocked.length === 0,
      unmatched, hierarchy_blocked, mapping,
      guildRoles: guildRoles.filter((r) => !r.managed && r.name !== "@everyone").map((r) => ({ name: r.name, id: r.id, position: r.position })),
    });
  }

  // ---- op=entitlements: map each linked user's supporter Discord role -> backup entitlement ----
  // (service role / cron only). Patreon's native Discord integration grants the tier roles; we
  // read them and flip is_supporter via set_supporter (which runs the 2-year retention timer).
  if (op === "entitlements") {
    if (role !== "service_role") return json({ error: "forbidden (service role required)" }, 403);
    if (!BOT_TOKEN || !GUILD_ID) return json({ error: "DISCORD_BOT_TOKEN / DISCORD_GUILD_ID not set" }, 400);

    const lr = await rest("discord_links?select=user_id,discord_id");
    if (!lr.ok) return json({ error: "could not read discord_links", status: lr.status }, 500);
    const linked = await lr.json() as Array<{ user_id: string; discord_id: string }>;

    // Patreon is authoritative for users who linked Patreon directly; skip them here so the two
    // entitlement sources (Discord roles vs the direct Patreon pledge) can't flip-flop is_supporter.
    const pl = await rest("patreon_links?select=user_id");
    const patreonLinked = new Set<string>((pl.ok ? await pl.json() as any[] : []).map((p) => p.user_id));

    const er = await rest("entitlements?select=user_id,is_supporter");
    const ent = new Map<string, boolean>((er.ok ? await er.json() as any[] : []).map((e) => [e.user_id, e.is_supporter === true]));

    // Highest tier first; the first matching role wins.
    const SUP = [
      { id: "1514172907868131450", tier: "Platinum Supporter" },
      { id: "1514172480774996079", tier: "Gold Supporter" },
      { id: "1514172140654694451", tier: "Supporter" },
    ];

    let supporters = 0, lapsed = 0, unchanged = 0, skipped = 0, deferred = 0;
    const eErrors: string[] = [];
    for (const m of linked) {
      if (patreonLinked.has(m.user_id)) { deferred++; continue; }   // Patreon owns this user's entitlement
      const mem = await discordGet(`/guilds/${GUILD_ID}/members/${m.discord_id}`);
      let on = false, tier: string | null = null;
      // The roles array has to be there, not just a 2xx. A response whose body is cut after the
      // headers land (the read deadline can do that) arrives as ok with a null body, and reading
      // that as an empty role set means no tier matches, which falls straight through to
      // set_supporter(p_on => false) and starts the 2 year retention timer on a paying supporter.
      // Unusable body is a transient read, not a lapse.
      if (mem.ok && Array.isArray(mem.body?.roles)) {
        const roles = new Set<string>(mem.body.roles as string[]);
        for (const s of SUP) { if (roles.has(s.id)) { on = true; tier = s.tier; break; } }
      } else if (mem.status === 404) {
        on = false;   // not in the guild -> lapse (consistent with the role-as-entitlement model)
      } else {
        skipped++; continue;   // transient error or unusable body -> don't change their entitlement
      }
      const current = ent.get(m.user_id) === true;
      if (!on && !current) { unchanged++; continue; }   // non-supporter, no change -> no spurious row
      const sr = await rest("rpc/set_supporter", {
        method: "POST",
        body: JSON.stringify({ p_user_id: m.user_id, p_on: on, p_tier: tier, p_patreon_member_id: null }),
      });
      if (!sr.ok) { eErrors.push(`set_supporter ${m.user_id}: ${sr.status}`); continue; }
      if (on) supporters++; else lapsed++;
    }
    return json({ op: "entitlements", linked: linked.length, supporters, lapsed, unchanged, skipped, deferred, errors: eErrors });
  }

  // ---- op=sync: scope = self (authenticated) / one user (service+?user) / all (service) ----
  let targetUid: string | null = null;
  if (role === "authenticated") {
    if (!claims.sub) return json({ error: "no subject in token" }, 403);
    targetUid = claims.sub;                       // a user can only sync themselves
  } else {
    targetUid = url.searchParams.get("user");     // service role: one user, or null = all
  }

  const mResp = await rest("rpc/compute_member_metrics", { method: "POST", body: "{}" });
  if (!mResp.ok) return json({ error: "compute_member_metrics failed", status: mResp.status }, 500);
  const members = (await mResp.json() as any[]).map((m) => ({
    user_id: m.user_id, discord_id: m.discord_id, discord_username: m.discord_username,
    upload_count: num(m.upload_count), total_downloads: num(m.total_downloads), best_wilson: num(m.best_wilson),
    consensus_facts: num(m.consensus_facts), votes_cast: num(m.votes_cast),
    founder: num(m.founder), supporter_months: num(m.supporter_months), founding_supporter: num(m.founding_supporter),
  })) as Member[];

  const aResp = await rest("achievements?select=key,metric,kind,threshold,min_floor,min_population,discord_role_id&enabled=eq.true&order=sort");
  const achs = (aResp.ok ? await aResp.json() as any[] : []).map((a) => ({
    key: a.key, metric: a.metric, kind: a.kind, threshold: num(a.threshold), min_floor: num(a.min_floor), min_population: num(a.min_population), discord_role_id: a.discord_role_id,
  })) as Ach[];

  function cutoff(metric: string, topPct: number): { cut: number; pop: number } {
    const vals = members.map((m) => num((m as any)[metric])).filter((v) => v > 0).sort((a, b) => b - a);
    if (vals.length === 0) return { cut: Infinity, pop: 0 };
    const idx = Math.min(vals.length - 1, Math.max(0, Math.ceil((topPct / 100) * vals.length) - 1));
    return { cut: vals[idx], pop: vals.length };
  }
  function earns(m: Member, a: Ach): boolean {
    const v = num((m as any)[a.metric]);
    if (a.kind === "threshold") return v >= a.threshold;
    const { cut, pop } = cutoff(a.metric, a.threshold);
    return pop >= a.min_population && v >= a.min_floor && v >= cut;
  }

  const managed = achs.filter((a) => a.discord_role_id).map((a) => a.discord_role_id!);
  const scope = targetUid ? members.filter((m) => m.user_id === targetUid) : members;
  const plan = scope.filter((m) => m.discord_id).map((m) => {
    const earned = achs.filter((a) => earns(m, a)).map((a) => a.key);
    const desired = new Set(achs.filter((a) => a.discord_role_id && earned.includes(a.key)).map((a) => a.discord_role_id!));
    return { discord_id: m.discord_id!, username: m.discord_username, earned, add: [...desired], remove: managed.filter((id) => !desired.has(id)) };
  });

  const dryRun = !BOT_TOKEN || !GUILD_ID || managed.length === 0;
  if (dryRun) {
    return json({ dryRun: true, reason: !BOT_TOKEN ? "DISCORD_BOT_TOKEN not set" : !GUILD_ID ? "DISCORD_GUILD_ID not set" : "no achievements have a discord_role_id yet", scope: targetUid ?? "all", members: members.length, linked: plan.length, plan });
  }

  // Read each member's roles ONCE, then write only the differences. Before this, the loop
  // issued a PUT or DELETE for every managed role on every member every run (members x
  // managed = 143 writes here), all sequential at roughly 0.9 s each, which is the whole of
  // the ~142 s runtime and left the sweep about 8 s under the edge wall-clock ceiling.
  // Steady state is now one read per member and no writes at all.
  //
  // p.add and p.remove are used exactly as built above: p.remove is already
  // managed.filter(not desired), so the roles this can touch stay a subset of the achievement
  // roles. Do not rebuild either list from `have`. A diff seeded from the member's actual
  // roles would sweep in the Patreon tier roles, and op=entitlements reads those to decide
  // is_supporter, which starts the backup retention timer on a paying supporter.
  let applied = 0, removed = 0, unchanged = 0, notInGuild = 0, skipped = 0;
  const errors: string[] = [];
  let sawLimits = false;
  for (const p of plan) {
    const mem = await discordGet(`/guilds/${GUILD_ID}/members/${p.discord_id}`);
    if (!sawLimits && mem.headers) {
      // One line, once per run: the bucket the member read actually landed in. The write
      // route's ceiling was never measured, only inferred, and now that writes are rare this
      // is the budget that matters. Cheap enough to leave in.
      sawLimits = true;
      console.log(`[role-sync] member-read limits bucket=${mem.headers.get("x-ratelimit-bucket")} limit=${mem.headers.get("x-ratelimit-limit")} remaining=${mem.headers.get("x-ratelimit-remaining")} reset-after=${mem.headers.get("x-ratelimit-reset-after")}`);
    }
    if (!mem.ok) {
      // 404 is a member who left the guild; anything else is transient. Neither is a reason
      // to guess at their roles, so both skip. Never treat a failed read as "holds nothing":
      // that reads as "missing every earned role" and would re-PUT every role they have earned,
      // while skipping every remove, so it would be silent as well as wasteful.
      if (mem.status === 404) notInGuild++;
      else { skipped++; errors.push(`member ${p.discord_id}: ${mem.status}`); }
      continue;
    }
    if (!Array.isArray(mem.body?.roles)) { skipped++; errors.push(`member ${p.discord_id}: no roles array`); continue; }
    const have = new Set<string>(mem.body.roles as string[]);
    for (const roleId of p.add) {
      if (have.has(roleId)) { unchanged++; continue; }
      const r = await discord("PUT", `/guilds/${GUILD_ID}/members/${p.discord_id}/roles/${roleId}`);
      if (r.ok) applied++; else if (r.status !== 404) errors.push(`add ${roleId}->${p.discord_id}: ${r.status}`);
    }
    for (const roleId of p.remove) {
      if (!have.has(roleId)) { unchanged++; continue; }
      const r = await discord("DELETE", `/guilds/${GUILD_ID}/members/${p.discord_id}/roles/${roleId}`);
      if (r.ok) removed++; else if (r.status !== 404) errors.push(`remove ${roleId}->${p.discord_id}: ${r.status}`);
    }
  }
  // Role hygiene: strip our managed roles from discord ids that were unlinked or replaced (roles
  // apply to ONE account per user). Full sweep only. An id re-claimed by a current link is just
  // dequeued and left to the normal sync above.
  //
  // Bounded at ORPHAN_LIMIT rows a run, because these stay blind deletes: the queue is
  // consume-once (only set_discord_link and unlink_my_discord ever add to it, and neither
  // re-queues an id), so dequeuing on the strength of a read that came back partial would drop
  // the cleanup for good. Blind costs 13 writes per row at roughly 0.9 s, hence the bound.
  // Whatever does not fit stays queued and goes out on the next run.
  const ORPHAN_LIMIT = 5;
  let orphansCleared = 0, orphansMore = false;
  if (!targetUid && managed.length > 0) {
    const or = await rest(`discord_role_orphans?select=discord_id&limit=${ORPHAN_LIMIT}`);
    const orphans = or.ok ? await or.json() as Array<{ discord_id: string }> : [];
    orphansMore = orphans.length === ORPHAN_LIMIT;   // a full page: assume more are waiting
    const dl = await rest("discord_links?select=discord_id");
    const linkedIds = new Set<string>((dl.ok ? await dl.json() as any[] : []).map((d) => d.discord_id));
    for (const o of orphans) {
      if (linkedIds.has(o.discord_id)) {
        await rest(`discord_role_orphans?discord_id=eq.${o.discord_id}`, { method: "DELETE" });
        continue;
      }
      let ok = true;
      for (const roleId of managed) {
        const r = await discord("DELETE", `/guilds/${GUILD_ID}/members/${o.discord_id}/roles/${roleId}`);
        if (!r.ok && r.status !== 404) ok = false;
      }
      if (ok) { await rest(`discord_role_orphans?discord_id=eq.${o.discord_id}`, { method: "DELETE" }); orphansCleared++; }
    }
  }
  // console.log, not just the response body: the cron caller is pg_net, which hangs up long
  // before this returns and throws the body away, so the response was never a health signal
  // for the scheduled runs. `applied: 0` with everything in `unchanged` is the healthy steady
  // state now, not a regression.
  const result = { dryRun: false, scope: targetUid ?? "all", members: members.length, linked: plan.length, applied, removed, unchanged, notInGuild, skipped, orphansCleared, orphansMore, errors };
  console.log(`[role-sync] ${JSON.stringify(result)}`);
  return json(result);
});
