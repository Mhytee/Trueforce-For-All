-- 0083: trigger functions never need a direct EXECUTE grant (the trigger runs
-- them as the table owner). Revoke the default PUBLIC execute so they aren't
-- anon/authenticated-callable, closing the advisor's
-- anon_security_definer_function_executable warning on both notify triggers.
revoke execute on function public.notify_report_flag() from public, anon, authenticated;
revoke execute on function public.notify_appeal()      from public, anon, authenticated;
