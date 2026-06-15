import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Patreon account link (Phase 2). The plugin runs a loopback OAuth flow (PatreonOAuthFlow),
// captures the authorization `code`, and POSTs it here WITH the user's Trueforce session JWT.
// Confidential client: exchanges the code with the Patreon client secret server-side, reads the
// user's identity + membership to OUR campaign, records the link (set_patreon_link), grants
// supporter status if active (set_supporter), and -- only if the patron is ALREADY in our Discord
// server -- records the Discord link from social_connections (set_discord_link source='patreon').
// We DON'T link Discord for a non-member: that link would be dead weight (role-sync 404s, no roles
// ever apply). Such patrons are flagged discord_join_needed so the UI points them at Link Discord,
// which is the only path that can guilds.join them. Linked user is ALWAYS the JWT subject.
//   op=config   : { client_id, redirect_uris, scope }. Any valid JWT.
//   op=exchange : body { code, redirect_uri }. Requires an AUTHENTICATED user JWT.

const SUPABASE_URL  = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY   = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const CLIENT_ID     = Deno.env.get("PATREON_CLIENT_ID") || "";
const CLIENT_SECRET = Deno.env.get("PATREON_CLIENT_SECRET") || "";
const BOT_TOKEN     = Deno.env.get("DISCORD_BOT_TOKEN") || "";
const GUILD_ID      = Deno.env.get("DISCORD_GUILD_ID") || "";
// Our Trueforce For All campaign. Only a membership to THIS campaign may grant supporter status;
// /v2/identity returns the user's pledges to EVERY creator they back. Env override if it changes.
const CAMPAIGN_ID   = Deno.env.get("PATREON_CAMPAIGN_ID") || "1719316";

const SCOPE = "identity identity[email] identity.memberships";

const REDIRECT_URIS = [
  "http://localhost:51781/",
  "http://localhost:51782/",
  "http://localhost:51783/",
];

const PATREON = "https://www.patreon.com/api/oauth2";

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
function decodeJwt(t: string): any {
  try { return JSON.parse(atob(t.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))); } catch { return null; }
}
async function rpc(fn: string, args: unknown): Promise<Response> {
  return fetch(`${SUPABASE_URL}/rest/v1/rpc/${fn}`, {
    method: "POST",
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`, "Content-Type": "application/json" },
    body: JSON.stringify(args),
  });
}
// Is this Discord user already a member of our guild? true / false / null (couldn't check).
async function discordMemberExists(discordId: string): Promise<boolean | null> {
  if (!BOT_TOKEN || !GUILD_ID) return null;
  try {
    const r = await fetch(`https://discord.com/api/v10/guilds/${GUILD_ID}/members/${discordId}`, { headers: { Authorization: `Bot ${BOT_TOKEN}` } });
    if (r.status === 200) return true;
    if (r.status === 404) return false;
    return null;
  } catch { return null; }
}

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  const claims = decodeJwt(token);
  if (!claims) return json({ error: "missing or invalid authorization" }, 401);

  const op = new URL(req.url).searchParams.get("op") || "exchange";

  if (op === "config") {
    if (!CLIENT_ID) return json({ error: "PATREON_CLIENT_ID not set" }, 400);
    return json({ client_id: CLIENT_ID, redirect_uris: REDIRECT_URIS, scope: SCOPE });
  }

  if (claims.role !== "authenticated" || !claims.sub) {
    return json({ error: "sign in to Trueforce before linking Patreon" }, 403);
  }
  if (!CLIENT_ID || !CLIENT_SECRET) return json({ error: "Patreon OAuth is not configured (missing client id/secret)" }, 400);

  let body: any;
  try { body = await req.json(); } catch { return json({ error: "bad request body" }, 400); }
  const code = (body?.code ?? "").toString();
  const redirectUri = (body?.redirect_uri ?? "").toString();
  if (!code) return json({ error: "missing code" }, 400);
  if (!REDIRECT_URIS.includes(redirectUri)) return json({ error: "redirect_uri not allowed" }, 400);

  const tokResp = await fetch(`${PATREON}/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      code, grant_type: "authorization_code",
      client_id: CLIENT_ID, client_secret: CLIENT_SECRET, redirect_uri: redirectUri,
    }).toString(),
  });
  if (!tokResp.ok) return json({ error: "Patreon rejected the sign-in. Try linking again." }, 502);
  let tok: any;
  try { tok = await tokResp.json(); } catch { return json({ error: "Patreon returned an unexpected response. Try linking again." }, 502); }
  const accessToken: string = tok?.access_token;
  if (!accessToken) return json({ error: "no access token from Patreon" }, 502);

  const idUrl = `${PATREON}/v2/identity`
    + "?include=memberships,memberships.currently_entitled_tiers,memberships.campaign"
    + "&fields%5Bmember%5D=patron_status,currently_entitled_amount_cents,campaign_lifetime_support_cents"
    + "&fields%5Btier%5D=title"
    + "&fields%5Buser%5D=full_name,social_connections";
  const meResp = await fetch(idUrl, { headers: { Authorization: `Bearer ${accessToken}` } });
  if (!meResp.ok) return json({ error: "could not read your Patreon profile" }, 502);
  let me: any;
  try { me = await meResp.json(); } catch { return json({ error: "could not read your Patreon profile" }, 502); }

  const data = me?.data ?? {};
  const patreonUserId = (data.id ?? "").toString();
  if (!patreonUserId) return json({ error: "Patreon returned no user id" }, 502);
  const attrs = data.attributes ?? {};
  const fullName: string | null = attrs.full_name || null;
  const discordId: string | null = attrs.social_connections?.discord?.user_id || null;

  const included: any[] = me?.included ?? [];
  const tierTitles = new Map<string, string>();
  for (const inc of included) if (inc.type === "tier") tierTitles.set(inc.id, inc.attributes?.title);
  let active = false; let tier: string | null = null;
  for (const inc of included) {
    if (inc.type !== "member") continue;
    // Scope to OUR campaign. /v2/identity returns the user's memberships to EVERY creator they
    // back, so without this an active pledge to an unrelated campaign (e.g. EmuDeck) would wrongly
    // grant Trueforce supporter status. The member's campaign relationship carries the id.
    if (inc.relationships?.campaign?.data?.id !== CAMPAIGN_ID) continue;
    if (inc.attributes?.patron_status === "active_patron") {
      active = true;
      const tRef = inc.relationships?.currently_entitled_tiers?.data?.[0];
      if (tRef) tier = tierTitles.get(tRef.id) ?? tier;
      break;
    }
  }

  const linkResp = await rpc("set_patreon_link", {
    p_user_id: claims.sub, p_patreon_user_id: patreonUserId, p_full_name: fullName,
  });
  if (!linkResp.ok) {
    const txt = await linkResp.text();
    const conflict = txt.includes("already linked to another account") || txt.includes("patreon_links_patreon_user_id_key") || txt.includes("23505");
    return json({ error: conflict
      ? "That Patreon account is already linked to a different Trueforce user."
      : "could not save the link" }, conflict ? 409 : 500);
  }

  if (active) {
    await rpc("set_supporter", { p_user_id: claims.sub, p_on: true, p_tier: tier, p_patreon_member_id: null });
  }

  // Discord auto-link, gated on guild membership (see file header). Only a real, useful link is
  // recorded; non-members are flagged so the UI sends them to Link Discord to join.
  let discordLinked = false, discordJoinNeeded = false;
  if (discordId) {
    const member = await discordMemberExists(discordId);
    if (member === true) {
      try {
        const dResp = await rpc("set_discord_link", {
          p_user_id: claims.sub, p_discord_id: discordId, p_username: null, p_source: "patreon",
        });
        discordLinked = dResp.ok;
      } catch { /* best-effort */ }
    } else if (member === false) {
      discordJoinNeeded = true;
    }
  }

  return json({ linked: true, patreon_full_name: fullName, is_supporter: active, tier, discord_linked: discordLinked, discord_join_needed: discordJoinNeeded });
});
