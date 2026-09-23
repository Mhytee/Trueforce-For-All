import "jsr:@supabase/functions-js/edge-runtime.d.ts";

// Backup-deletion warning emails. Two entry points:
//   op=run (default, service role / daily cron): emails lapsed supporters as their 2-year
//     backup retention window closes, at 6mo / 3mo / 1mo / 1wk / 1day before deletion. Each
//     stage fires once (entitlements.backup_warn_stage; set_supporter resets it on resubscribe).
//   op=preview&stage=N (authenticated user): a self-test that sends the stage-N email to the
//     CALLER's own address (from the JWT email claim) WITHOUT touching any entitlement. Powers
//     the in-plugin WARNEMAIL dev code.
// Sends via the Resend HTTP API. Runs DRY-RUN (no email) until RESEND_API_KEY is set.

const SUPABASE_URL   = Deno.env.get("SUPABASE_URL")!;
const SERVICE_KEY    = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const RESEND_API_KEY = Deno.env.get("RESEND_API_KEY") || "";
const FROM           = Deno.env.get("WARN_FROM") || "Trueforce For All <noreply@tf4all.mhytee.com>";
const PATREON_URL    = "https://www.patreon.com/Mhytee";

function json(b: unknown, s = 200): Response {
  return new Response(JSON.stringify(b), { status: s, headers: { "Content-Type": "application/json" } });
}
function jwtClaims(t: string): any {
  try { return JSON.parse(atob(t.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))); } catch { return null; }
}
async function rpc(fn: string, args: unknown): Promise<Response> {
  return fetch(`${SUPABASE_URL}/rest/v1/rpc/${fn}`, {
    method: "POST",
    headers: { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}`, "Content-Type": "application/json" },
    body: JSON.stringify(args),
  });
}

// Stored most-distant -> nearest. The tone stays calm and friendly at every stage; only the
// timeframe shrinks and stage 5 adds a "last note like this" framing. No threat/urgency ramp.
// Honesty over comfort: the cloud copy may be the user's only copy, and the free way to keep it
// is "Restore from cloud" (download), which stays available after lapsing. The {date} token in
// each lead is filled with the real deletion date. (Authored via an accuracy-judged copy pass.)
const STAGES = [
  { stage: 1, days: 180, label: "about 6 months",
    subject: "Your Trueforce For All cloud backup, and a date to know about",
    lead: "A quick heads-up: your Trueforce For All cloud backup is scheduled to be deleted on {date}." },
  { stage: 2, days: 90, label: "about 3 months",
    subject: "About 3 months left on your Trueforce For All cloud backup",
    lead: "A reminder that your Trueforce For All cloud backup is scheduled to be deleted on {date}." },
  { stage: 3, days: 30, label: "about a month",
    subject: "About a month left on your Trueforce For All cloud backup",
    lead: "A reminder that your Trueforce For All cloud backup is scheduled to be deleted on {date}." },
  { stage: 4, days: 7, label: "a week",
    subject: "About a week left on your Trueforce For All cloud backup",
    lead: "A quick reminder that your Trueforce For All cloud backup is scheduled to be deleted on {date}." },
  { stage: 5, days: 1, label: "1 day",
    subject: "A last gentle heads-up about your Trueforce For All cloud backup",
    lead: "This is the last note like this: your Trueforce For All cloud backup is scheduled to be deleted on {date}." },
];
type Stage = typeof STAGES[number];

function buildEmail(st: Stage, dateStr: string): { subject: string; html: string } {
  const lead = st.lead.replace(/\{date\}/g, dateStr);
  const p1 = `Your backup holds your saved effects, presets, and settings. If you would like to keep it, open Trueforce For All and use "Restore from cloud" to bring it back. It is free, even now that your support has paused.`;
  const p2 = `If you ever resubscribe on <a href="${PATREON_URL}">Patreon</a>, the cloud copy stays put and the 2-year timer starts fresh. Totally optional, and there is no pressure either way.`;
  const p3 = `If you do nothing, the backup is permanently deleted on ${dateStr} and cannot be recovered afterward. I just did not want it to disappear without a heads-up. Thanks for having been part of Trueforce For All.`;
  const html = `<div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#222;line-height:1.55">
  <p>Hi,</p>
  <p>${lead}</p>
  <p>${p1}</p>
  <p>${p2}</p>
  <p>${p3}</p>
</div>`;
  return { subject: st.subject, html };
}

async function sendEmail(to: string, subject: string, html: string): Promise<boolean> {
  if (!RESEND_API_KEY) return false;
  const r = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: { Authorization: `Bearer ${RESEND_API_KEY}`, "Content-Type": "application/json" },
    body: JSON.stringify({ from: FROM, to: [to], subject, html }),
  });
  return r.ok;
}

Deno.serve(async (req) => {
  const token = (req.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "");
  const claims = jwtClaims(token);
  const role = claims?.role ?? null;
  const op = new URL(req.url).searchParams.get("op") || "run";

  // ---- op=preview: authenticated self-test. Sends a stage email to the caller's own address. ----
  if (op === "preview") {
    if (role !== "authenticated" || !claims?.email) return json({ error: "sign in to preview the warning email" }, 403);
    if (!RESEND_API_KEY) return json({ error: "RESEND_API_KEY isn't set on the server yet, so no email can be sent." }, 400);
    // Per-user throttle (self-spam / Resend quota guard): 5/hour + 15/day. claims.sub is the
    // verified user id (verify_jwt=true). Fail closed if the claim can't be recorded.
    const cl = await rpc("claim_warn_preview", { p_user_id: claims.sub });
    let allowed = false; try { allowed = cl.ok && (await cl.json()) === true; } catch { allowed = false; }
    if (!allowed) return json({ error: "Too many preview emails for now. Try again later." }, 429);
    let stage = parseInt(new URL(req.url).searchParams.get("stage") || "1", 10);
    if (!(stage >= 1 && stage <= 5)) stage = 1;
    const st = STAGES.find((x) => x.stage === stage)!;
    const dateStr = new Date(Date.now() + st.days * 86400000).toISOString().slice(0, 10);   // representative date
    const { subject, html } = buildEmail(st, dateStr);
    const ok = await sendEmail(claims.email, subject, html);
    return ok ? json({ sent: true, stage, label: st.label, to: claims.email })
              : json({ error: "Resend rejected the email" }, 502);
  }

  // ---- op=run: service-role / daily cron. ----
  if (role !== "service_role") return json({ error: "forbidden (service role required)" }, 403);

  const cr = await rpc("list_backup_warn_candidates", {});
  if (!cr.ok) return json({ error: "list_backup_warn_candidates failed", status: cr.status }, 500);
  const candidates = await cr.json() as Array<{ user_id: string; email: string | null; backup_retain_until: string; backup_warn_stage: number }>;

  const now = Date.now();
  let sent = 0, skipped = 0;
  const errors: string[] = [];
  const plan: Array<{ user_id: string; stage: number; label: string }> = [];

  for (const c of candidates) {
    if (!c.email || !c.backup_retain_until) { skipped++; continue; }
    const daysLeft = (new Date(c.backup_retain_until).getTime() - now) / 86400000;
    let target = 0;
    for (const s of STAGES) if (daysLeft <= s.days) target = s.stage;   // last match = most urgent crossed
    if (target <= (c.backup_warn_stage || 0)) { skipped++; continue; }  // already warned for this (or more urgent)
    const st = STAGES.find((x) => x.stage === target)!;
    plan.push({ user_id: c.user_id, stage: target, label: st.label });

    if (!RESEND_API_KEY) { continue; }   // dry-run: report the plan, send nothing
    const dateStr = new Date(c.backup_retain_until).toISOString().slice(0, 10);
    const { subject, html } = buildEmail(st, dateStr);
    const ok = await sendEmail(c.email, subject, html);
    if (!ok) { errors.push(`email ${c.user_id}: send failed`); continue; }
    await rpc("set_backup_warn_stage", { p_user_id: c.user_id, p_stage: target });
    sent++;
  }

  return json({ candidates: candidates.length, sent, skipped, dryRun: !RESEND_API_KEY, plan, errors });
});
