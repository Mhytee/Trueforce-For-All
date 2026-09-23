// Guide text, loaded from the .md files embedded in the DLL.
//
// Embedded rather than written to PluginsData for the same reason the iRacing
// firing patterns are: no installer step, no folder to go stale or half-written,
// and a plain three-DLL deploy carries the guides with it.
//
// Authoring a guide is writing Markdown in Guides\<name>.md and adding one line
// to the menu. No C# string concatenation, no escaped newlines, and the file
// reads as the guide reads.

using System;
using System.IO;
using System.Reflection;

namespace TrueforceForAll.Plugin
{
    /// <summary>Where a guide's text is being shown. Two of these files render
    /// both in the help browser and inside the settings panel, and a sentence
    /// that points at a control reads differently depending on whether the
    /// reader is standing in front of it.</summary>
    internal enum GuideContext
    {
        /// <summary>The help browser: the reader is not looking at the panel, so
        /// a control has to be named and located.</summary>
        Guide,
        /// <summary>Rendered into the settings panel itself: the control is on
        /// screen, so naming its tab reads as though it were somewhere else.</summary>
        Panel,
    }

    internal static class GuideText
    {
        /// <summary>Load Guides\<paramref name="name"/>.md. Returns a short
        /// placeholder rather than throwing or showing an empty window if the
        /// resource is missing: a guide that fails to load is a build mistake,
        /// and the reader should see something that says so.</summary>
        /// <param name="ctx">Picks between the two halves of any
        /// {{guide:...|panel:...}} token in the file. See ApplyContext.</param>
        internal static string Load(string name, GuideContext ctx = GuideContext.Guide)
            => ApplyContext(LoadRaw(name), ctx);

        /// <summary>Resolve the context tokens in a guide.
        ///
        /// Syntax: {{guide:seen in the browser|panel:seen in the settings panel}}.
        /// Either half may be omitted, and a half may be empty, which is how a
        /// sentence drops a clause in one context and keeps it in the other.
        /// A literal '|' cannot appear inside a token; nothing needs one.
        ///
        /// This exists because the alternative was writing the text twice, and
        /// the copy that gets forgotten is always the one nobody is looking at.
        /// Deliberately tiny: two named branches, no nesting, no conditions.</summary>
        internal static string ApplyContext(string md, GuideContext ctx)
        {
            if (string.IsNullOrEmpty(md) || md.IndexOf("{{", StringComparison.Ordinal) < 0)
                return md;
            string want = ctx == GuideContext.Panel ? "panel" : "guide";
            var sb = new System.Text.StringBuilder(md.Length);
            int i = 0;
            while (i < md.Length)
            {
                int open = md.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(md, i, md.Length - i); break; }
                int close = md.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) { sb.Append(md, i, md.Length - i); break; }

                sb.Append(md, i, open - i);
                foreach (string part in md.Substring(open + 2, close - open - 2).Split('|'))
                {
                    int colon = part.IndexOf(':');
                    if (colon <= 0) continue;
                    if (string.Equals(part.Substring(0, colon).Trim(), want,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(part.Substring(colon + 1));
                        break;
                    }
                }
                i = close + 2;
            }
            return sb.ToString();
        }

        private static string LoadRaw(string name)
        {
            string resource = "TrueforceForAll.Plugin.Guides." + name + ".md";
            try
            {
                var asm = typeof(GuideText).Assembly;
                using (Stream s = asm.GetManifestResourceStream(resource))
                {
                    if (s == null)
                    {
                        SimHub.Logging.Current.Warn("[TF4ALL] Guide resource missing: " + resource);
                        return "This guide is missing from this build. Please report it.";
                    }
                    using (var r = new StreamReader(s))
                        return r.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TF4ALL] Guide load failed (" + resource + "): " + ex.Message);
                return "This guide could not be loaded.";
            }
        }
    }
}
