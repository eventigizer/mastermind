"""Draw Mastermind's icon — an indigo cobweb hung from the bottom-right corner of a transparent square — and write
mastermind.ico.

No tile, frame or padding: the tray draws 16 px at 100 % scaling, and the threads get every pixel of it. Every size is
drawn on its own pixel grid, never resampled from a larger one: a thread downscaled to a tray size blurs away, and the
tray is where the icon has to read.

Usage:
  python make_mastermind_icon.py <out_dir>                 writes <out_dir>/mastermind.ico
  python make_mastermind_icon.py <out_dir> --preview <png> [--beside <icon.png>]
      also writes a sheet of the tray sizes on a dark and a light taskbar, zoomed so the real pixels show — with
      --beside, another app's icon next to each, as it will sit in the tray
  python make_mastermind_icon.py <out_dir> --png <png>
      also writes the store's 128 px icon, drawn on its own grid like every other size
"""
import argparse
import math
import os

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]   # tray at 100/125/150 % DPI, Explorer, Alt-Tab, the shortcut
INDIGO = (130, 128, 255, 255)               # a light indigo: 1 px threads of a darker one fade on a dark taskbar
THREADS = 4                                 # fanning from the corner, leftward to upward


def point(s, k, a):
    """The pixel k px from the bottom-right corner along angle a — 0 runs leftward, pi/2 upward."""
    c = s - 1
    return round(c - k * math.cos(a)), round(c - k * math.sin(a))


def draw(s):
    """The cobweb at size s: the threads fan out of the corner, and between them hang arcs sagging toward it — two
    at the tray sizes, three from 32 px up, where there is room. Everything off the threads is transparent."""
    web = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(web)
    w = max(1, round(s / 28))               # one pixel through every tray size; thicker from 48 px
    gap = (math.pi / 2) / (THREADS - 1)
    angles = [i * gap for i in range(THREADS)]
    for a in angles:
        d.line([point(s, 0, a), point(s, s - 1, a)], fill=255, width=w)
    arcs = 2 if s < 32 else 3
    steps = 4 if s < 32 else 16             # a tray arc has pixels for four bends; a large one would show kinks
    for n in range(1, arcs + 1):
        r = (s - 2) * n / arcs
        pts = []
        for a in angles[:-1]:
            for j in range(steps):
                t = j / steps
                pts.append(point(s, r * (1 - 0.2 * math.sin(math.pi * t)), a + t * gap))
        pts.append(point(s, r, angles[-1]))
        d.line(pts, fill=255, width=w, joint="curve")
    im = Image.new("RGBA", (s, s), INDIGO)
    im.putalpha(web)
    return im


def preview(path, frames, beside=None):
    """The icon at the three tray sizes (100 / 125 / 150 % scaling), on a dark and a light taskbar, zoomed 6x
    (nearest) so the real pixels show — with `beside`, that icon next to each, scaled as Windows would."""
    other = Image.open(beside).convert("RGBA") if beside else None
    tray = [16, 20, 24]
    gap = 8
    per = 2 if other else 1
    width = gap + sum((t + gap) * per + gap for t in tray)
    rows = []
    for bg in ((31, 31, 31, 255), (243, 243, 243, 255)):
        row = Image.new("RGBA", (width, 24 + 2 * gap), bg)
        x = gap
        for t in tray:
            for icon in ([other.resize((t, t), Image.LANCZOS)] if other else []) + [frames[t]]:
                row.alpha_composite(icon, (x, gap + (24 - t) // 2))
                x += t + gap
            x += gap
        rows.append(row)
    sheet = Image.new("RGBA", (width, sum(r.height for r in rows)))
    y = 0
    for r in rows:
        sheet.paste(r, (0, y))
        y += r.height
    sheet.resize((sheet.width * 6, sheet.height * 6), Image.NEAREST).save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("--preview")
    ap.add_argument("--beside")
    ap.add_argument("--png")
    args = ap.parse_args()
    frames = {s: draw(s) for s in SIZES}
    os.makedirs(args.out_dir, exist_ok=True)
    largest = frames[SIZES[-1]]
    # BMP frames, never Pillow's default PNG ones: System.Drawing.Icon and the exe's Win32 icon resource are the
    # readers, and a 32-bit DIB is the frame every Windows icon reader takes.
    largest.save(os.path.join(args.out_dir, "mastermind.ico"), sizes=[(s, s) for s in SIZES],
                 append_images=[frames[s] for s in SIZES[:-1]], bitmap_format="bmp")
    if args.png:
        draw(128).save(args.png)
    if args.preview:
        preview(args.preview, frames, args.beside)


if __name__ == "__main__":
    main()
