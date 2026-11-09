"""Builds assets/icon/standmate.ico: the kanji 立 ("stand") from the Yu Gothic
font on a rounded square. Plain typography - no generated artwork.

Usage: python tools/make_icon.py   (needs Pillow and Windows' Yu Gothic font)
"""
import os
from PIL import Image, ImageDraw, ImageFont

ACCENT = (227, 154, 174, 255)   # #E39AAE, the app's sakura accent
INK = (28, 29, 34, 255)         # #1C1D22, the app's background
FONT = r"C:\Windows\Fonts\YuGothB.ttc"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = os.path.join(os.path.dirname(__file__), "..", "assets", "icon", "standmate.ico")


def render(size: int) -> Image.Image:
    scale = 8  # supersample, then downscale for smooth edges
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=ACCENT)
    font = ImageFont.truetype(FONT, int(s * 0.78))
    l, t, r, b = d.textbbox((0, 0), "立", font=font)
    d.text(((s - (r - l)) / 2 - l, (s - (b - t)) / 2 - t), "立", font=font, fill=INK)
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    images = [render(sz) for sz in SIZES]
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    images[-1].save(OUT, format="ICO", sizes=[(sz, sz) for sz in SIZES], append_images=images[:-1])
    print("wrote", os.path.abspath(OUT))
