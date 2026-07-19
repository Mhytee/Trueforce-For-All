import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Patreon -> supporters wall sync + entitlement reconcile (Phase 2). Pulls the campaign's
// members from the Patreon v2 REST API, populates the public list_supporters() wall (active
// patrons only), AND reconciles is_supporter for users who linked Patreon directly: Patreon is
// authoritative for them (the Discord entitlement sync skips patreon-linked users), so an active
// pledge grants and a lapse revokes, with no Discord required.
// Auth: service_role ONLY. Dormant (dryRun) until the PATREON_* secrets are set.
//   op=sync / op=diagnose.

const SUPABASE_URL  = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY   = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const CLIENT_ID     = Deno.env.get("PATREON_CLIENT_ID") || "";
const CLIENT_SECRET = Deno.env.get("PATREON_CLIENT_SECRET") || "";
const ENV_ACCESS    = Deno.env.get("PATREON_CREATOR_ACCESS_TOKEN") || "";
const ENV_REFRESH   = Deno.env.get("PATREON_CREATOR_REFRESH_TOKEN") || "";

const PATREON = "https://www.patreon.com/api/oauth2";

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
function jwtRole(t: string): string | null {
  try { return JSON.parse(atob(t.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")))?.role ?? null; } catch { return null; }
}
async function rest(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${SUPABASE_URL}/rest/v1/${path}`, {
    ...init,
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`, "Content-Type": "application/json", ...(init?.headers || {}) },
  });
}

function displayName(full: string): string {
  const parts = (full || "").trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "A supporter";
  if (parts.length === 1) return parts[0];
  return `${parts[0]} ${parts[parts.length - 1][0].toUpperCase()}.`;
}

async function loadToken(): Promise<{ access: string; refresh: string }> {
  const r = await rest("patreon_oauth?select=access_token,refresh_token&id=eq.1");
  if (r.ok) {
    const rows = await r.json() as Array<{ access_token: string | null; refresh_token: string | null }>;
    if (rows.length && rows[0].access_token) return { access: rows[0].access_token!, refresh: rows[0].refresh_token || ENV_REFRESH };
  }
  return { access: ENV_ACCESS, refresh: ENV_REFRESH };
}
async function saveToken(access: string, refresh: string, expiresIn: number): Promise<void> {
  const expiresAt = new Date(Date.now() + Math.max(0, expiresIn - 60) * 1000).toISOString();
  await rest("patreon_oauth?on_conflict=id", {
    method: "POST",
    headers: { Prefer: "resolution=merge-duplicates" },
    body: JSON.stringify([{ id: 1, access_token: access, refresh_token: refresh, expires_at: expiresAt, updated_at: new Date().toISOString() }]),
  });
}
async function refreshToken(refresh: string): Promise<{ access: string; refresh: string } | null> {
  if (!CLIENT_ID || !CLIENT_SECRET || !refresh) return null;
  const r = await fetch(`${PATREON}/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "refresh_token", refresh_token: refresh, client_id: CLIENT_ID, client_secret: CLIENT_SECRET }).toString(),
  });
  if (!r.ok) return null;
  let j: any; try { j = await r.json(); } catch { return null; }
  if (!j?.access_token) return null;
  const next = { access: j.access_token as string, refresh: (j.refresh_token as string) || refresh };
  await saveToken(next.access, next.refresh, Number(j.expires_in) || 0);
  return next;
}

async function apiGet(path: string, tok: { access: string; refresh: string }): Promise<{ ok: boolean; status: number; body: any; tok: { access: string; refresh: string } }> {
  let cur = tok;
  for (let attempt = 0; attempt < 2; attempt++) {
    const r = await fetch(`${PATREON}${path}`, { headers: { Authorization: `Bearer ${cur.access}` } });
    if (r.status === 401 && attempt === 0) {
      const next = await refreshToken(cur.refresh);
      if (!next) return { ok: false, status: 401, body: null, tok: cur };
      cur = next; continue;
    }
    let body: any = null; try { body = await r.json(); } catch { /* non-json */ }
    return { ok: r.ok, status: r.status, body, tok: cur };
  }
  return { ok: false, status: 401, body: null, tok: cur };
}

async function resolveCampaign(tok: { access: string; refresh: string }): Promise<{ id: string | null; creatorId: string | null; tok: typeof tok; status: number }> {
  const r = await apiGet("/v2/campaigns?include=creator", tok);
  const id = r.ok ? (r.body?.data?.[0]?.id ?? null) : null;
  const creator = r.ok ? (r.body?.data?.[0]?.relationships?.creator?.data?.id ?? null) : null;
  return { id, creatorId: creator ? String(creator) : null, tok: r.tok, status: r.status };
}

type Patron = { member_id: string; full_name: string; display_name: string; tier: string | null; tier_rank: number; amount_cents: number; lifetime_cents: number };
type UserStatus = Map<string, { active: boolean; tier: string | null }>;

// Page through campaign members. `patrons` = active patrons (for the wall, null on any page
// failure so we never sync a partial roster). `userStatus` = EVERY member keyed by Patreon user
// id with their active flag + tier, so the entitlement reconcile can both grant and revoke.
async function fetchMembers(campaignId: string, tok: { access: string; refresh: string }): Promise<{ patrons: Patron[] | null; userStatus: UserStatus | null; tok: typeof tok }> {
  const out: Patron[] = [];
  const userStatus: UserStatus = new Map();
  const fields =
    "include=currently_entitled_tiers,user" +
    "&fields%5Bmember%5D=full_name,patron_status,currently_entitled_amount_cents,campaign_lifetime_support_cents" +
    "&fields%5Btier%5D=title" +
    "&fields%5Buser%5D=full_name,first_name" +
    "&page%5Bcount%5D=200";
  let path: string | null = `/v2/campaigns/${campaignId}/members?${fields}`;
  let cur = tok;
  let guard = 0;
  while (path && guard++ < 100) {
    const r = await apiGet(path, cur);
    cur = r.tok;
    if (!r.ok) return { patrons: null, userStatus: null, tok: cur };
    const included = new Map<string, any>();
    for (const inc of (r.body?.included ?? [])) included.set(`${inc.type}:${inc.id}`, inc);
    for (const m of (r.body?.data ?? [])) {
      const a = m.attributes ?? {};
      const isActive = a.patron_status === "active_patron";
      const userRef = m.relationships?.user?.data;
      const userId = userRef?.id ? String(userRef.id) : null;
      const tierRef = m.relationships?.currently_entitled_tiers?.data?.[0];
      const tier = tierRef ? (included.get(`${tierRef.type}:${tierRef.id}`)?.attributes?.title ?? null) : null;
      // Record EVERY member (active or lapsed) so the reconcile can revoke a cancelled pledge.
      if (userId) userStatus.set(userId, { active: isActive, tier });
      if (!isActive) continue;
      const user = userRef ? included.get(`${userRef.type}:${userRef.id}`) : null;
      const full = a.full_name || user?.attributes?.full_name || user?.attributes?.first_name || "";
      const amount = Number(a.currently_entitled_amount_cents) || 0;
      const lifetime = Number(a.campaign_lifetime_support_cents) || 0;
      out.push({ member_id: String(m.id), full_name: full, display_name: displayName(full), tier, tier_rank: amount, amount_cents: amount, lifetime_cents: lifetime });
    }
    const nextUrl: string | null = r.body?.links?.next ?? null;
    path = nextUrl ? nextUrl.replace(PATREON, "") : null;
  }
  return { patrons: out, userStatus, tok: cur };
}

// Reconcile is_supporter for every user in patreon_links from their CURRENT Patreon status.
// Skips no-op rows (not active + not currently a supporter) so we never create spurious
// entitlement rows. The campaign CREATOR is exempt: they can never be an "active patron" of
// their own campaign, so pledge status must never revoke their entitlement.
// Best-effort: errors are counted, not thrown.
async function reconcileLinkedEntitlements(userStatus: UserStatus, creatorId: string | null): Promise<{ granted: number; revoked: number; unchanged: number; exempt: number; errors: number }> {
  let granted = 0, revoked = 0, unchanged = 0, exempt = 0, errors = 0;
  try {
    const lr = await rest("patreon_links?select=user_id,patreon_user_id");
    const links = lr.ok ? await lr.json() as Array<{ user_id: string; patreon_user_id: string }> : [];
    const er = await rest("entitlements?select=user_id,is_supporter");
    const ent = new Map<string, boolean>((er.ok ? await er.json() as any[] : []).map((e) => [e.user_id, e.is_supporter === true]));
    for (const link of links) {
      if (creatorId && link.patreon_user_id === creatorId) { exempt++; continue; }
      const st = userStatus.get(link.patreon_user_id);
      const active = st?.active === true;
      const tier = st?.tier ?? null;
      const current = ent.get(link.user_id) === true;
      if (!active && !current) { unchanged++; continue; }
      const sr = await rest("rpc/set_supporter", { method: "POST", body: JSON.stringify({ p_user_id: link.user_id, p_on: active, p_tier: tier, p_patreon_member_id: null }) });
      if (!sr.ok) { errors++; continue; }
      if (active) granted++; else revoked++;
    }
  } catch (_) { errors++; }
  return { granted, revoked, unchanged, exempt, errors };
}

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  if (jwtRole(token) !== "service_role") return json({ error: "forbidden (service role required)" }, 403);

  const op = new URL(req.url).searchParams.get("op") || "sync";
  const secrets = { PATREON_CLIENT_ID: !!CLIENT_ID, PATREON_CLIENT_SECRET: !!CLIENT_SECRET, PATREON_CREATOR_ACCESS_TOKEN: !!ENV_ACCESS, PATREON_CREATOR_REFRESH_TOKEN: !!ENV_REFRESH };

  let tok = await loadToken();
  if (!tok.access) return json({ dryRun: true, reason: "PATREON_CREATOR_ACCESS_TOKEN not set", secrets });

  const camp = await resolveCampaign(tok);
  tok = camp.tok;
  if (!camp.id) return json({ error: "could not resolve campaign (token invalid or no campaign?)", status: camp.status, secrets }, 502);

  const fetched = await fetchMembers(camp.id, tok);
  tok = fetched.tok;
  if (fetched.patrons === null || fetched.userStatus === null) return json({ error: "members fetch failed; nothing changed", campaign: camp.id }, 502);

  if (op === "diagnose") {
    return json({ op, secrets, campaign: camp.id, active_patrons: fetched.patrons.length, members_seen: fetched.userStatus.size,
      sample: fetched.patrons.slice(0, 3).map((p) => ({ display_name: p.display_name, tier: p.tier })) });
  }

  const sr = await rest("rpc/sync_supporters", { method: "POST", body: JSON.stringify({ p_patrons: fetched.patrons }) });
  if (!sr.ok) return json({ error: "sync_supporters failed", status: sr.status, body: await sr.text() }, 500);
  const counts = await sr.json();

  const reconcile = await reconcileLinkedEntitlements(fetched.userStatus, camp.creatorId);
  return json({ op, campaign: camp.id, pulled: fetched.patrons.length, counts, reconcile });
});
