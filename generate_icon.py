"""One-shot icon generator: tt-chat.ico — an FF1-styled royal-blue chat bubble
with a blocky white "TT" (Tequilas' Tavern chat)."""
from PIL import Image, ImageDraw

OUT = "tt-chat.ico"
SIZES = [16, 24, 32, 48, 64, 128, 256]
MASTER = 1024

NAVY_TOP = (20, 36, 192, 255)     # #1424C0
NAVY_BOT = (10, 22, 160, 255)     # #0A16A0
WHITE = (255, 255, 255, 255)
INNER = (128, 144, 232, 255)      # #8090E8
GOLD = (248, 216, 120, 255)       # #F8D878


def render(s: int) -> Image.Image:
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    m = s * 0.04                    # outer margin
    bw = max(2, round(s * 0.055))   # white border width
    r = s * 0.18                    # corner radius

    # Bubble body (leave the bottom fifth for the tail)
    bx0, by0 = m, m
    bx1, by1 = s - m, s * 0.78

    # Vertical gradient fill via horizontal strips, clipped by a rounded-rect mask.
    mask = Image.new("L", (s, s), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle((bx0, by0, bx1, by1), radius=r, fill=255)
    # tail: triangle hanging below the bubble, apex tucked under the left side
    tail = [(s * 0.28, by1 - s * 0.02), (s * 0.52, by1 - s * 0.02), (s * 0.32, s * 0.96)]
    md.polygon(tail, fill=255)

    grad = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / max(1, s - 1)
        col = tuple(round(NAVY_TOP[i] + (NAVY_BOT[i] - NAVY_TOP[i]) * t) for i in range(4))
        gd.line([(0, y), (s, y)], fill=col)
    img.paste(grad, (0, 0), mask)

    # White outer border: bubble first, then the tail's two outer edges, then
    # re-open the seam where the tail meets the bubble so they read as one shape.
    d.rounded_rectangle((bx0, by0, bx1, by1), radius=r, outline=WHITE, width=bw)
    seam_x0 = tail[0][0] + bw * 0.9
    seam_x1 = tail[1][0] - bw * 0.9
    d.rectangle((seam_x0, by1 - bw, seam_x1, by1 + 1), fill=NAVY_BOT)
    d.line([tail[0], tail[2]], fill=WHITE, width=bw)
    d.line([tail[1], tail[2]], fill=WHITE, width=bw)

    # Thin inner light-blue border (FF1 double-border look)
    ib = max(1, bw // 2)
    inset = bw + max(1, round(s * 0.02))
    d.rounded_rectangle((bx0 + inset, by0 + inset, bx1 - inset, by1 - inset),
                        radius=max(1, r - inset), outline=INNER, width=ib)

    # Blocky "TT" drawn as rectangles (crisp at small sizes)
    cx = (bx0 + bx1) / 2
    cy = (by0 + by1) / 2 + s * 0.01
    th = (by1 - by0) * 0.44          # letter height
    stem = max(2, round(s * 0.085))  # stroke width
    barw = th * 0.72                 # top-bar width
    gap = s * 0.055                  # gap between letters

    for k, lx in enumerate((cx - gap / 2 - barw / 2, cx + gap / 2 + barw / 2)):
        top = cy - th / 2
        col = WHITE if k == 0 else GOLD
        # top bar
        d.rectangle((lx - barw / 2, top, lx + barw / 2, top + stem), fill=col)
        # stem
        d.rectangle((lx - stem / 2, top, lx + stem / 2, cy + th / 2), fill=col)

    return img


master = render(MASTER)
imgs = [master.resize((z, z), Image.LANCZOS) for z in SIZES]
imgs[-1].save(OUT, sizes=[(z, z) for z in SIZES], append_images=imgs[:-1])
print("wrote", OUT)
