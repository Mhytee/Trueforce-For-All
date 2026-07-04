import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Posts/updates ONE report card per piece of content in #preset-reports. The
// after-insert trigger fires per report row, but multiple reporters of the same
// item collapse into a single card: the first open report posts it, later ones
// just bump the "Reports" count + reason breakdown on the existing card. The
// card's buttons carry the card-holder report id; report-action resolves the
// whole target (all open reports) when acted on. Trigger sends the service_role
// JWT (verify_jwt on). DRY-RUN until DISCORD_BOT_TOKEN + DISCORD_REPORTS_CHANNEL_ID.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const BOT_TOKEN    = Deno.env.get("DISCORD_BOT_TOKEN") || "";
const CHANNEL_ID   = Deno.env.get("DISCORD_REPORTS_CHANNEL_ID") || "";

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
async function restRows(path: string): Promise<any[]> {
  try { const r = await rest(path); if (!r.ok) return []; const a = await r.json(); return Array.isArray(a) ? a : []; }
  catch { return []; }
}
async function restRow(path: string): Promise<any | null> {
  const a = await restRows(path); return a[0] ?? null;
}
async function callRpc(fn: string, body: unknown): Promise<any | null> {
  try { const r = await rest(`rpc/${fn}`, { method: "POST", body: JSON.stringify(body) }); if (!r.ok) return null; return await r.json(); }
  catch { return null; }
}
function tsTag(iso: string | null, style = "D"): string {
  if (!iso) return "unknown";
  const ms = Date.parse(iso);
  return Number.isFinite(ms) ? `<t:${Math.floor(ms / 1000)}:${style}>` : "unknown";
}

const TARGETS: Record<string, { table: string; label: string; cols: string; hasGame: boolean; hasTags: boolean }> = {
  preset:        { table: "presets",        label: "Car preset",    cols: "name,author,description,game,effect_tags,downloads,upvotes,downvotes,created_at,owner_user_id", hasGame: true,  hasTags: true },
  game_preset:   { table: "game_presets",   label: "Game preset",   cols: "name,author,description,game,effect_tags,downloads,upvotes,downvotes,created_at,owner_user_id", hasGame: true,  hasTags: true },
  custom_engine: { table: "custom_engines", label: "Custom engine", cols: "name,author,description,downloads,upvotes,downvotes,created_at,owner_user_id", hasGame: false, hasTags: false },
  pack:          { table: "packs",          label: "Pack",          cols: "name,author,description,downloads,upvotes,downvotes,created_at,owner_user_id", hasGame: false, hasTags: false },
};
const CATEGORY_LABELS: Record<string, string> = {
  broken: "Broken / doesn't work", inappropriate: "Inappropriate content", spam: "Spam or low effort",
  wrong_data: "Wrong car or data", stolen: "Stolen (not the uploader's)", other: "Other",
};

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  if (jwtClaims(token)?.role !== "service_role") return json({ error: "forbidden" }, 403);

  let body: any = {};
  try { body = await req.json(); } catch { /* empty */ }
  const reportId = String(body?.report_id || "");
  if (!reportId) return json({ error: "report_id required" }, 400);

  const report = await restRow(`report_flags?id=eq.${reportId}&select=id,target_type,target_id`);
  if (!report) return json({ error: "report not found" }, 404);
  const t = TARGETS[report.target_type as string];
  if (!t) return json({ error: `unknown target_type ${report.target_type}` }, 400);

  // Every OPEN report for this target (for the count, reason breakdown, and to
  // find an existing card to update rather than posting a duplicate).
  const open = await restRows(
    `report_flags?target_type=eq.${report.target_type}&target_id=eq.${report.target_id}&status=eq.open` +
    `&select=id,category,note,reporter_id,created_at,discord_message_id,discord_channel_id&order=created_at.asc`);
  const holder = open.find((r) => r.discord_message_id) || null;
  const holderId = holder ? holder.id : report.id;
  const count = open.length || 1;

  const target      = await restRow(`${t.table}?id=eq.${report.target_id}&select=${t.cols}`);
  const targetName  = target?.name || "(name unavailable)";
  const ownerId     = target?.owner_user_id || null;
  const foot        = ownerId ? await callRpc("mod_user_footprint", { p_user_id: ownerId }) : null;

  // Reason breakdown across all open reports + the most recent note.
  const catCounts: Record<string, number> = {};
  let latestNote: string | null = null, latestNoteAt = 0;
  for (const r of open) {
    const c = (r.category as string) || "other";
    catCounts[c] = (catCounts[c] || 0) + 1;
    if (r.note) { const ms = Date.parse(r.created_at) || 0; if (ms >= latestNoteAt) { latestNoteAt = ms; latestNote = r.note; } }
  }
  const reasons = Object.keys(catCounts).length
    ? Object.entries(catCounts).map(([k, n]) => `${CATEGORY_LABELS[k] || k}${n > 1 ? ` x${n}` : ""}`).join(", ")
    : "Other";

  if (!BOT_TOKEN || !CHANNEL_ID) {
    console.log(`[report-notify] DRY-RUN: ${t.label} "${targetName}" reports=${count} reasons=${reasons}`);
    return json({ ok: true, dryRun: true, report_id: reportId });
  }

  const fields: any[] = [
    { name: "Type",    value: t.label,        inline: true },
    { name: "Reports", value: String(count),  inline: true },
  ];
  fields.push({ name: "Reasons", value: reasons.slice(0, 256), inline: true });
  fields.push({ name: "Stats", value: `${target?.downloads ?? 0} dl  ${target?.upvotes ?? 0}/${target?.downvotes ?? 0}`, inline: true });
  if (t.hasGame && target?.game) fields.push({ name: "Game", value: String(target.game), inline: true });
  if (t.hasTags && Array.isArray(target?.effect_tags) && target.effect_tags.length)
    fields.push({ name: "Tags", value: target.effect_tags.join(", ").slice(0, 256), inline: true });
  if (target?.created_at) fields.push({ name: "Uploaded", value: tsTag(target.created_at), inline: true });
  if (target?.description) fields.push({ name: "Description", value: String(target.description).slice(0, 1000) });
  if (latestNote) fields.push({ name: "Latest reporter note", value: String(latestNote).slice(0, 1000) });

  const uploaderName = foot?.username || target?.author || "(no account)";
  if (foot && !foot.error) {
    const total = (foot.presets_total ?? 0) + (foot.game_presets_total ?? 0) + (foot.custom_engines_total ?? 0) + (foot.packs_total ?? 0);
    const rap = [
      `Account: ${tsTag(foot.account_created)}`,
      `Uploads: ${total} (${foot.presets_suppressed ?? 0} already hidden)`,
      `Car facts: ${foot.car_facts_total ?? 0}`,
      `Times reported: ${foot.times_reported ?? 0}`,
    ].join("\n");
    fields.push({ name: `Uploader: ${uploaderName}`, value: rap });
  } else {
    fields.push({ name: "Uploader", value: `${uploaderName} (no linked account on this item)` });
  }

  const noAccount = !ownerId;
  const components = [
    { type: 1, components: [
      { type: 2, style: 2, label: "Dismiss", custom_id: `rpt:d:${holderId}`,  disabled: false },
      { type: 2, style: 4, label: "Remove",  custom_id: `rpt:r:${holderId}`,  disabled: false },
      { type: 2, style: 1, label: "Ban…", custom_id: `rpt:bm:${holderId}`, disabled: noAccount },
    ]},
    { type: 1, components: [
      { type: 2, style: 2, label: "View footprint",  custom_id: `rpt:fp:${holderId}`, disabled: noAccount },
      { type: 2, style: 2, label: "Inspect content", custom_id: `rpt:in:${holderId}`, disabled: false },
    ]},
  ];

  const payload = {
    embeds: [{
      title: `Report: ${targetName}`.slice(0, 256),
      color: 0xE0A800,
      fields,
      footer: { text: `report ${holderId}` },
    }],
    components,
  };

  // Update the existing card on an additional report; otherwise post a new one.
  if (holder && holder.discord_message_id && holder.discord_channel_id) {
    const u = await fetch(`https://discord.com/api/v10/channels/${holder.discord_channel_id}/messages/${holder.discord_message_id}`, {
      method: "PATCH", headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" }, body: JSON.stringify(payload),
    });
    if (!u.ok) console.error(`[report-notify] card update failed ${u.status}`);
    return json({ ok: true, updated: true, message_id: holder.discord_message_id, reports: count });
  }

  const resp = await fetch(`https://discord.com/api/v10/channels/${CHANNEL_ID}/messages`, {
    method: "POST", headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" }, body: JSON.stringify(payload),
  });
  if (!resp.ok) {
    const txt = await resp.text().catch(() => "");
    console.error(`[report-notify] Discord post failed ${resp.status}: ${txt}`);
    return json({ error: "discord post failed", status: resp.status }, 502);
  }
  const msg = await resp.json().catch(() => null);
  if (msg?.id) {
    await rest(`report_flags?id=eq.${report.id}`, {
      method: "PATCH", headers: { Prefer: "return=minimal" },
      body: JSON.stringify({ discord_message_id: msg.id, discord_channel_id: CHANNEL_ID }),
    }).catch(() => {});
  }
  return json({ ok: true, message_id: msg?.id ?? null, reports: count });
});
