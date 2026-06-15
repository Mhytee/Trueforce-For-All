namespace TrueforceForAll.Plugin
{
    /// <summary>The official community backend the plugin talks to.
    ///
    /// These are PUBLIC client values: the Supabase project URL and its publishable
    /// (anon-tier) key. The publishable key is designed to ship in client binaries; access is
    /// governed by row-level security on the server, NOT by keeping this key secret. Every
    /// Supabase client (web, mobile, desktop) ships a key like this, and it's already extractable
    /// from any installed copy. Rotation, if ever needed, is a one-line change here + a release.
    ///
    /// NEVER put the service_role key, the Discord bot token, the Patreon client secret, or the
    /// Resend key here. those are server-side Edge Function secrets and must never reach a client.
    ///
    /// Seeded into TrueforceSettings at startup (TrueforcePlugin.Init) so a fresh install can
    /// reach sign-in / community / backup without any manual configuration.</summary>
    internal static class CommunityBackend
    {
        public const string Url     = "https://dvttzzjbktelcikvyzmt.supabase.co";
        public const string AnonKey = "sb_publishable_QD0B3fT16R-nXGiiYYecEg_2aSd6mXK";
    }
}
