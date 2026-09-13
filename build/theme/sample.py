#!/usr/bin/env python3
"""Where the theme colours come from.

The Swift app's ThemePalette is not on this machine, but its output is:
docs/reference/screenshots/ was captured from the running app, and every colour
it paints can be read back out of the pixels. This script does that reading, so
the values in AppPalette.cs are checkable rather than remembered -- run it and
it re-measures each one, comparing against what the code claims.

Every screenshot is in the **Dracula** theme (settings.png shows it selected),
so Dracula's chrome is measured directly. StrangeTerm Dark appears only as the
swatch in its row of the theme picker, which is enough for its three surfaces
and its accent; the rest of that theme is derived here by rules the same
screenshots justify. See docs/adr/0004-theme-colours.md.

    python3 build/theme/sample.py

Exits non-zero if a measurement no longer matches, which means either the
screenshots changed or the expected value did.
"""

import struct
import sys
import zlib
from pathlib import Path

SHOTS = Path(__file__).resolve().parents[2] / "docs/reference/screenshots"


# --- reading a PNG -------------------------------------------------------
# The screenshots are 8-bit truecolour and not interlaced, which is the one
# case worth 30 lines of decoder. Anything else here is a bug, not a format to
# support, so it says so rather than guessing.

class Image:
    def __init__(self, path):
        data = path.read_bytes()
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError(f"{path} is not a PNG")

        pixels, offset = bytearray(), 8
        while offset < len(data):
            length, kind = struct.unpack(">I4s", data[offset:offset + 8])
            body = data[offset + 8:offset + 8 + length]
            offset += 12 + length
            if kind == b"IHDR":
                self.width, self.height, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", body)
                if (depth, colour, interlace) != (8, 2, 0):
                    raise ValueError(f"{path}: expected 8-bit truecolour, not interlaced")
            elif kind == b"IDAT":
                pixels += body
            elif kind == b"IEND":
                break

        self.rows = self._unfilter(zlib.decompress(bytes(pixels)))

    def _unfilter(self, raw):
        stride = self.width * 3
        rows, previous, offset = [], bytearray(stride), 0
        for _ in range(self.height):
            filter_type = raw[offset]
            line = bytearray(raw[offset + 1:offset + 1 + stride])
            offset += 1 + stride
            for i in range(stride):
                left = line[i - 3] if i >= 3 else 0
                up = previous[i]
                upleft = previous[i - 3] if i >= 3 else 0
                if filter_type == 1:
                    line[i] = (line[i] + left) & 0xFF
                elif filter_type == 2:
                    line[i] = (line[i] + up) & 0xFF
                elif filter_type == 3:
                    line[i] = (line[i] + (left + up) // 2) & 0xFF
                elif filter_type == 4:
                    p = left + up - upleft
                    pa, pb, pc = abs(p - left), abs(p - up), abs(p - upleft)
                    nearest = left if pa <= pb and pa <= pc else (up if pb <= pc else upleft)
                    line[i] = (line[i] + nearest) & 0xFF
                elif filter_type != 0:
                    raise ValueError(f"unknown PNG filter {filter_type}")
            rows.append(line)
            previous = line
        return rows

    def pixel(self, x, y):
        row = self.rows[y]
        return row[x * 3], row[x * 3 + 1], row[x * 3 + 2]

    def flat(self, x, y, radius=2):
        """The colour of a flat area, refusing to answer if it is not flat.

        A single pixel can land on an anti-aliased edge and read as a colour the
        theme does not contain. Sampling a block and insisting it is one colour
        is what makes a measurement a measurement. The radius shrinks for the
        progress bars, which are only seven pixels tall.
        """
        colours = {self.pixel(x + dx, y + dy)
                   for dx in range(-radius, radius + 1) for dy in range(-radius, radius + 1)}
        if len(colours) != 1:
            raise ValueError(f"({x},{y}) is not a flat area: {[hexof(c) for c in colours]}")
        return colours.pop()

    def ink(self, x0, y0, x1, y1):
        """The commonest colour that is not the background: the text itself.

        Small text never reaches its own colour -- every pixel is a blend -- but
        a run of it at 15pt does, and the modal far-from-background colour is
        that value. Not any run: macOS gamma-boosts the core of a large bold
        glyph past the colour it was asked for, so the 20pt heading reads
        #FFFFF8 where the colour is #F8F8F2. The regions below are chosen to
        avoid that.
        """
        from collections import Counter
        counts = Counter(self.pixel(x, y) for y in range(y0, y1) for x in range(x0, x1))
        background = counts.most_common(1)[0][0]
        ink = Counter({c: n for c, n in counts.items()
                       if sum(abs(a - b) for a, b in zip(c, background)) > 150})
        if not ink:
            raise ValueError(f"({x0},{y0})-({x1},{y1}) is all background")
        return ink.most_common(1)[0][0]


def hexof(colour):
    # Clamped: solving a tint back out can overshoot the end of the range by a
    # rounding step, and a colour is still a colour at 255.
    return "#%02X%02X%02X" % tuple(min(255, max(0, int(round(v)))) for v in colour)


def rgb(text):
    text = text.lstrip("#")
    return tuple(int(text[i:i + 2], 16) for i in (0, 2, 4))


def luminance(colour):
    return 0.2126 * colour[0] + 0.7152 * colour[1] + 0.0722 * colour[2]


# --- what each measurement is -------------------------------------------
# (role, screenshot, how to read it, what AppPalette.cs says it is)

DRACULA = [
    ("Rail",       "dashboard", ("flat", 35, 500),                "#191A21"),
    ("Sidebar",    "dashboard", ("flat", 250, 950),               "#21222C"),
    ("Background", "dashboard", ("flat", 1000, 100),              "#282A36"),
    ("Surface",    "dashboard", ("flat", 1000, 520),              "#343746"),
    ("Border",     "dashboard", ("flat", 1400, 252, 1),          "#424450"),
    ("Selection",  "dashboard", ("flat", 300, 340),               "#353147"),
    ("Accent",     "dashboard", ("flat", 1317, 50),               "#BD93F9"),
    ("Text",       "settings",  ("ink", 85, 1240, 175, 1262),     "#F8F8F2"),
    ("Muted",      "settings",  ("ink", 37, 839, 772, 877),       "#6272A4"),
    ("OnAccent",   "settings",  ("ink", 745, 1905, 825, 1940),    "#191A21"),
    ("Danger",     "settings",  ("ink", 759, 752, 843, 775),      "#FF5555"),
    ("Warning",    "dashboard", ("flat", 600, 293, 1),            "#FFB86C"),
]

# StrangeTerm Dark is not the theme any screenshot is in. Its row in the theme
# picker draws its own surfaces and accent as four squares, which is the whole
# of what can be measured for it.
STRANGETERM_DARK = [
    ("Sidebar",    "settings", ("flat", 67, 248),  "#16181D"),
    ("Background", "settings", ("flat", 95, 248),  "#1A1D23"),
    ("Surface",    "settings", ("flat", 123, 248), "#21252E"),
    ("Accent",     "settings", ("flat", 151, 248), "#2ED3A0"),
]

# The same four squares for the theme the screenshots are in, which is what
# says the squares mean surfaces at all: they are the colours measured above.
DRACULA_SWATCH = [
    ("Sidebar",    "settings", ("flat", 67, 362),  "#21222C"),
    ("Background", "settings", ("flat", 95, 362),  "#282A36"),
    ("Surface",    "settings", ("flat", 123, 362), "#343746"),
    ("Accent",     "settings", ("flat", 151, 362), "#BD93F9"),
]


# The sidebar draws a coloured initials badge for each host: the host's colour
# laid over the sidebar at a low alpha. Solving each tint back out is what says
# which colours the set is made of -- and green comes back as Dracula's ANSI
# green, which TerminalPalette already carries, so the chrome and the terminal
# are using one palette rather than two that happen to agree.
TINTS = [
    ("web-02 (green)",     132, 259, "#50FA7B"),
    ("homelab (red)",      115, 637, "#FF5555"),
    ("vps-paris (orange)", 115, 689, "#FFB86C"),
    ("staging (purple)",   116, 542, "#BD93F9"),
    ("bastion (muted)",    114, 447, "#6272A4"),
]


def solve_tints(image, alpha=0.16, background="#21222C"):
    print(f"\nHost badges, solved back out at {alpha:.0%} over the sidebar")
    base = rgb(background)
    for name, x, y, expected in TINTS:
        tint = image.flat(x, y, 1)
        implied = [(t - (1 - alpha) * b) / alpha for t, b in zip(tint, base)]
        near = max(abs(a - b) for a, b in zip(implied, rgb(expected)))
        print(f"  {name:<19} {hexof(tint)} -> {hexof(implied)}  "
              f"{'matches' if near <= 6 else 'differs from'} {expected}")


def measure(images, table, title):
    print(f"\n{title}")
    failures = 0
    for role, shot, how, expected in table:
        got = images[shot].flat(*how[1:]) if how[0] == "flat" else images[shot].ink(*how[1:])
        ok = hexof(got) == expected
        failures += not ok
        print(f"  {role:<11} {hexof(got)}  {'' if ok else '!= ' + expected + '  MISMATCH'}")
    return failures


def derive():
    """StrangeTerm Dark's unmeasured roles, from Dracula's own relationships."""
    dracula = {role: rgb(value) for role, _, _, value in DRACULA}
    dark = {role: rgb(value) for role, _, _, value in STRANGETERM_DARK}
    # Its foreground is not in the screenshots either, but it is already ported:
    # TerminalPalette.StrangeTermDark carries the Swift app's terminal colours.
    dark["Text"] = rgb("#E7EAF0")

    print("\nStrangeTerm Dark, derived")

    ratio = [a / b for a, b in zip(dracula["Rail"], dracula["Sidebar"])]
    print(f"  Rail        {hexof([v * r for v, r in zip(dark['Sidebar'], ratio)])}"
          f"   sidebar x {['%.3f' % r for r in ratio]}, the ratio Dracula's rail has to its sidebar")

    ratio = [a / b for a, b in zip(dracula["Border"], dracula["Surface"])]
    print(f"  Border      {hexof([v * r for v, r in zip(dark['Surface'], ratio)])}"
          f"   surface x {['%.3f' % r for r in ratio]}, likewise")

    # Dracula's selection is its accent laid over its sidebar; find the alpha
    # that reproduces the measurement rather than assuming a round number.
    alpha = next(a / 100 for a in range(1, 40)
                 if hexof([(a / 100) * x + (1 - a / 100) * y
                           for x, y in zip(dracula["Accent"], dracula["Sidebar"])]) == hexof(dracula["Selection"]))
    blended = [alpha * x + (1 - alpha) * y for x, y in zip(dark["Accent"], dark["Sidebar"])]
    print(f"  Selection   {hexof(blended)}   accent at {alpha:.0%} over its sidebar, the alpha Dracula's selection is")

    # Muted text is a theme's own colour, not a shade of anything else. What
    # carries across is how far down it sits: the same luminance, in the hue the
    # theme's surfaces already have.
    target = luminance(dracula["Muted"])
    scaled = [v * target / luminance(dark["Background"]) for v in dark["Background"]]
    check = [v * target / luminance(dracula["Background"]) for v in dracula["Background"]]
    print(f"  Muted       {hexof(scaled)}   background raised to luminance {target:.0f}, Dracula's comment colour")
    print(f"              (the same rule on Dracula's own background gives {hexof(check)} "
          f"against a measured {hexof(dracula['Muted'])}: the right depth, a plainer hue)")

    print(f"  OnAccent    {hexof([v * r for v, r in zip(dark['Sidebar'], [a / b for a, b in zip(dracula['Rail'], dracula['Sidebar'])])])}"
          f"   its rail, as Dracula's label on an accent button is its rail")


def main():
    images = {name: Image(SHOTS / f"{name}.png") for name in ("dashboard", "settings")}
    failures = measure(images, DRACULA, "Dracula, measured from the app itself")
    failures += measure(images, DRACULA_SWATCH, "Dracula, measured again from its swatch in the theme picker")
    failures += measure(images, STRANGETERM_DARK, "StrangeTerm Dark, measured from its swatch in the theme picker")
    solve_tints(images["dashboard"])
    derive()
    if failures:
        print(f"\n{failures} measurement(s) no longer match.", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
