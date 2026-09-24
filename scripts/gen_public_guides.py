#!/usr/bin/env python3
"""Generate the public guides/ folder from the guides the plugin ships.

The in-app guides are the source of truth: they are embedded in the plugin
and rendered by its own viewer, which understands three things GitHub does
not.

    [label](guide:key)          a cross-reference to another guide
    [label](tab:key)            a jump to a tab in the settings panel
    {{guide:A|panel:B}}         A in the guide browser, B in the panel

Published raw, those read as dead links and literal braces. So the public
copy is generated: cross-references become relative .md links, panel jumps
become plain text, and the context tokens resolve to their guide half.

One source, one command, no second copy to keep in step by hand:

    python scripts/gen_public_guides.py

It rewrites guides/ in place and prints what changed. Run it after editing
anything under src/TrueforceForAll.Plugin/Guides.
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "TrueforceForAll.Plugin", "Guides")
OUT = os.path.join(ROOT, "guides")

# Titles and grouping mirror SettingsControl.Guides.cs, which is what the
# in-app browser lists. Anything not named here still gets published; it
# just lands under "More" with a title made from its file name.
TITLES = [
    ("Setup", [
        ("iracing-setup", "iRacing"),
        ("raceroom-setup", "RaceRoom"),
        ("lmu-setup", "Le Mans Ultimate"),
        ("assetto-corsa-setup", "Assetto Corsa"),
        ("csp-bridge", "The TF4ALL CSP Bridge"),
        ("forza-setup", "Forza"),
        ("forza-forward", "Forwarding Forza telemetry to SimHub"),
        ("farming-sim", "Farming Simulator"),
        ("usbpcap", "USBPcap, and why it is needed"),
        ("simhub-license", "SimHub's license and telemetry rates"),
    ]),
    ("Force feedback", [
        ("normal-ffb", "How the force reaches your wheel"),
        ("force-handover", "The force handover"),
        ("telemetry-ffb", "Telemetry Based FFB"),
        ("tuning-effects", "Tuning the effects"),
        ("weak-effects", "When the effects feel weak"),
        ("ffb-not-working", "When there is no force feedback"),
        ("native-trueforce", "Games with their own Trueforce"),
    ]),
    ("Lights and screen", [
        ("light-patterns", "Light patterns"),
        ("force-and-lights", "Why lights and force share a channel"),
        ("wheel-screen", "The wheel's screen"),
    ]),
    ("The rest", [
        ("dash", "The TF4ALL Dash"),
        ("home-tile", "The SimHub home tile"),
        ("car-facts", "Car facts"),
        ("lovely-car-data", "Lovely Sim Racing car data"),
        ("backup-sync", "Backup and sync"),
        ("bindings", "Putting controls on your wheel"),
        ("anti-cheat", "Anti-cheat safety"),
    ]),
]

HEADER = ("<!-- Generated from src/TrueforceForAll.Plugin/Guides by "
          "scripts/gen_public_guides.py. Edit the source, not this file. -->\n\n")


def yaml_str(v):
    """Always quote. A title holding a colon or an apostrophe is otherwise a
    YAML landmine, and the build that breaks is on a runner, not here."""
    return '"' + v.replace("\\", "\\\\").replace('"', '\\"') + '"' 


def resolve_tokens(text):
    """{{guide:A|panel:B}} -> A. Either half may be missing or empty."""
    def one(m):
        body = m.group(1)
        parts = dict()
        for half in body.split("|"):
            if ":" in half:
                k, v = half.split(":", 1)
                parts[k.strip()] = v
            elif half.strip():
                parts["guide"] = half
        return parts.get("guide", "")
    return re.sub(r"\{\{(.*?)\}\}", one, text, flags=re.S)


def rewrite_links(text, known):
    """guide:key -> key.md (when it exists), tab:key -> plain label."""
    def guide_link(m):
        label, key = m.group(1), m.group(2)
        return "[%s](%s.md)" % (label, key) if key in known else label
    text = re.sub(r"\[([^\]]+)\]\(guide:([a-z0-9-]+)\)", guide_link, text)
    # A panel jump has no web equivalent: keep the words, drop the link.
    text = re.sub(r"\[([^\]]+)\]\(tab:[a-z0-9-]+\)", r"\1", text)
    return text


def title_for(key, titled):
    if key in titled:
        return titled[key]
    return key.replace("-", " ").capitalize()


def main():
    if not os.path.isdir(SRC):
        print("no guides at " + SRC)
        return 1
    keys = sorted(f[:-3] for f in os.listdir(SRC) if f.endswith(".md"))
    known = set(keys)
    titled = {k: t for _, entries in TITLES for k, t in entries}

    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    written = []
    for key in keys:
        raw = io.open(os.path.join(SRC, key + ".md"), encoding="utf-8").read()
        body = rewrite_links(resolve_tokens(raw), known).strip() + "\n"
        text = HEADER + "# " + title_for(key, titled) + "\n\n" + body
        path = os.path.join(OUT, key + ".md")
        old = io.open(path, encoding="utf-8").read() if os.path.exists(path) else None
        if old != text:
            io.open(path, "w", encoding="utf-8", newline="\n").write(text)
            written.append(key + ".md")

    # The index, in the browser's own order, with anything new at the end.
    lines = [HEADER.rstrip("\n"), "", "# TF4ALL guides", "",
             "The same guides the plugin shows under the **?** in its panel.",
             "They are generated from the plugin's own copies, so they never",
             "drift from what you see in the app.", ""]
    listed = set()
    for group, entries in TITLES:
        rows = [(k, t) for k, t in entries if k in known]
        if not rows:
            continue
        lines.append("## " + group)
        lines.append("")
        for k, t in rows:
            lines.append("- [%s](%s.md)" % (t, k))
            listed.add(k)
        lines.append("")
    rest = [k for k in keys if k not in listed]
    if rest:
        lines.append("## More")
        lines.append("")
        for k in rest:
            lines.append("- [%s](%s.md)" % (title_for(k, titled), k))
        lines.append("")
    index = "\n".join(lines)

    # The site's sidebar, from the same grouping the index just used, so the
    # two can never disagree. Jekyll reads guides/_data/nav.yml on build.
    nav = ["# Generated by scripts/gen_public_guides.py. Do not edit by hand.",
           "# The sidebar on the published guides site is built from this."]
    for group, entries in TITLES:
        rows = [(k, t) for k, t in entries if k in known]
        if not rows:
            continue
        nav.append("- group: %s" % yaml_str(group))
        nav.append("  items:")
        for k, t in rows:
            nav.append("      - key: %s" % yaml_str(k))
            nav.append("        title: %s" % yaml_str(t))
    if rest:
        nav.append("- group: More")
        nav.append("  items:")
        for k in rest:
            nav.append("      - key: %s" % yaml_str(k))
            nav.append("        title: %s" % yaml_str(title_for(k, titled)))
    navdir = os.path.join(OUT, "_data")
    if not os.path.isdir(navdir):
        os.makedirs(navdir)
    npath = os.path.join(navdir, "nav.yml")
    navtext = "\n".join(nav) + "\n"
    old_nav = io.open(npath, encoding="utf-8").read() if os.path.exists(npath) else None
    if old_nav != navtext:
        io.open(npath, "w", encoding="utf-8", newline="\n").write(navtext)
        written.append("_data/nav.yml")

    ipath = os.path.join(OUT, "README.md")
    old = io.open(ipath, encoding="utf-8").read() if os.path.exists(ipath) else None
    if old != index:
        io.open(ipath, "w", encoding="utf-8", newline="\n").write(index)
        written.append("README.md")

    # A guide that vanished upstream should vanish here too.
    stale = [f for f in os.listdir(OUT)
             if f.endswith(".md") and f != "README.md" and f[:-3] not in known]
    for f in stale:
        os.remove(os.path.join(OUT, f))

    print("guides: %d published, %d written, %d removed"
          % (len(keys), len(written), len(stale)))
    for f in written:
        print("  " + f)
    return 0


if __name__ == "__main__":
    sys.exit(main())
