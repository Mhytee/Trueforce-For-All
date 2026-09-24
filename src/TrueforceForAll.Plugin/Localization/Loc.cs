// The static door to the language table: Loc.T(key), Loc.F(key, args) and
// Loc.N(key, n, args) for C#, Loc.Instance for the XAML markup extension.
// No WPF here so a designer, a test or a call that runs before Init never
// drags the presentation stack in; every method returns the key when the
// store does not exist yet, so nothing throws for want of a table.
//
// Design and phases: docs/localization-plan.md.

using System;

namespace TrueforceForAll.Plugin.Localization
{
    public static class Loc
    {
        /// <summary>The live table, or null until Initialize has run.</summary>
        public static LocStore Instance { get; private set; }

        /// <summary>Build the store, resolve the requested language and
        /// publish it. Called once from TrueforcePlugin.Init.</summary>
        /// <param name="readEmbedded">Returns the embedded language JSON for a
        /// culture tag, or null when the build has none.</param>
        /// <param name="languagesRoot">The folder holding root overrides and
        /// shipped\ copies, or null for no disk layers.</param>
        /// <param name="requestedTag">The culture tag to resolve ("es",
        /// "de-DE"); null or empty means English.</param>
        /// <param name="log">Receives already-prefixed warning text.</param>
        public static void Initialize(Func<string, string> readEmbedded, string languagesRoot, string requestedTag, Action<string> log)
        {
            var store = new LocStore(readEmbedded, languagesRoot, log);
            store.Load(requestedTag);
            Instance = store;
        }

        /// <summary>The text for a key, or the key itself before Initialize.</summary>
        public static string T(string key)
        {
            var store = Instance;
            return store == null ? key : store[key];
        }

        /// <summary>The formatted text for a key, or the key before Initialize.</summary>
        public static string F(string key, params object[] args)
        {
            var store = Instance;
            return store == null ? key : store.F(key, args);
        }

        /// <summary>The plural-aware formatted text, or the key before Initialize.</summary>
        public static string N(string key, int n, params object[] args)
        {
            var store = Instance;
            return store == null ? key : store.N(key, n, args);
        }
    }
}
