import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// 2-year backup-retention GC (Phase 2, M4). Cron-invoked. Lists users whose backup retention
// window has passed (lapsed supporters > 2 years) via list_expired_backup_users(), then DELETEs
// each user's backups/<uid>/setup.json through the Storage API (the storage.protect_delete
// trigger blocks direct SQL deletes, so this must go through the Storage REST API with the
// service role). Service-role-gated. Idempotent: a missing object (404) counts as done.
//
// Also drains backup_delete_queue (migration 0097): delete_my_account enqueues the deleting
// user's uid there because the entitlements row cascades away with auth.users, so this GC's
// lapsed-supporter listing could never see them. Queue rows are cleared per-uid only after
// the Storage DELETE succeeds (or 404s), so a failed delete retries on the next run.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY  = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
function jwtRole(t: string): string | null {
  try { return JSON.parse(atob(t.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))).role ?? null; } catch { return null; }
}

async function rpc(name: string, body: unknown): Promise<Response> {
  return fetch(`${SUPABASE_URL}/rest/v1/rpc/${name}`, {
    method: "POST",
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

async function deleteBackupObject(uid: string): Promise<boolean> {
  const del = await fetch(`${SUPABASE_URL}/storage/v1/object/backups/${encodeURIComponent(uid)}/setup.json`, {
    method: "DELETE",
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}` },
  });
  return del.ok || del.status === 404;   // 404 = already gone
}

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  if (jwtRole(token) !== "service_role") return json({ error: "forbidden (service role required)" }, 403);

  const errors: string[] = [];

  // 1. Lapsed supporters past the 2-year retention window. Non-fatal on
  // failure: the deleted-account queue drain below is the ONLY deletion path
  // for deleted accounts' backups and must not starve because this unrelated
  // listing had a bad night.
  let rows: Array<{ user_id: string }> = [];
  const lr = await rpc("list_expired_backup_users", {});
  if (!lr.ok) errors.push(`list_expired_backup_users failed: ${lr.status}`);
  else rows = await lr.json() as Array<{ user_id: string }>;

  let deleted = 0;
  for (const r of rows) {
    const uid = r.user_id;
    if (!uid) continue;
    if (await deleteBackupObject(uid)) deleted++;
    else errors.push(`delete ${uid}: storage delete failed`);
  }

  // 2. Deleted-account queue (delete_my_account enqueues; we clear per-uid on success).
  let queueDeleted = 0;
  let queued = 0;
  const qr = await rpc("list_queued_backup_deletions", {});
  if (!qr.ok) {
    errors.push(`list_queued_backup_deletions failed: ${qr.status}`);
  } else {
    const qrows = await qr.json() as Array<{ user_id: string }>;
    queued = qrows.length;
    const cleared: string[] = [];
    for (const r of qrows) {
      const uid = r.user_id;
      if (!uid) continue;
      if (await deleteBackupObject(uid)) { queueDeleted++; cleared.push(uid); }
      else errors.push(`queue delete ${uid}: storage delete failed`);
    }
    if (cleared.length) {
      const cr = await rpc("clear_backup_delete_queue", { p_user_ids: cleared });
      if (!cr.ok) errors.push(`clear_backup_delete_queue failed: ${cr.status}`);
    }
  }

  return json({ expired: rows.length, deleted, queued, queue_deleted: queueDeleted, errors });
});
