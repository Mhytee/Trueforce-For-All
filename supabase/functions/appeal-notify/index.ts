import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Posts an appeal card to #preset-reports when a user clicks "Request review"
// in the plugin (request_review RPC -> moderation_notices.appeal_state='pending'
// -> notify_appeal trigger -> here). Mirror of report-notify: trigger sends the
// Vault service_role JWT, so verify_jwt stays on.
//
// The card shows what was actioned (warn/removal/ban), the item, the uploader,
// and their appeal note, with Reinstate / Deny buttons handled by report-action
// (the single interactions endpoint) via apl:reinstate|deny:<notice_id>.
//
// DRY-RUN until DISCORD_BOT_TOKEN + DISCORD_REPORTS_CHANNEL_ID exist.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const BOT_TOKEN    = Deno.env.get("DISCORD_BOT_TOKEN") || "";
// Appeals reuse the reports channel; optional override if you split them later.
const CHANNEL_ID   = Deno.env.get("DISCORD_APPEALS_CHANNEL_ID") || Deno.env.get("DISCORD_REPORTS_CHANNEL_ID") || "";

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
function jwtClaims(t: string): any {
  try { return JSON.parse(atob(t.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))); } catch { return null; }
}
async function rest(path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${SUPABASE_URL}/rest/v1/${path}`, {
    ...init,
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`, "Content-Type": "application/json", ...(init?.headers || {}) },
  });
}
async function restRow(path: string): Promise<any | null> {
  try { const r = await rest(path); if (!r.ok) return null; const a = await r.json(); return Array.isArray(a) ? (a[0] ?? null) : a; }
  catch { return null; }
}

const KIND_LABEL: Record<string, string> = { warn: "Warned (hidden)", removal: "Removed", ban: "Banned" };

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  if (jwtClaims(token)?.role !== "service_role") return json({ error: "forbidden" }, 403);

  let body: any = {};
  try { body = await req.json(); } catch { /* empty */ }
  const noticeId = String(body?.notice_id || "");
  if (!noticeId) return json({ error: "notice_id required" }, 400);

  const n = await restRow(`moderation_notices?id=eq.${noticeId}&select=id,user_id,target_type,target_id,kind,reason_category,message,appeal_note,proposed_name,proposed_description,proposed_body`);
  if (!n) return json({ error: "notice not found" }, 404);

  const reporter = await restRow(`profiles?id=eq.${n.user_id}&select=username`);
  const username = reporter?.username || "(unknown)";

  // Current (frozen) values of the reported item, to diff against the user's
  // proposed fix. Read with the service key, which sees suppressed rows.
  const TT: Record<string, string> = { preset: "presets", game_preset: "game_presets", custom_engine: "custom_engines", pack: "packs" };
  const tbl = TT[n.target_type as string];
  const cur = (tbl && n.target_id) ? await restRow(`${tbl}?id=eq.${n.target_id}&select=name,description`) : null;
  function diffLine(label: string, oldV: string | null, newV: string | null): string | null {
    const o = (oldV || "").trim(), nw = (newV || "").trim();
    if (!nw || o === nw) return null;
    return `${label}: ~~${o || "(empty)"}~~ -> ${nw}`;
  }

  if (!BOT_TOKEN || !CHANNEL_ID) {
    console.log(`[appeal-notify] DRY-RUN: appeal on ${n.kind} by ${username}`);
    return json({ ok: true, dryRun: true, notice_id: noticeId });
  }

  const fields: any[] = [
    { name: "Original action", value: KIND_LABEL[n.kind as string] || n.kind, inline: true },
    { name: "Uploader",        value: username, inline: true },
  ];
  if (n.reason_category) fields.push({ name: "Reason", value: String(n.reason_category), inline: true });
  if (n.message)     fields.push({ name: "What they were told", value: String(n.message).slice(0, 1000) });
  fields.push({ name: "Their appeal", value: (n.appeal_note ? String(n.appeal_note) : "(no note provided)").slice(0, 1000) });

  // Proposed fix diff (old -> new). On Reinstate the mod applies exactly this.
  const diffs: string[] = [];
  const dN = diffLine("Name", cur?.name, n.proposed_name); if (dN) diffs.push(dN);
  const dD = diffLine("Description", cur?.description, n.proposed_description); if (dD) diffs.push(dD);
  if (n.proposed_body) diffs.push("Body: replaced with a different tune");
  fields.push({ name: "Proposed fix", value: (diffs.length ? diffs.join("\n") : "(no changes proposed, appealing as-is)").slice(0, 1000) });

  const payload = {
    embeds: [{
      title: `Appeal: ${username}`.slice(0, 256),
      color: 0x3B88C3, // blue = needs a decision
      fields,
      footer: { text: `notice ${noticeId}` },
    }],
    components: [{
      type: 1,
      components: [
        { type: 2, style: 3, label: "Reinstate", custom_id: `apl:reinstate:${noticeId}` }, // success / green
        { type: 2, style: 4, label: "Deny",      custom_id: `apl:deny:${noticeId}` },       // danger / red
      ],
    }],
  };

  const resp = await fetch(`https://discord.com/api/v10/channels/${CHANNEL_ID}/messages`, {
    method: "POST",
    headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  if (!resp.ok) {
    const txt = await resp.text().catch(() => "");
    console.error(`[appeal-notify] Discord post failed ${resp.status}: ${txt}`);
    return json({ error: "discord post failed", status: resp.status }, 502);
  }
  const msg = await resp.json().catch(() => null);
  if (msg?.id) {
    await rest(`moderation_notices?id=eq.${noticeId}`, {
      method: "PATCH", headers: { Prefer: "return=minimal" },
      body: JSON.stringify({ discord_message_id: msg.id, discord_channel_id: CHANNEL_ID }),
    }).catch(() => {});
  }
  return json({ ok: true, message_id: msg?.id ?? null });
});
