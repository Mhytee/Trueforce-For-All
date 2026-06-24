-- 0091: lock down RPC EXECUTE surface flagged by the 2026-06-23 security audit.
--
-- (1) The profanity trigger helpers regained PUBLIC execute when 0090 added
--     them (a freshly created function defaults to PUBLIC EXECUTE). They are
--     BEFORE INSERT/UPDATE triggers on presets/game_presets/packs/custom_engines
--     (name, description) and profiles (username); Postgres never checks EXECUTE
--     when a trigger fires, so removing every grant leaves the triggers working
--     while taking the helpers off the public REST surface
--     (/rest/v1/rpc/_reject_blocked_*). Lock completely.
revoke execute on function public._reject_blocked_text()     from anon, authenticated, public;
revoke execute on function public._reject_blocked_username() from anon, authenticated, public;

-- (2) The report_* RPCs regained PUBLIC execute when 0080 redefined them with
--     the (uuid, text, text) category/note signature (the same signature-change
--     regression called out in 0023). They already raise 'Login required' for
--     anon, and the plugin only ever calls them with a Bearer token (the Report
--     button lives behind community sign-in). Drop anon and PUBLIC; keep
--     authenticated so signed-in users can still report.
revoke execute on function public.report_preset(uuid, text, text)        from anon, public;
revoke execute on function public.report_game_preset(uuid, text, text)   from anon, public;
revoke execute on function public.report_pack(uuid, text, text)          from anon, public;
revoke execute on function public.report_custom_engine(uuid, text, text) from anon, public;

-- (3) get_account_stats and check_username_available were intentionally left
--     anon-callable in 0026, but neither needs it. Both are only reached after
--     sign-in: the Account panel guards on AuthIsSignedIn and the client returns
--     null without a bearer, and the username picker opens only post-sign-in.
--     Revoking anon also closes username enumeration to the logged-out
--     whole-internet role. Keep authenticated.
revoke execute on function public.get_account_stats()            from anon, public;
revoke execute on function public.check_username_available(text) from anon, public;
