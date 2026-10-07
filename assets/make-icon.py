"""Renders assets/logo.svg's shapes to PNG and ICO. Needs Pillow: pip install pillow

The geometry is repeated here (not parsed from the SVG) so the script has no SVG dependency.
Keep the two in sync when the logo changes.
"""
from pathlib import Path
from PIL import Image, ImageDraw

HERE = Path(__file__).parent
BOARD, TRACE, GOLD, WHITE = "#0F3B2E", "#2E7D62", "#E2B755", "#E8F3EC"


def render(size: int) -> Image.Image:
    scale = 8  # supersample, then shrink for smooth edges
    px = size * scale
    k = px / 64
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def pt(x, y):
        return (x * k, y * k)

    def line(points, color, width):
        w = max(1, round(width * k))
        d.line([pt(*p) for p in points], fill=color, width=w, joint="curve")
        for x, y in (points[0], points[-1]):  # round caps
            r = w / 2
            d.ellipse([x * k - r, y * k - r, x * k + r, y * k + r], fill=color)

    def dot(x, y, r, color):
        d.ellipse([(x - r) * k, (y - r) * k, (x + r) * k, (y + r) * k], fill=color)

    d.rounded_rectangle([pt(4, 4), pt(60, 60)], radius=13 * k, fill=BOARD)
    # Tiny sizes keep only the prompt: the traces turn to noise below 24 px.
    if size >= 24:
        line([(4, 18), (14, 18), (20, 24)], TRACE, 2.5)
        line([(60, 46), (50, 46), (44, 40)], TRACE, 2.5)
        line([(18, 60), (18, 50)], TRACE, 2.5)
        line([(46, 4), (46, 14)], TRACE, 2.5)
        for x, y in ((21, 25), (43, 39), (18, 49), (46, 15)):
            dot(x, y, 3, GOLD)
        line([(22, 33), (30, 39), (22, 45)], WHITE, 4.5)
        line([(34, 45), (43, 45)], GOLD, 4.5)
    else:
        line([(18, 22), (31, 32), (18, 42)], WHITE, 7)
        line([(35, 43), (48, 43)], GOLD, 7)
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    render(256).save(HERE / "logo.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [render(s) for s in sizes]
    frames[-1].save(HERE / "app.ico", format="ICO", append_images=frames[:-1], sizes=[(s, s) for s in sizes])
    print("wrote logo.png and app.ico")
