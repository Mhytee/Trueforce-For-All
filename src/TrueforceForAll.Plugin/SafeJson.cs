using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    /// <summary>The one deserializer guard for untrusted JSON. Use these
    /// settings on EVERY Newtonsoft deserialize of user-supplied or imported
    /// content (presets, packs, installed-packs file, backups, community
    /// bodies): MaxDepth 64 prevents unbounded recursion / stack-overflow DoS
    /// through a deeply nested malicious document. New stores and import paths
    /// must reference this instead of declaring their own settings; the
    /// security cap lives here and nowhere else. (Core.Tests mirrors the
    /// contract with its own copy since it cannot reference this assembly.)</summary>
    internal static class SafeJson
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MaxDepth = 64
        };
    }
}
