-- 0041: rename the quality BASE achievement 'acclaimed' -> 'highly_rated' (owner-chosen:
-- clearer + more social-status, and pairs cleanly with the prestige 'top_rated'). The
-- internal key is renamed to match the label; nothing references the key by value (the
-- role-sync Edge Function reads achievements dynamically), so the rename is safe.
update public.achievements
   set key = 'highly_rated', label = 'Highly Rated',
       description = 'Has a highly-rated upload (Wilson >= 0.70)', updated_at = now()
 where key = 'acclaimed';
