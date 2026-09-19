"""
glass_ui.py

Shared "glass" look for every DAAMS desktop dialog (PIN prompt,
classification picker, loading window, message boxes).

Why this module exists
----------------------
Tkinter has no real transparency or backdrop blur, so a glass UI can't
be built from normal widgets. Instead each window is RENDERED with
Pillow:
    1. A near-black slate base (--bg) with four soft radial colour blobs.
    2. A glass card: that background is blurred (20px) and saturated
       (160%) -- the same as CSS `backdrop-filter: blur(20px)
       saturate(160%)` -- then tinted with --surface and given a
       hairline --border.
    3. Buttons / fields / rows are rendered from a crop of that same
       background, so they stay translucent instead of flat grey.
The result is shown on a single Tk Canvas; text is drawn on top with
canvas text items, and the PIN field is a real Entry sitting on a
rendered glass field.

Design tokens (colours, fonts, risk / classification palettes) all live
at the top of this file, so restyling is a one-place change.

Requires: Pillow  (pip install Pillow)

Preview every dialog without touching Explorer:
    python glass_ui.py
"""

import math
import sys
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
from functools import partial

from PIL import Image, ImageChops, ImageDraw, ImageEnhance, ImageFilter, ImageTk

# ---------------------------------------------------------------------------
# DESIGN TOKENS
# ---------------------------------------------------------------------------
BG = "#0D1015"
TEXT = "#E4E7EC"
TEXT_DIM = "#9198AA"
ACCENT = "#4C8DFF"
ACCENT_DIM = "#25406E"

# (rgb, alpha) -- glass surfaces
SURFACE = ((23, 29, 38), 0.52)
SURFACE_ALT = ((31, 39, 50), 0.55)
BORDER_ALPHA = 0.08          # rgba(255,255,255,.08)
GLASS_BLUR = 20              # blur(20px)
GLASS_SATURATE = 1.6         # saturate(160%)

# Background glow blobs: (x fraction, y fraction, radius as fraction of the
# window's longer side, rgb, peak alpha)
BLOBS = [
    (0.08, 0.04, 0.85, (76, 141, 255), 0.30),   # blue   -- top left
    (0.96, 0.98, 0.80, (225, 83, 97), 0.22),    # red    -- bottom right
    (0.04, 0.96, 0.70, (63, 182, 139), 0.16),   # green  -- bottom left
    (0.98, 0.06, 0.60, (217, 165, 68), 0.14),   # amber  -- top right
]

# (foreground, background)
RISK_COLORS = {
    "Low": ("#3FB68B", "#15271f"),
    "Medium": ("#D9A544", "#2c2410"),
    "High": ("#E2793F", "#2c1c0f"),
    "Critical": ("#E15361", "#2c1216"),
}
CLASSIFICATION_COLORS = {
    "Public": ("#6B7A99", "#171d29"),
    "Sensitive": ("#4C8DFF", "#101c33"),
    "Private": ("#9B6BE0", "#1c1730"),
    "Confidential": ("#E15361", "#2c1216"),
}

# Icon / status colours per message kind.
KIND_COLORS = {
    "success": RISK_COLORS["Low"][0],
    "warning": RISK_COLORS["Medium"][0],
    "error": RISK_COLORS["Critical"][0],
    "info": ACCENT,
    "shield": ACCENT,
    "lock": ACCENT,
}

# First installed family wins; the last entry of each list is the fallback.
SANS_CANDIDATES = ["IBM Plex Sans", "Segoe UI Variable Text", "Segoe UI", "Helvetica"]
MONO_CANDIDATES = ["IBM Plex Mono", "Cascadia Mono", "Consolas", "Courier New", "Courier"]

_families = {}


# ---------------------------------------------------------------------------
# SMALL HELPERS
# ---------------------------------------------------------------------------
def _rgb(hex_color: str) -> tuple:
    h = hex_color.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def _hex(rgb) -> str:
    return "#%02x%02x%02x" % tuple(rgb[:3])


def _mix(a, b, t: float) -> tuple:
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def _resolve_families(root) -> dict:
    if not _families:
        available = set(tkfont.families(root))
        _families["sans"] = next((f for f in SANS_CANDIDATES if f in available), "TkDefaultFont")
        _families["mono"] = next((f for f in MONO_CANDIDATES if f in available), "TkFixedFont")
    return _families


def _wrap(text: str, font, max_width: int, chars: bool = False) -> list:
    """Wrap text to a pixel width. chars=True breaks anywhere (for paths)."""
    lines = []
    for para in text.split("\n"):
        if para == "":
            lines.append("")
            continue
        cur = ""
        if chars:
            for ch in para:
                if cur and font.measure(cur + ch) > max_width:
                    lines.append(cur)
                    cur = ch
                else:
                    cur += ch
        else:
            for word in para.split(" "):
                if font.measure(word) > max_width:
                    # A single "word" wider than the line (e.g. a long path):
                    # flush what we have, then break the word by character.
                    if cur:
                        lines.append(cur)
                    chunks = _wrap(word, font, max_width, chars=True)
                    lines.extend(chunks[:-1])
                    cur = chunks[-1]
                    continue
                trial = word if not cur else cur + " " + word
                if not cur or font.measure(trial) <= max_width:
                    cur = trial
                else:
                    lines.append(cur)
                    cur = word
        lines.append(cur)
    return lines


# ---------------------------------------------------------------------------
# PILLOW RENDERING
# ---------------------------------------------------------------------------
def _rounded_mask(w: int, h: int, r: int, inset: int = 0, ss: int = 4) -> Image.Image:
    """Anti-aliased rounded-rectangle mask (drawn 4x, then downsampled)."""
    m = Image.new("L", (w * ss, h * ss), 0)
    ImageDraw.Draw(m).rounded_rectangle(
        (inset * ss, inset * ss, (w - inset) * ss - 1, (h - inset) * ss - 1),
        radius=max(0, r - inset) * ss,
        fill=255,
    )
    return m.resize((w, h), Image.Resampling.LANCZOS)


def _shape(base: Image.Image, r: int, fill=None, fill_alpha: float = 1.0,
           border=None, border_alpha: float = BORDER_ALPHA) -> Image.Image:
    """Return `base` with a translucent rounded fill and hairline border on top."""
    w, h = base.size
    out = base.copy()
    outer = _rounded_mask(w, h, r)
    if fill is not None:
        out.paste(fill, (0, 0), outer.point(lambda v: int(v * fill_alpha)))
    if border is not None:
        ring = ImageChops.subtract(outer, _rounded_mask(w, h, r, inset=1))
        out.paste(border, (0, 0), ring.point(lambda v: int(v * border_alpha)))
    return out


def _blob_mask(d: int, alpha: float) -> Image.Image:
    g = Image.radial_gradient("L").resize((d, d), Image.Resampling.BILINEAR)

    def curve(v):
        t = 1 - v / 255
        return int(255 * alpha * t * t * (3 - 2 * t))   # smoothstep falloff

    return g.point(curve)


def _render_background(w: int, h: int) -> Image.Image:
    base = Image.new("RGB", (w, h), _rgb(BG))
    span = max(w, h)
    for fx, fy, fr, rgb, alpha in BLOBS:
        r = int(span * fr)
        base.paste(rgb, (int(w * fx) - r, int(h * fy) - r), _blob_mask(2 * r, alpha))
    return base


def _glass_panel(img: Image.Image, blurred: Image.Image, box: tuple, radius: int) -> None:
    """Frosted-glass card: blurred+saturated backdrop, surface tint, border, faint sheen."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    outer = _rounded_mask(w, h, radius)
    img.paste(blurred.crop(box), (x0, y0), outer)

    tile = _shape(img.crop(box), radius, fill=SURFACE[0], fill_alpha=SURFACE[1],
                  border=(255, 255, 255), border_alpha=BORDER_ALPHA)

    grad = Image.linear_gradient("L").resize((w, h), Image.Resampling.BILINEAR)
    sheen = grad.point(lambda v: int(max(0, 255 - v * 2.2) * 0.07))
    tile.paste((255, 255, 255), (0, 0), ImageChops.multiply(sheen, outer))
    img.paste(tile, (x0, y0))


def _glyph_mask(kind: str, size: int, stroke: float = 2.2) -> Image.Image:
    ss = 4
    s = size * ss
    m = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(m)
    wpx = int(stroke * ss)

    def p(x, y):
        return (x * s, y * s)

    def dot(cx, cy, r):
        d.ellipse([p(cx - r, cy - r), p(cx + r, cy + r)], fill=255)

    def line(pts):
        d.line([p(*pt) for pt in pts], fill=255, width=wpx, joint="curve")
        cap = wpx / 2 / s
        dot(*pts[0], cap)
        dot(*pts[-1], cap)

    if kind == "shield":
        line([(.5, .17), (.79, .29), (.79, .53), (.5, .84), (.21, .53), (.21, .29), (.5, .17)])
    elif kind == "lock":
        d.rounded_rectangle([p(.29, .45), p(.71, .77)], radius=.06 * s, outline=255, width=wpx)
        d.arc([p(.36, .20), p(.64, .64)], 180, 360, fill=255, width=wpx)
        line([(.36, .42), (.36, .47)])
        line([(.64, .42), (.64, .47)])
    elif kind == "success":
        line([(.29, .53), (.44, .67), (.72, .35)])
    elif kind == "error":
        line([(.34, .34), (.66, .66)])
        line([(.66, .34), (.34, .66)])
    elif kind == "warning":
        line([(.5, .26), (.5, .56)])
        dot(.5, .71, .05)
    else:  # info
        dot(.5, .30, .05)
        line([(.5, .44), (.5, .72)])
    return m.resize((size, size), Image.Resampling.LANCZOS)


def _bake_icon(img: Image.Image, x: int, y: int, size: int, kind: str) -> None:
    color = _rgb(KIND_COLORS.get(kind, ACCENT))
    box = (x, y, x + size, y + size)
    tile = _shape(img.crop(box), 12, fill=color, fill_alpha=0.16, border=color, border_alpha=0.38)
    tile.paste(color, (0, 0), _glyph_mask(kind, size))
    img.paste(tile, (x, y))


def _pill_image(w: int = 96, h: int = 6, pad: int = 14) -> Image.Image:
    """Glowing accent pill (RGBA) used by the loading bar."""
    size = (w + 2 * pad, h + 2 * pad)
    glow = Image.new("L", size, 0)
    ImageDraw.Draw(glow).rounded_rectangle((pad, pad, pad + w, pad + h), radius=h // 2, fill=255)
    glow = glow.filter(ImageFilter.GaussianBlur(6)).point(lambda v: int(v * 0.55))
    core = Image.new("L", size, 0)
    core.paste(_rounded_mask(w, h, h // 2), (pad, pad))
    im = Image.new("RGBA", size, _rgb(ACCENT) + (0,))
    im.putalpha(ImageChops.lighter(glow, core))
    return im


def _style_titlebar(root) -> None:
    """Dark title bar matching --bg (Windows 10 20H1+ / Windows 11). Best effort."""
    if sys.platform != "win32":
        return
    try:
        import ctypes

        root.update_idletasks()
        user32, dwm = ctypes.windll.user32, ctypes.windll.dwmapi
        user32.GetParent.argtypes = [ctypes.c_void_p]
        user32.GetParent.restype = ctypes.c_void_p
        dwm.DwmSetWindowAttribute.argtypes = [ctypes.c_void_p, ctypes.c_uint,
                                              ctypes.c_void_p, ctypes.c_uint]
        hwnd = user32.GetParent(root.winfo_id()) or root.winfo_id()

        def set_attr(attr, value):
            v = ctypes.c_int(value)
            dwm.DwmSetWindowAttribute(hwnd, attr, ctypes.byref(v), ctypes.sizeof(v))

        set_attr(20, 1)   # DWMWA_USE_IMMERSIVE_DARK_MODE
        set_attr(19, 1)   # same flag on older Windows 10 builds
        r, g, b = _rgb(BG)
        set_attr(35, (b << 16) | (g << 8) | r)      # DWMWA_CAPTION_COLOR
        r, g, b = _rgb(TEXT)
        set_attr(36, (b << 16) | (g << 8) | r)      # DWMWA_TEXT_COLOR
    except Exception:
        pass  # purely cosmetic -- never let it break a dialog


# ---------------------------------------------------------------------------
# THE WINDOW
# ---------------------------------------------------------------------------
class GlassWindow:
    """
    Builds one glass dialog top-to-bottom. Call the element methods in
    order (header, text, code_block, field, options, progress, buttons),
    then show(). Layout is measured up front, so the window is exactly
    as tall as its content.
    """

    M = 14          # margin between window edge and glass card
    PAD = 24        # padding inside the card
    RADIUS = 18

    def __init__(self, width: int = 440, title: str = "DAAMS", closable: bool = True):
        self.root = tk.Tk()
        self.root.withdraw()
        self.root.title(title)
        self.root.resizable(False, False)
        self.root.configure(bg=BG)

        fam = _resolve_families(self.root)

        def font(kind, size, weight="normal"):
            return tkfont.Font(root=self.root, family=fam[kind], size=size, weight=weight)

        self.fonts = {
            "title": font("sans", 13, "bold"),
            "body": font("sans", 10),
            "small": font("sans", 9),
            "btn": font("sans", 10, "bold"),
            "mono": font("mono", 9),
            "eyebrow": font("mono", 8),
            "field": font("mono", 13),
        }

        self.W = width
        self.x0 = self.M + self.PAD
        self.x1 = width - self.M - self.PAD
        self.cw = self.x1 - self.x0
        self.y = self.M + self.PAD

        self.closable = closable
        self.result = None
        self._closed = False
        self._count = 0
        self._bakes = []
        self._draws = []
        self._loops = []
        self._after_ids = {}
        self._refs = []
        self._entry = None
        self._on_enter = None

    # ---- layout plumbing --------------------------------------------------
    def _reserve(self, h: int, gap: int = 14) -> int:
        if self._count:
            self.y += gap
        self._count += 1
        y0 = self.y
        self.y += h
        return y0

    def _photo(self, image: Image.Image):
        photo = ImageTk.PhotoImage(image, master=self.root)
        self._refs.append(photo)
        return photo

    def _linespace(self, font_key: str) -> int:
        return self.fonts[font_key].metrics("linespace")

    # ---- elements ---------------------------------------------------------
    def header(self, eyebrow: str, title: str, icon: str = "shield") -> None:
        size = 40
        y0 = self._reserve(size + 17, gap=0)

        def bake(img):
            _bake_icon(img, self.x0, y0, size, icon)
            line = Image.new("L", (self.cw, 1), int(255 * BORDER_ALPHA))
            img.paste((255, 255, 255), (self.x0, y0 + size + 16), line)

        def draw():
            tx = self.x0 + size + 14
            self.canvas.create_text(tx, y0 + 3, text=eyebrow.upper(), anchor="nw",
                                    font=self.fonts["eyebrow"], fill=TEXT_DIM, state="disabled")
            self.canvas.create_text(tx, y0 + 18, text=title, anchor="nw",
                                    font=self.fonts["title"], fill=TEXT, state="disabled")

        self._bakes.append(bake)
        self._draws.append(draw)
        self._count = 1  # next element gets a normal gap (the divider already spaced this one)
        self.y += 2

    def text(self, text: str, style: str = "body", gap: int = 14) -> None:
        font = self.fonts["small" if style == "small" else "body"]
        color = TEXT if style == "body" else TEXT_DIM
        lines = _wrap(text, font, self.cw)
        y0 = self._reserve(len(lines) * self._linespace("small" if style == "small" else "body"), gap)
        self._draws.append(lambda: self.canvas.create_text(
            self.x0, y0, text="\n".join(lines), anchor="nw", font=font, fill=color, state="disabled"))

    def code_block(self, text: str, max_lines: int = 3, truncate: str = "middle") -> None:
        """Mono text on an inset glass chip -- used for paths, IDs and error details."""
        font = self.fonts["mono"]
        inner = self.cw - 28
        cpl = max(8, inner // max(1, font.measure("0")))

        if truncate == "middle" and len(text) > cpl * max_lines:
            keep = cpl * max_lines - 1
            head = keep // 3
            text = text[:head] + "…" + text[-(keep - head):]
        lines = _wrap(text, font, inner, chars=True)
        if len(lines) > max_lines:
            lines = lines[-max_lines:] if truncate == "tail" else lines[:max_lines]

        ls = self._linespace("mono")
        h = len(lines) * ls + 22
        y0 = self._reserve(h)
        box = (self.x0, y0, self.x1, y0 + h)

        def bake(img):
            chip = _shape(img.crop(box), 10, fill=SURFACE_ALT[0], fill_alpha=0.85,
                          border=(255, 255, 255), border_alpha=0.09)
            img.paste(chip, box[:2])

        self._bakes.append(bake)
        self._draws.append(lambda: self.canvas.create_text(
            self.x0 + 14, y0 + 11, text="\n".join(lines), anchor="nw",
            font=font, fill=TEXT, state="disabled"))

    def field(self, label: str, show: str = "•") -> None:
        h = 46
        ls = self._linespace("small")
        y0 = self._reserve(ls + 6 + h, gap=16)
        box = (self.x0, y0 + ls + 6, self.x1, y0 + ls + 6 + h)

        def draw():
            self.canvas.create_text(self.x0, y0, text=label.upper(), anchor="nw",
                                    font=self.fonts["eyebrow"], fill=TEXT_DIM, state="disabled")
            crop = self.bg.crop(box)
            normal = _shape(crop, 12, fill=SURFACE_ALT[0], fill_alpha=0.75,
                            border=(255, 255, 255), border_alpha=0.10)
            focus = _shape(crop, 12, fill=SURFACE_ALT[0], fill_alpha=0.75,
                           border=_rgb(ACCENT), border_alpha=0.95)
            ph_n, ph_f = self._photo(normal), self._photo(focus)
            item = self.canvas.create_image(box[0], box[1], image=ph_n, anchor="nw")

            entry = tk.Entry(
                self.root, show=show, font=self.fonts["field"], fg=TEXT,
                bg=_hex(normal.getpixel((normal.width // 2, normal.height // 2))),
                insertbackground=ACCENT, insertwidth=2, relief="flat", bd=0,
                highlightthickness=0, selectbackground=ACCENT_DIM, selectforeground=TEXT,
            )
            self.canvas.create_window(box[0] + 16, box[1] + h // 2, window=entry,
                                      anchor="w", width=(box[2] - box[0]) - 32)
            entry.bind("<FocusIn>", lambda e: None if self._closed
                       else self.canvas.itemconfig(item, image=ph_f))
            entry.bind("<FocusOut>", lambda e: None if self._closed
                       else self.canvas.itemconfig(item, image=ph_n))
            self._entry = entry

        self._draws.append(draw)

    def entry_value(self) -> str:
        return self._entry.get() if self._entry else ""

    def options(self, items: list) -> None:
        """
        Vertical list of choice rows. Each item:
            {"key": ..., "label": str, "fg": "#hex", "bg": "#hex", "sub": optional str}
        Clicking a row closes the window with that item's key.
        """
        row_h, gap = 50, 8
        y0 = self._reserve(len(items) * row_h + (len(items) - 1) * gap)
        badge_w = max(self.fonts["btn"].measure(i["label"]) for i in items) + 34
        for idx, item in enumerate(items):
            top = y0 + idx * (row_h + gap)
            self._draws.append(partial(
                self._draw_option, (self.x0, top, self.x1, top + row_h), item, badge_w))

    def _draw_option(self, box: tuple, item: dict, badge_w: int) -> None:
        w, h = box[2] - box[0], box[3] - box[1]
        fg, bgc = _rgb(item["fg"]), _rgb(item["bg"])
        crop = self.bg.crop(box)
        bx, by, bh = 14, (h - 28) // 2, 28

        def state(alpha, border, border_alpha):
            im = _shape(crop, 12, fill=SURFACE_ALT[0], fill_alpha=alpha,
                        border=border, border_alpha=border_alpha)
            badge = _shape(im.crop((bx, by, bx + badge_w, by + bh)), 14, fill=bgc,
                           fill_alpha=1.0, border=fg, border_alpha=0.35)
            im.paste(badge, (bx, by))
            return self._photo(im)

        imgs = (state(0.55, (255, 255, 255), 0.08),
                state(0.95, fg, 0.60),
                state(0.35, fg, 0.60))
        image_item = self.canvas.create_image(box[0], box[1], image=imgs[0], anchor="nw")
        self.canvas.create_text(box[0] + bx + badge_w // 2, box[1] + h // 2, text=item["label"],
                                font=self.fonts["btn"], fill=item["fg"], state="disabled")
        if item.get("sub"):
            self.canvas.create_text(box[2] - 16, box[1] + h // 2, text=item["sub"], anchor="e",
                                    font=self.fonts["mono"], fill=TEXT_DIM, state="disabled")
        self._bind_button(image_item, imgs, lambda: self.close(item["key"]))

    def progress(self) -> None:
        y0 = self._reserve(22)
        ty = y0 + 8

        def bake(img):
            track = _rounded_mask(self.cw, 6, 3).point(lambda v: int(v * 0.10))
            img.paste((255, 255, 255), (self.x0, ty), track)

        def draw():
            pill = self._photo(_pill_image())
            pad = 14
            py = ty + 3 - pill.height() // 2
            item = self.canvas.create_image(self.x0 - pad, py, image=pill, anchor="nw", state="disabled")
            start = time.monotonic()
            span = self.cw - 96

            def step():
                t = ((time.monotonic() - start) / 1.5) % 2
                p = t if t <= 1 else 2 - t
                self.canvas.coords(item, self.x0 - pad + (1 - math.cos(math.pi * p)) / 2 * span, py)

            self.every(16, step)

        self._bakes.append(bake)
        self._draws.append(draw)

    def buttons(self, specs: list, gap_before: int = 20) -> None:
        """
        specs: [(label, style, value), ...] laid out right-aligned, in order.
        style: "primary" | "secondary" | "danger". `value` (or its return
        value, if callable) is what the window closes with. The first
        primary/danger button is triggered by Enter.
        """
        h, spacing = 38, 10
        y0 = self._reserve(h, gap=gap_before)
        widths = [max(104, self.fonts["btn"].measure(s[0]) + 44) for s in specs]
        x = self.x1 - (sum(widths) + spacing * (len(specs) - 1))
        for spec, wd in zip(specs, widths):
            if self._on_enter is None and spec[1] in ("primary", "danger"):
                self._on_enter = partial(self._activate, spec[2])
            self._draws.append(partial(self._draw_button, (x, y0, x + wd, y0 + h), spec))
            x += wd + spacing

    def _draw_button(self, box: tuple, spec: tuple) -> None:
        label, style, value = spec
        crop = self.bg.crop(box)
        white = (255, 255, 255)
        if style == "primary":
            base = _rgb(ACCENT)
            imgs = [_shape(crop, 11, fill=c, border=white, border_alpha=0.22)
                    for c in (base, _mix(base, white, 0.14), _mix(base, (0, 0, 0), 0.22))]
            color = "#FFFFFF"
        elif style == "danger":
            fg, bgc = _rgb(RISK_COLORS["Critical"][0]), _rgb(RISK_COLORS["Critical"][1])
            imgs = [
                _shape(crop, 11, fill=bgc, fill_alpha=0.95, border=fg, border_alpha=0.45),
                _shape(crop, 11, fill=_mix(bgc, fg, 0.20), fill_alpha=1.0, border=fg, border_alpha=0.85),
                _shape(crop, 11, fill=_mix(bgc, (0, 0, 0), 0.30), fill_alpha=1.0, border=fg, border_alpha=0.5),
            ]
            color = RISK_COLORS["Critical"][0]
        else:
            imgs = [
                _shape(crop, 11, fill=SURFACE_ALT[0], fill_alpha=0.70, border=white, border_alpha=0.10),
                _shape(crop, 11, fill=SURFACE_ALT[0], fill_alpha=1.00, border=white, border_alpha=0.20),
                _shape(crop, 11, fill=SURFACE_ALT[0], fill_alpha=0.45, border=white, border_alpha=0.10),
            ]
            color = TEXT
        photos = tuple(self._photo(i) for i in imgs)
        item = self.canvas.create_image(box[0], box[1], image=photos[0], anchor="nw")
        self.canvas.create_text((box[0] + box[2]) // 2, (box[1] + box[3]) // 2, text=label,
                                font=self.fonts["btn"], fill=color, state="disabled")
        self._bind_button(item, photos, partial(self._activate, value))

    def _bind_button(self, image_item: int, photos: tuple, command) -> None:
        normal, hover, pressed = photos
        state = {"inside": False}
        c = self.canvas

        # NOTE: every handler bails out once the window is closing. Destroying
        # a window makes Tk fire synthetic <Leave>/<FocusOut> events, and
        # touching the canvas mid-destruction can crash the Tcl interpreter.
        def enter(_):
            if self._closed:
                return
            state["inside"] = True
            c.itemconfig(image_item, image=hover)
            c.config(cursor="hand2")

        def leave(_):
            if self._closed:
                return
            state["inside"] = False
            c.itemconfig(image_item, image=normal)
            c.config(cursor="")

        def press(_):
            if self._closed:
                return
            c.itemconfig(image_item, image=pressed)

        def release(_):
            if self._closed:
                return
            if state["inside"]:
                c.itemconfig(image_item, image=hover)
                command()

        c.tag_bind(image_item, "<Enter>", enter)
        c.tag_bind(image_item, "<Leave>", leave)
        c.tag_bind(image_item, "<ButtonPress-1>", press)
        c.tag_bind(image_item, "<ButtonRelease-1>", release)

    # ---- lifecycle --------------------------------------------------------
    def every(self, ms: int, fn) -> None:
        """Call fn() every `ms` milliseconds while the window is open."""
        key = len(self._loops)

        def loop():
            if self._closed:
                return
            fn()
            if not self._closed:
                self._after_ids[key] = self.root.after(ms, loop)
        self._loops.append((ms, loop, key))

    def _activate(self, value) -> None:
        self.close(value() if callable(value) else value)

    def close(self, value=None) -> None:
        if self._closed:
            return
        self._closed = True
        self.result = value
        for after_id in self._after_ids.values():
            try:
                self.root.after_cancel(after_id)
            except Exception:
                pass
        # Only stop the event loop here. The window itself is destroyed in
        # show() AFTER mainloop() returns -- destroying a canvas from inside
        # one of its own click handlers crashes the Tcl interpreter.
        self.root.quit()

    def show(self):
        """Render, display, block until closed, and return the result."""
        root, w = self.root, self.W
        h = self.y + self.PAD + self.M
        card = (self.M, self.M, w - self.M, h - self.M)

        img = _render_background(w, h)
        blurred = ImageEnhance.Color(img.filter(ImageFilter.GaussianBlur(GLASS_BLUR))).enhance(GLASS_SATURATE)
        _glass_panel(img, blurred, card, self.RADIUS)
        for bake in self._bakes:
            bake(img)
        self.bg = img

        self.canvas = tk.Canvas(root, width=w, height=h, bg=BG, highlightthickness=0, bd=0)
        self.canvas.pack()
        self.canvas.create_image(0, 0, image=self._photo(img), anchor="nw")
        for draw in self._draws:
            draw()

        if self.closable:
            root.protocol("WM_DELETE_WINDOW", lambda: self.close(None))
            root.bind("<Escape>", lambda e: self.close(None))
        else:
            root.protocol("WM_DELETE_WINDOW", lambda: None)
        root.bind("<Return>", lambda e: self._on_enter() if self._on_enter else None)

        sw, sh = root.winfo_screenwidth(), root.winfo_screenheight()
        root.geometry(f"{w}x{h}+{(sw - w) // 2}+{max(20, (sh - h) // 3)}")
        _style_titlebar(root)
        root.deiconify()
        root.attributes("-topmost", True)   # never open hidden behind Explorer
        root.lift()
        root.focus_force()
        if self._entry:
            self._entry.focus_set()

        for ms, loop, key in self._loops:
            self._after_ids[key] = root.after(ms, loop)
        root.mainloop()
        try:
            root.destroy()
        except tk.TclError:
            pass
        return self.result


# ---------------------------------------------------------------------------
# READY-MADE DIALOGS
# ---------------------------------------------------------------------------
def ask_pin(message: str, title: str = "PIN required", eyebrow: str = "DAAMS · Security"):
    """Masked PIN prompt. Returns the typed text, or None if cancelled."""
    win = GlassWindow(width=420)
    win.header(eyebrow, title, icon="lock")
    win.text(message, "dim")
    win.field("PIN")
    win.buttons([("Cancel", "secondary", None), ("Confirm", "primary", win.entry_value)])
    return win.show()


def show_message(kind: str, title: str, message: str, detail: str = None, eyebrow: str = None) -> None:
    """kind: 'success' | 'info' | 'warning' | 'error'."""
    eyebrows = {"success": "DAAMS · Done", "info": "DAAMS · Notice",
                "warning": "DAAMS · Security notice", "error": "DAAMS · Error"}
    win = GlassWindow(width=440)
    win.header(eyebrow or eyebrows.get(kind, "DAAMS"), title, icon=kind)
    win.text(message, "body")
    if detail:
        win.code_block(detail, max_lines=6, truncate="tail")
    win.buttons([("OK", "primary", True)])
    win.show()


def ask_confirm(title: str, message: str, path: str = None, note: str = None,
                confirm_label: str = "Confirm", cancel_label: str = "Cancel",
                danger: bool = False, icon: str = "warning",
                eyebrow: str = "DAAMS · Confirm") -> bool:
    """Yes/no dialog. Returns True only if the confirm button was chosen."""
    win = GlassWindow(width=440)
    win.header(eyebrow, title, icon=icon)
    win.text(message, "body")
    if path:
        win.code_block(path)
    if note:
        win.text(note, "dim")
    win.buttons([(cancel_label, "secondary", False),
                 (confirm_label, "danger" if danger else "primary", True)])
    return bool(win.show())


def ask_classification(path: str, asset_type: str, classifications, sublabels: dict = None):
    """Classification picker. Returns the chosen classification, or None."""
    sublabels = sublabels or {}
    items = []
    for name in classifications:
        fg, bg = CLASSIFICATION_COLORS.get(name, (TEXT_DIM, "#171d29"))
        items.append({"key": name, "label": name, "fg": fg, "bg": bg, "sub": sublabels.get(name)})

    win = GlassWindow(width=440)
    win.header("DAAMS · Protect asset", f"Protect this {asset_type}", icon="shield")
    win.code_block(path)
    win.text("Choose a classification:", "dim")
    win.options(items)
    win.buttons([("Cancel", "secondary", None)], gap_before=16)
    return win.show()


def run_with_loading(message: str, func, *args, min_seconds: float = 0.7, **kwargs):
    """
    Show a glass 'please wait' window with an animated bar while
    func(*args, **kwargs) runs on a background thread. Returns func's
    result, or re-raises its exception. The window stays up for at least
    `min_seconds` so a fast operation doesn't just flash on screen.
    """
    outcome = {}

    def worker():
        try:
            outcome["value"] = func(*args, **kwargs)
        except BaseException as e:  # re-raised on the UI thread below
            outcome["error"] = e

    win = GlassWindow(width=420, closable=False)
    win.header("DAAMS · Working", "Please wait", icon="shield")
    win.text(message, "dim")
    win.progress()

    thread = threading.Thread(target=worker)
    started = time.monotonic()
    thread.start()

    def check():
        if not thread.is_alive() and time.monotonic() - started >= min_seconds:
            win.close(None)

    win.every(50, check)
    win.show()
    thread.join()

    if "error" in outcome:
        raise outcome["error"]
    return outcome.get("value")


# ---------------------------------------------------------------------------
# PREVIEW: python glass_ui.py
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    demo_path = r"C:\DAAMS_Test\HR_Contracts\2026\Q3\Employee_Contracts_Final_v3.xlsx"
    ask_pin("Enter your DAAMS PIN to protect this file.")
    show_message("warning", "Default PIN in use",
                 "You are still using the DEFAULT PIN (1234).\n\nPlease change it soon by running change_pin_cli.py.")
    print("chosen:", ask_classification(
        demo_path, "file", ("Public", "Sensitive", "Private", "Confidential"),
        {"Public": "base risk +5", "Sensitive": "base risk +15",
         "Private": "base risk +25", "Confidential": "base risk +35"}))
    run_with_loading("Protecting... please wait.", time.sleep, 3)
    show_message("success", "Protected", f"'{demo_path}' is now protected as Sensitive.")
    print("remove:", ask_confirm("Remove protection", "Remove DAAMS protection from this file?",
                                 path=demo_path,
                                 note="The file itself will NOT be deleted or modified. Existing activity history will be kept.",
                                 confirm_label="Remove protection", danger=True))
    show_message("error", "Unexpected error", "Something went wrong. Details were saved to daams_cli_debug.log.",
                 detail="ModuleNotFoundError: No module named 'firebase_admin'")
