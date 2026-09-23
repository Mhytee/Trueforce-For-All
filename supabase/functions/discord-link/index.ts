import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Standalone Discord account link for Trueforce (Phase 2, M5). The plugin runs a loopback
// OAuth flow (scope=identify guilds.join), captures the authorization `code`, and POSTs it
// here WITH the user's Trueforce session JWT. This function is the CONFIDENTIAL client: it
// exchanges the code with the Discord client secret server-side, reads the Discord identity,
// ADDS the user to the community guild (so role assignment can land even if they hadn't
// joined yet), and records the link for the JWT's user via set_discord_link(...,'discord_oauth').
// The linked user is ALWAYS the JWT subject, never a client-supplied id.
//
//   op=config   : returns { client_id, redirect_uris, scope } so the plugin can build the
//                 authorize URL. Any valid JWT.
//   op=exchange : body { code, redirect_uri }. Requires an AUTHENTICATED user JWT.

const SUPABASE_URL  = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY   = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const CLIENT_ID     = Deno.env.get("DISCORD_CLIENT_ID") || "";
const CLIENT_SECRET = Deno.env.get("DISCORD_CLIENT_SECRET") || "";
const BOT_TOKEN     = Deno.env.get("DISCORD_BOT_TOKEN") || "";
const GUILD_ID      = Deno.env.get("DISCORD_GUILD_ID") || "";

// scope=guilds.join lets the bot add the user to the community guild during linking (the
// consent screen shows "Join servers for you"), so a brand-new member still gets their roles.
const SCOPE = "identify guilds.join";

// Loopback redirects the plugin may use. EACH must ALSO be registered in the Discord app's
// OAuth2 settings. Exact-match allowlist: the exchange refuses any redirect_uri not here.
const REDIRECT_URIS = [
  "http://localhost:51778/",
  "http://localhost:51779/",
  "http://localhost:51780/",
];

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
// Add the user to the community guild using their OAuth token (needs guilds.join scope on the
// token + the bot present with the CREATE_INSTANT_INVITE permission). Idempotent: 201 = newly
// added, 204 = was already a member. Best-effort: a failure still lets the link complete.
async function addToGuild(discordId: string, accessToken: string): Promise<"joined" | "already" | "failed"> {
  if (!BOT_TOKEN || !GUILD_ID) return "failed";
  try {
    const r = await fetch(`https://discord.com/api/v10/guilds/${GUILD_ID}/members/${discordId}`, {
      method: "PUT",
      headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" },
      body: JSON.stringify({ access_token: accessToken }),
    });
    if (r.status === 201) return "joined";
    if (r.status === 204) return "already";
    return "failed";
  } catch { return "failed"; }
}

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  const claims = decodeJwt(token);
  if (!claims) return json({ error: "missing or invalid authorization" }, 401);

  const op = new URL(req.url).searchParams.get("op") || "exchange";

  if (op === "config") {
    if (!CLIENT_ID) return json({ error: "DISCORD_CLIENT_ID not set" }, 400);
    return json({ client_id: CLIENT_ID, redirect_uris: REDIRECT_URIS, scope: SCOPE });
  }

  // op === "exchange": the link is recorded for the JWT subject only.
  if (claims.role !== "authenticated" || !claims.sub) {
    return json({ error: "sign in to Trueforce before linking Discord" }, 403);
  }
  if (!CLIENT_ID || !CLIENT_SECRET) return json({ error: "Discord OAuth is not configured (missing client id/secret)" }, 400);

  let body: any;
  try { body = await req.json(); } catch { return json({ error: "bad request body" }, 400); }
  const code = (body?.code ?? "").toString();
  const redirectUri = (body?.redirect_uri ?? "").toString();
  if (!code) return json({ error: "missing code" }, 400);
  if (!REDIRECT_URIS.includes(redirectUri)) return json({ error: "redirect_uri not allowed" }, 400);

  // 1. Exchange the authorization code for a short-lived Discord access token.
  const tokResp = await fetch("https://discord.com/api/v10/oauth2/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: CLIENT_ID, client_secret: CLIENT_SECRET,
      grant_type: "authorization_code", code, redirect_uri: redirectUri,
    }).toString(),
  });
  if (!tokResp.ok) return json({ error: "Discord rejected the sign-in. Try linking again." }, 502);
  let tok: any;
  try { tok = await tokResp.json(); }
  catch { return json({ error: "Discord returned an unexpected response. Try linking again." }, 502); }
  const accessToken: string = tok?.access_token;
  if (!accessToken) return json({ error: "no access token from Discord" }, 502);

  // 2. Read the Discord identity.
  const meResp = await fetch("https://discord.com/api/v10/users/@me", { headers: { Authorization: `Bearer ${accessToken}` } });
  if (!meResp.ok) { await revoke(accessToken); return json({ error: "could not read your Discord profile" }, 502); }
  let me: any;
  try { me = await meResp.json(); }
  catch { await revoke(accessToken); return json({ error: "could not read your Discord profile" }, 502); }
  const discordId = (me?.id ?? "").toString();
  const username = (me?.global_name || me?.username || null);
  if (!discordId) { await revoke(accessToken); return json({ error: "Discord returned no user id" }, 502); }

  // 3. Add them to the community guild so roles can be assigned even if they weren't a member.
  const join = await addToGuild(discordId, accessToken);

  // 4. Record the link for THIS user (service role). set_discord_link enforces
  //    one-discord-per-user and rejects an id already held by another account.
  const linkResp = await rpc("set_discord_link", {
    p_user_id: claims.sub, p_discord_id: discordId, p_username: username, p_source: "discord_oauth",
  });
  // 5. We don't need ongoing Discord access; drop the token regardless of the link outcome.
  await revoke(accessToken);

  if (!linkResp.ok) {
    const txt = await linkResp.text();
    const conflict = txt.includes("already linked to another account")
      || txt.includes("discord_links_discord_id_key") || txt.includes("23505");
    return json({ error: conflict
      ? "That Discord account is already linked to a different Trueforce user."
      : "could not save the link" }, conflict ? 409 : 500);
  }

  return json({ linked: true, join, discord_username: username });
});

async function revoke(accessToken: string): Promise<void> {
  try {
    await fetch("https://discord.com/api/v10/oauth2/token/revoke", {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ client_id: CLIENT_ID, client_secret: CLIENT_SECRET, token: accessToken }).toString(),
    });
  } catch { /* best-effort */ }
}
