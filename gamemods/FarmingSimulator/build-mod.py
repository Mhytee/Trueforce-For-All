# Packages the TF4ALL Telemetry mod: generates the icon DDS (uncompressed
# BGRA8, 256x256, no external tooling needed) and zips modDesc + Lua + icon
# into one zip per supported game generation. FS22 and FS25 share the exact
# same Lua (SimHub's own mod ships an identical file to both); only the
# modDesc descVersion differs, matching what each game accepts (read from
# SimHub's shipped paks: FS22=61, FS25=92). FS19 is a different API vintage
# and is deliberately not built. Run with any Python 3.
import os
import re
import struct
import zipfile

here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(here, "TF4ALLTelemetry")
icon_path = os.path.join(src, "tf4all.dds")

TARGETS = {"FS22": "61", "FS25": "92"}

W = H = 256


def make_icon():
    # DDS header: uncompressed 32-bit BGRA.
    DDSD_FLAGS = 0x1 | 0x2 | 0x4 | 0x1000 | 0x8  # caps|height|width|pixelformat|pitch
    header = struct.pack(
        "<4sII II I II 44x",
        b"DDS ", 124, DDSD_FLAGS, H, W, W * 4, 0, 0)
    pixelformat = struct.pack("<II4sIIIII", 32, 0x41, b"\0\0\0\0", 32,
                              0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)
    caps = struct.pack("<IIIII", 0x1000, 0, 0, 0, 0)
    px = bytearray()
    for y in range(H):
        for x in range(W):
            # Dark blue field with a lighter centered band: reads as a badge
            # in the mod list without pretending to be artwork.
            in_band = 88 <= y <= 168
            edge = x < 10 or x >= W - 10 or y < 10 or y >= H - 10
            if edge:
                b, g, r = 200, 168, 40
            elif in_band:
                b, g, r = 230, 200, 60
            else:
                b, g, r = 90, 55, 18
            px += bytes((b, g, r, 255))
    with open(icon_path, "wb") as f:
        f.write(header + pixelformat + caps + bytes(px))


def make_zips():
    with open(os.path.join(src, "modDesc.xml"), encoding="utf-8") as f:
        desc_template = f.read()
    for tag, desc_version in TARGETS.items():
        out_zip = os.path.join(here, "TF4ALLTelemetry_%s.zip" % tag)
        desc = re.sub(r'descVersion="\d+"', 'descVersion="%s"' % desc_version,
                      desc_template, count=1)
        with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("modDesc.xml", desc)
            for name in ("TF4ALLTelemetry.lua", "tf4all.dds"):
                z.write(os.path.join(src, name), name)
        print("wrote", out_zip)


make_icon()
make_zips()
