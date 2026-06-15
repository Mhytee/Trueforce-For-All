import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// 2-year backup-retention GC (Phase 2, M4). Cron-invoked. Lists users whose backup retention
// window has passed (lapsed supporters > 2 years) via list_expired_backup_users(), then DELETEs
// each user's backups/<uid>/setup.json through the Storage API (the storage.protect_delete
// trigger blocks direct SQL deletes, so this must go through the Storage REST API with the
// service role). Service-role-gated. Idempotent: a missing object (404) counts as done.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
function jwtRole(t: string): string | null {
  try { return JSON.parse(atob(t.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))).role ?? null; } catch { return null; }
}

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  if (jwtRole(token) !== "service_role") return json({ error: "forbidden (service role required)" }, 403);

  const lr = await fetch(`${SUPABASE_URL}/rest/v1/rpc/list_expired_backup_users`, {
    method: "POST",
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`, "Content-Type": "application/json" },
    body: "{}",
  });
  if (!lr.ok) return json({ error: "list_expired_backup_users failed", status: lr.status }, 500);
  const rows = await lr.json() as Array<{ user_id: string }>;

  let deleted = 0;
  const errors: string[] = [];
  for (const r of rows) {
    const uid = r.user_id;
    if (!uid) continue;
    const del = await fetch(`${SUPABASE_URL}/storage/v1/object/backups/${encodeURIComponent(uid)}/setup.json`, {
      method: "DELETE",
      headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}` },
    });
    if (del.ok || del.status === 404) deleted++;   // 404 = already gone
    else errors.push(`delete ${uid}: ${del.status}`);
  }

  return json({ expired: rows.length, deleted, errors });
});
