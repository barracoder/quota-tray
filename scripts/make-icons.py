#!/usr/bin/env python3
"""Regenerate src/QuotaTray.App/assets/{quota-tray.png,quota-tray.ico,QuotaTray.icns}.

Only needed when the icon design changes; the outputs are committed.
Requires Pillow (pip install pillow) and, for the .icns, macOS iconutil.
"""
import math
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "QuotaTray.App", "assets")
SIZE = 1024
SS = 4  # supersample


def render(size: int) -> Image.Image:
    s = size * SS
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    # macOS-style rounded square, dark slate.
    pad = int(s * 0.06)
    radius = int(s * 0.22)
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=radius, fill=(30, 41, 59, 255))
    # Ring: grey track, blue arc for 82% remaining, starting at 12 o'clock clockwise.
    stroke = int(s * 0.11)
    inset = int(s * 0.24)
    box = [inset, inset, s - inset, s - inset]
    # Track on its own layer so the alpha composites over the slate instead of replacing it.
    track = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(track).ellipse(box, outline=(148, 163, 184, 90), width=stroke)
    im.alpha_composite(track)
    d = ImageDraw.Draw(im)
    d.arc(box, start=-90, end=-90 + 360 * 0.82, fill=(59, 130, 246, 255), width=stroke)
    # Round caps on the arc.
    r = (s - 2 * inset) / 2
    cx = cy = s / 2
    for ang in (-90, -90 + 360 * 0.82):
        x = cx + r * math.cos(math.radians(ang))
        y = cy + r * math.sin(math.radians(ang))
        d.ellipse([x - stroke / 2, y - stroke / 2, x + stroke / 2, y + stroke / 2], fill=(59, 130, 246, 255))
    return im.resize((size, size), Image.LANCZOS)


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    master = render(SIZE)
    master.save(os.path.join(OUT, "quota-tray.png"))
    master.save(os.path.join(OUT, "quota-tray.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    if shutil.which("iconutil") is None:
        print("iconutil not found; skipping .icns (run on macOS)")
        return 0
    with tempfile.TemporaryDirectory() as tmp:
        iconset = os.path.join(tmp, "QuotaTray.iconset")
        os.mkdir(iconset)
        for base in (16, 32, 128, 256, 512):
            render(base).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
            render(base * 2).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
        subprocess.check_call(["iconutil", "-c", "icns", iconset, "-o", os.path.join(OUT, "QuotaTray.icns")])
    print("wrote", sorted(os.listdir(OUT)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
