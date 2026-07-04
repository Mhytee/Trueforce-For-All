import "jsr:@supabase/functions-js/edge-runtime.d.ts";
// tweetnacl, not Web Crypto: Ed25519 in the edge runtime's crypto.subtle has
// been unreliable; tweetnacl is the proven path for Discord interaction sigs.
import nacl from "https://esm.sh/tweetnacl@1.0.3";

// Discord interactions endpoint. NO Supabase JWT (verify_jwt = false); auth is
// the Ed25519 signature against DISCORD_APP_PUBLIC_KEY. Answers the PING.
// Every action requires the mod role (DISCORD_MOD_ROLE_ID).
//
// Report card (5 buttons): Dismiss | Remove | Ban... | View footprint | Inspect content
//   Remove  -> submenu (allow appeal / final no-appeal) -> modal for an optional message
//   Ban...  -> ephemeral submenu: 7d / 30d / Permanent
//              temp = suspend ALL their uploads for the window (auto-restored);
//              permanent = purge (archive car facts too)
//   Footprint / Inspect -> ephemeral (read-only)
// Appeal card: Reinstate / Deny (apl:reinstate|deny:<notice_id>).
//
// Ban (from the submenu) and Remove (from the modal) act from a context that
// isn't the card itself, so they edit the original card via the bot token using
// the discord_message_id stamped on the report row.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const PUBLIC_KEY   = Deno.env.get("DISCORD_APP_PUBLIC_KEY") || "";
const MOD_ROLE_ID  = Deno.env.get("DISCORD_MOD_ROLE_ID") || "";
const BOT_TOKEN    = Deno.env.get("DISCORD_BOT_TOKEN") || "";

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
function hexToBytes(hex: string): Uint8Array {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
  return out;
}
function verifySignature(rawBody: string, sigHex: string, ts: string): boolean {
  if (!PUBLIC_KEY || !sigHex || !ts) return false;
  try {
    return nacl.sign.detached.verify(
      new TextEncoder().encode(ts + rawBody), hexToBytes(sigHex), hexToBytes(PUBLIC_KEY));
  } catch (e) { console.error("[report-action] sig verify error:", e); return false; }
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
async function callRpc(fn: string, body: unknown): Promise<any | null> {
  try { const r = await rest(`rpc/${fn}`, { method: "POST", body: JSON.stringify(body) }); if (!r.ok) return null; return await r.json(); }
  catch { return null; }
}
async function patchReport(reportId: string, fields: Record<string, unknown>) {
  await rest(`report_flags?id=eq.${reportId}`, {
    method: "PATCH", headers: { Prefer: "return=minimal" }, body: JSON.stringify(fields),
  }).catch(() => {});
}
function tsTag(iso: string | null, style = "D"): string {
  if (!iso) return "unknown";
  const ms = Date.parse(iso);
  return Number.isFinite(ms) ? `<t:${Math.floor(ms / 1000)}:${style}>` : "unknown";
}

const TARGET_TABLE: Record<string, string> = {
  preset: "presets", game_preset: "game_presets", custom_engine: "custom_engines", pack: "packs",
};

const PING = 1, MESSAGE_COMPONENT = 3, MODAL_SUBMIT = 5;
const PONG = 1, CHANNEL_MESSAGE = 4, UPDATE_MESSAGE = 7, MODAL = 9;
const EPHEMERAL = 64;
const ONE_DAY = 86400 * 1000;

function ephemeral(content: string) {
  return json({ type: CHANNEL_MESSAGE, data: { flags: EPHEMERAL, content: content.slice(0, 1900) } });
}
// Edit the message the interaction is ON (buttons that live on the card).
function updateCard(message: any, color: number, resolvedLine: string) {
  const orig = (message?.embeds && message.embeds[0]) || { fields: [] };
  const updated = { ...orig, color, fields: [...(orig.fields || []), { name: "Resolved", value: resolvedLine }] };
  return json({ type: UPDATE_MESSAGE, data: { embeds: [updated], components: [] } });
}
// Edit the original card out-of-band (Ban submenu + Remove modal act on a
// different message). Best-effort via the bot token + the stored message id.
async function editCardById(reportId: string, color: number, resolvedLine: string) {
  if (!BOT_TOKEN) return;
  const rep = await restRow(`report_flags?id=eq.${reportId}&select=discord_channel_id,discord_message_id`);
  const ch = rep?.discord_channel_id, mid = rep?.discord_message_id;
  if (!ch || !mid) return;
  try {
    const g = await fetch(`https://discord.com/api/v10/channels/${ch}/messages/${mid}`, { headers: { Authorization: `Bot ${BOT_TOKEN}` } });
    if (!g.ok) return;
    const m = await g.json().catch(() => null);
    const orig = (m?.embeds && m.embeds[0]) || { fields: [] };
    const updated = { ...orig, color, fields: [...(orig.fields || []), { name: "Resolved", value: resolvedLine }] };
    await fetch(`https://discord.com/api/v10/channels/${ch}/messages/${mid}`, {
      method: "PATCH", headers: { Authorization: `Bot ${BOT_TOKEN}`, "Content-Type": "application/json" },
      body: JSON.stringify({ embeds: [updated], components: [] }),
    }).catch(() => {});
  } catch { /* best effort */ }
}
function banUntilIso(dur: string): string | null {
  if (dur === "perm") return null;
  const days = dur === "30" ? 30 : 7;
  return new Date(Date.now() + days * ONE_DAY).toISOString();
}
async function resolve(reportId: string): Promise<{ rep: any | null; owner: string | null }> {
  const rep = await restRow(`report_flags?id=eq.${reportId}&select=target_type,target_id,status`);
  if (!rep) return { rep: null, owner: null };
  const table = TARGET_TABLE[rep.target_type as string];
  let owner: string | null = null;
  if (table) {
    const row = await restRow(`${table}?id=eq.${rep.target_id}&select=owner_user_id`);
    owner = row?.owner_user_id ?? null;
  }
  return { rep, owner };
}
function modName(member: any): string {
  return member?.nick || member?.user?.global_name || member?.user?.username || "moderator";
}
function isMod(member: any): boolean {
  const roles: string[] = Array.isArray(member?.roles) ? member.roles : [];
  return !!MOD_ROLE_ID && roles.includes(MOD_ROLE_ID);
}
// Stored audit string: display name + the stable Discord id (so two mods with
// the same nick are distinguishable). Card text uses the plain display name.
function actorOf(member: any): string {
  const id = member?.user?.id || "";
  const name = modName(member);
  return id ? `${name} (${id})` : name;
}
// Resolve EVERY open report for a target (multi-reporter -> one decision).
async function resolveAllReports(rep: any, status: string, actor: string) {
  if (!rep) return;
  await rest(`report_flags?target_type=eq.${rep.target_type}&target_id=eq.${rep.target_id}&status=eq.open`, {
    method: "PATCH", headers: { Prefer: "return=minimal" },
    body: JSON.stringify({ status, resolved_by: actor, resolved_at: new Date().toISOString() }),
  }).catch(() => {});
}

const KIND_CODE: Record<string, string> = { preset: "p", game_preset: "g", custom_engine: "e", pack: "k" };
const CODE_KIND: Record<string, string> = { p: "preset", g: "game_preset", e: "custom_engine", k: "pack" };

// Build the footprint ephemeral payload: a text summary of everything the
// account uploaded + a multi-select to remove any of them (no ban). Removable =
// not already a permanent removal (suspended-by-ban items are still removable,
// which converts them into a removal that survives the ban). Returns the inner
// `data` for a type-4 (open) or type-7 (re-render) interaction response.
async function footprintData(owner: string, reportId: string): Promise<any | null> {
  const f = await callRpc("mod_user_footprint", { p_user_id: owner });
  if (!f || f.error) return null;
  const lines = [
    `**${f.username || "(unknown)"}**  ·  account ${tsTag(f.account_created)}`,
    `Presets ${f.presets_total} (${f.presets_suppressed} hidden) · Game ${f.game_presets_total} · Engines ${f.custom_engines_total} · Packs ${f.packs_total} · Car facts ${f.car_facts_total}`,
    `Times reported: ${f.times_reported}`, "",
  ];
  for (const it of (f.items || []).slice(0, 25)) {
    const nm = it.is_suppressed ? `~~${it.name}~~` : it.name;
    lines.push(`• ${it.kind}: ${nm} (${it.downloads} dl)`);
  }
  const removable = (f.items || []).filter((it: any) => !(it.is_suppressed && !it.suppressed_by_ban)).slice(0, 25);
  const components = removable.length ? [{
    type: 1, components: [{
      type: 3, custom_id: `rmvsel:${reportId}`,
      placeholder: "Remove an upload (no ban)…",
      min_values: 1, max_values: Math.min(removable.length, 25),
      options: removable.map((it: any) => ({
        label: `${it.kind}: ${it.name}`.slice(0, 100),
        value: `${KIND_CODE[it.kind] || "p"}:${it.id}`,
        description: (`${it.downloads} dl` + (it.is_suppressed ? " · suspended" : "")).slice(0, 100),
      })),
    }],
  }] : [];
  if (!removable.length) lines.push("", "_(nothing left to remove)_");
  return { flags: EPHEMERAL, content: lines.join("\n").slice(0, 1900), components };
}

Deno.serve(async (req) => {
  const raw = await req.text();
  if (!verifySignature(raw, req.headers.get("X-Signature-Ed25519") || "", req.headers.get("X-Signature-Timestamp") || "")) {
    return new Response("invalid request signature", { status: 401 });
  }
  let body: any;
  try { body = JSON.parse(raw); } catch { return json({ error: "bad json" }, 400); }

  if (body?.type === PING) return json({ type: PONG });

  // ---- Modal submit: Remove with an optional message ----
  if (body?.type === MODAL_SUBMIT) {
    if (!isMod(body.member)) return ephemeral("You need the moderator role.");
    const m = /^rptm:r:(a|f):([0-9a-fA-F-]{36})$/.exec(String(body.data?.custom_id || ""));
    if (!m) return ephemeral("Unrecognized form.");
    const appealable = m[1] === "a";
    const reportId = m[2];
    let msg = "";
    try { msg = body.data.components?.[0]?.components?.[0]?.value || ""; } catch { /* none */ }
    const res = await callRpc("mod_hide", { p_report_id: reportId, p_kind: "removal", p_actor: actorOf(body.member), p_message: msg, p_appealable: appealable });
    if (!res || res.error) return ephemeral("Remove failed (report gone?).");
    const tail = !res.notified ? " (no account to notify)" : appealable ? " (uploader notified, can appeal)" : " (uploader notified, final, no appeal)";
    await editCardById(reportId, 0xD9534F, `Removed by ${modName(body.member)}${appealable ? "" : " (final)"}${tail}`);
    return ephemeral(appealable ? "Removed. The uploader was notified and can appeal." : "Removed (final). The uploader was notified; no appeal allowed.");
  }

  if (body?.type !== MESSAGE_COMPONENT) return json({ type: PONG });
  if (!isMod(body.member)) return ephemeral("You need the moderator role to act on reports.");

  const who = modName(body.member);       // display name, for card text
  const actor = actorOf(body.member);     // "name (discord_id)", for the audit trail
  const parts = String(body.data?.custom_id || "").split(":");

  // ---- Appeal decisions (apl:reinstate|deny:<notice_id>) ----
  if (parts[0] === "apl") {
    const decision = parts[1], noticeId = parts[2];
    if (decision === "reinstate") {
      const res = await callRpc("mod_reinstate", { p_notice_id: noticeId, p_actor: actor });
      if (!res || res.error) return ephemeral("Reinstate failed (notice not found?).");
      let line = `Reinstated by ${who}`;
      if (res.kind === "ban") line += ` (unbanned, restored ${(res.detail || {}).car_facts_restored ?? 0} car facts)`;
      return updateCard(body.message, 0x5CB85C, line);
    }
    if (decision === "deny") {
      await callRpc("mod_deny_appeal", { p_notice_id: noticeId, p_actor: actor });
      return updateCard(body.message, 0xD9534F, `Appeal denied by ${who}`);
    }
    return ephemeral("Unrecognized appeal action.");
  }

  // ---- Remove uploads from the footprint picker (no ban) ----
  if (parts[0] === "rmvsel") {
    const reportId = parts[1];
    const { owner } = await resolve(reportId);
    if (!owner) return ephemeral("No account is attached to this item.");
    const values: string[] = Array.isArray(body.data?.values) ? body.data.values : [];
    for (const v of values) {
      const [tc, id] = String(v).split(":");
      const type = CODE_KIND[tc];
      if (type && id) await callRpc("mod_remove_target", { p_target_type: type, p_target_id: id, p_actor: actor });
    }
    const d = await footprintData(owner, reportId);
    return json({ type: UPDATE_MESSAGE, data: d || { content: "Done. Nothing left to remove.", components: [] } });
  }

  if (parts[0] !== "rpt") return ephemeral("Unrecognized action.");
  const action = parts[1];

  // ---- Dismiss (resolves every open report on this content) ----
  if (action === "d") {
    const { rep } = await resolve(parts[2]);
    if (!rep) return ephemeral("That report no longer exists.");
    await resolveAllReports(rep, "dismissed", actor);
    return updateCard(body.message, 0x5CB85C, `Dismissed by ${who}`);
  }

  // ---- Remove -> ephemeral submenu: allow-appeal vs final (no appeal) ----
  if (action === "r") {
    const reportId = parts[2];
    return json({ type: CHANNEL_MESSAGE, data: { flags: EPHEMERAL,
      content: "Remove this item. 'Allow appeal' lets the uploader fix it and request review; 'Final' removes it with no appeal.",
      components: [{ type: 1, components: [
        { type: 2, style: 4, label: "Remove (allow appeal)",     custom_id: `rpt:rm:a:${reportId}` },
        { type: 2, style: 4, label: "Remove (final, no appeal)", custom_id: `rpt:rm:f:${reportId}` },
      ]}],
    }});
  }

  // ---- Remove submenu pick -> open the optional-message modal, carrying appealable ----
  if (action === "rm") {
    const appealCode = parts[2], reportId = parts[3];
    return json({
      type: MODAL,
      data: {
        custom_id: `rptm:r:${appealCode}:${reportId}`,
        title: appealCode === "f" ? "Remove (final)" : "Remove content",
        components: [{ type: 1, components: [{
          type: 4, custom_id: "msg", style: 2,
          label: "Optional message to the uploader",
          placeholder: "e.g. inappropriate name - rename it and request a review.",
          required: false, max_length: 1000,
        }]}],
      },
    });
  }

  // ---- Ban... -> ephemeral duration submenu ----
  if (action === "bm") {
    const reportId = parts[2];
    const { owner } = await resolve(reportId);
    if (!owner) return ephemeral("No account is attached to this item, so there's nobody to ban. Use Remove instead.");
    return json({ type: CHANNEL_MESSAGE, data: { flags: EPHEMERAL,
      content: "Ban length. Temporary hides all their uploads until it ends; permanent also purges their content (reversible on appeal).",
      components: [{ type: 1, components: [
        { type: 2, style: 2, label: "7 days",    custom_id: `rpt:b:7:${reportId}` },
        { type: 2, style: 2, label: "30 days",   custom_id: `rpt:b:30:${reportId}` },
        { type: 2, style: 4, label: "Permanent", custom_id: `rpt:b:perm:${reportId}` },
      ]}],
    }});
  }

  // ---- Ban (from the submenu) ----
  if (action === "b") {
    const dur = parts[2], reportId = parts[3];
    const { rep, owner } = await resolve(reportId);
    if (!rep) return ephemeral("That report no longer exists.");
    if (!owner) return ephemeral("No account is attached to this item.");
    const until = banUntilIso(dur);
    const res = await callRpc("mod_ban_user", {
      p_user_id: owner, p_banned_until: until, p_reason: `report ${reportId} (Discord)`, p_actor: actor,
      p_offending_type: rep.target_type, p_offending_id: rep.target_id,
    });
    await resolveAllReports(rep, "banned", actor);
    const label = dur === "perm" ? "permanent" : `${dur}d`;
    let cardLine = `Banned by ${who} (${label})`;
    if (res && !res.error) {
      const items = (res.presets ?? 0) + (res.game_presets ?? 0) + (res.custom_engines ?? 0) + (res.packs ?? 0);
      cardLine += res.permanent
        ? `. Purged: hid ${items} items, archived ${res.car_facts_archived ?? 0} facts, ${res.devices_banned ?? 0} device(s)`
        : `. Suspended: hid ${items} items, ${res.devices_banned ?? 0} device(s)`;
    }
    await editCardById(reportId, 0x8B0000, cardLine);
    return json({ type: UPDATE_MESSAGE, data: { content: `Done. ${cardLine}`, components: [] } });
  }

  // ---- View footprint (ephemeral) ----
  if (action === "fp") {
    const { rep, owner } = await resolve(parts[2]);
    if (!rep) return ephemeral("That report no longer exists.");
    if (!owner) return ephemeral("No account is attached to this item (legacy/seed upload).");
    const d = await footprintData(owner, parts[2]);
    if (!d) return ephemeral("Couldn't load the uploader's footprint.");
    return json({ type: CHANNEL_MESSAGE, data: d });
  }

  // ---- Inspect content (ephemeral): the reported item's actual content ----
  if (action === "in") {
    const reportId = parts[2];
    const rep = await restRow(`report_flags?id=eq.${reportId}&select=target_type,target_id`);
    if (!rep) return ephemeral("That report no longer exists.");
    const table = TARGET_TABLE[rep.target_type as string];
    if (!table) return ephemeral("Unknown content type.");
    const row = await restRow(`${table}?id=eq.${rep.target_id}&select=name,author,description,body`);
    if (!row) return ephemeral("Couldn't load the content.");
    const out: string[] = [
      `**${row.name || "(unnamed)"}**${row.author ? ` by ${row.author}` : ""}`,
      row.description ? `> ${String(row.description).slice(0, 600)}` : "_(no description)_",
      "",
    ];
    try {
      const b = row.body;
      const entries = b?.entries || b?.items || b?.presets;
      if (Array.isArray(entries)) {
        out.push(`Contains ${entries.length} item(s):`);
        for (const e of entries.slice(0, 20)) out.push(`• ${e?.name || e?.car_id || e?.game || "item"}`);
      } else if (b && typeof b === "object") {
        const keys = Object.keys(b);
        out.push(`Settings (${keys.length}): ` + keys.slice(0, 30).join(", "));
        out.push("```json\n" + JSON.stringify(b).slice(0, 900) + "\n```");
      }
    } catch { /* body unreadable */ }
    return ephemeral(out.join("\n"));
  }

  return ephemeral("Unrecognized action.");
});
