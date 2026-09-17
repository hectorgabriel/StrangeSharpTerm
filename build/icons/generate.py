#!/usr/bin/env python3
"""Vendors the Lucide icons the app uses as Avalonia geometries.

Lucide draws with SVG primitives -- rect, line, circle, polyline, path -- and
Avalonia's StreamGeometry understands only path syntax, so each icon is folded
into a single path here rather than at runtime. The output is committed, so a
build needs no network and no dependency on a package built against a different
major version of Avalonia.

    ./build/icons/generate.py            # regenerate Icons.axaml

Every icon is 24x24 and stroked, never filled: SF Symbols' .fill variants have no
Lucide counterpart, so where the Swift app used fill to mean "active", colour
carries that instead.
"""
import math
import re
import sys
import urllib.request
from pathlib import Path

VERSION = "1.45.0"
BASE = f"https://unpkg.com/lucide-static@{VERSION}/icons"
ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "src/StrangeSharpTerm.App/Icons/Icons.axaml"
LICENCE = ROOT / "src/StrangeSharpTerm.App/Icons/LICENSE-lucide.txt"

# SF Symbol -> Lucide, one reviewed pass rather than glyph-by-glyph drift.
# See docs/adr/0003-icons.md for the three that needed a judgement call.
ICONS = {
    "refresh-cw": "arrow.clockwise",
    "arrow-left-right": "arrow.left.arrow.right",
    "arrow-right-to-line": "arrow.right.to.line",
    "arrow-up": "arrow.up",
    "zap": "bolt.fill",
    "circle-check": "checkmark.circle.fill",
    "chevron-down": "chevron.down",
    "chevron-right": "chevron.right",
    "chevron-up": "chevron.up",
    "chevrons-up-down": "chevron.up.chevron.down",
    "radio-tower": "dot.radiowaves.left.and.right",
    "ellipsis": "ellipsis",
    "octagon-alert": "exclamationmark.octagon.fill",
    "triangle-alert": "exclamationmark.triangle.fill",
    "folder": "folder",
    "settings": "gearshape",
    "info": "info.circle",
    "key": "key.fill",
    "shield-check": "lock.shield",
    "search": "magnifyingglass",
    "circle-minus": "minus.circle",
    "plus": "plus",
    "rows-2": "rectangle.split.1x2",
    "columns-2": "rectangle.split.2x1",
    "server": "server.rack",
    "sliders-horizontal": "slider.horizontal.3",
    "sparkles": "sparkles",
    "layers": "square.stack.3d.up",
    "square": "stop.fill",
    "text-cursor-input": "text.append",
    "trash-2": "trash",
    "x": "xmark",
    "circle-x": "xmark.circle / xmark.circle.fill",
    # Needed by the panes and browser still to come, generated now so the set is
    # decided in one pass rather than grown one glyph at a time.
    "square-terminal": "(terminal pane)",
    "folder-open": "(expanded folder)",
    "file": "(file browser entry)",
    "upload": "(sftp upload)",
    "download": "(sftp download)",
    "play": "(run)",
    "plug": "(connect)",
    "unplug": "(disconnect)",
    "panel-left": "(toggle sidebar)",
    # The workspace: a tree that can be added to, and a file that can be saved.
    "file-plus": "(new file in the workspace)",
    "folder-plus": "(new folder in the workspace)",
    "save": "(write the open file back)",
}


def numbers(text):
    return [float(value) for value in re.findall(r"-?\d*\.?\d+", text)]


def attribute(element, name, default=0.0):
    found = re.search(rf'{name}="(-?[\d.]+)"', element)
    return float(found.group(1)) if found else default


def rounded_rect(x, y, width, height, rx, ry):
    """A rect as a path. Lucide rounds corners on panels and servers."""
    rx = min(rx or ry, width / 2)
    ry = min(ry or rx, height / 2)
    if rx <= 0 or ry <= 0:
        return f"M{x},{y}h{width}v{height}h{-width}z"
    return (
        f"M{x + rx},{y}"
        f"h{width - 2 * rx}a{rx},{ry} 0 0 1 {rx},{ry}"
        f"v{height - 2 * ry}a{rx},{ry} 0 0 1 {-rx},{ry}"
        f"h{-(width - 2 * rx)}a{rx},{ry} 0 0 1 {-rx},{-ry}"
        f"v{-(height - 2 * ry)}a{rx},{ry} 0 0 1 {rx},{-ry}z"
    )


def circle(cx, cy, r):
    return f"M{cx - r},{cy}a{r},{r} 0 1 0 {2 * r},0a{r},{r} 0 1 0 {-2 * r},0z"


def points_to_path(points, close):
    values = numbers(points)
    pairs = list(zip(values[::2], values[1::2]))
    if not pairs:
        return ""
    path = "M" + " L".join(f"{x},{y}" for x, y in pairs)
    return path + ("z" if close else "")


def to_geometry(svg):
    """Folds one Lucide icon's primitives into a single path."""
    parts = []
    for match in re.finditer(r"<(path|circle|rect|line|polyline|polygon)\b[^>]*/?>", svg):
        element, kind = match.group(0), match.group(1)
        if kind == "path":
            data = re.search(r'\sd="([^"]+)"', element)
            if data:
                parts.append(absolute_start(data.group(1).strip()))
        elif kind == "circle":
            parts.append(circle(attribute(element, "cx"), attribute(element, "cy"), attribute(element, "r")))
        elif kind == "rect":
            parts.append(rounded_rect(
                attribute(element, "x"), attribute(element, "y"),
                attribute(element, "width"), attribute(element, "height"),
                attribute(element, "rx"), attribute(element, "ry")))
        elif kind == "line":
            parts.append(
                f"M{attribute(element, 'x1')},{attribute(element, 'y1')}"
                f"L{attribute(element, 'x2')},{attribute(element, 'y2')}")
        elif kind in ("polyline", "polygon"):
            found = re.search(r'points="([^"]+)"', element)
            if found:
                parts.append(points_to_path(found.group(1), close=kind == "polygon"))
    return " ".join(parts)


def absolute_start(d):
    """Pins a path's first point, leaving everything after it relative.

    A leading relative moveto is absolute per the SVG spec, but only while it is
    the first command of its own path; concatenated here, it would otherwise be
    measured from wherever the previous stroke ended. Rewriting the letter alone
    is not enough -- the numbers that follow a lowercase moveto are implicitly
    relative linetos, and an uppercase one would make them absolute.
    """
    if not d.startswith("m"):
        return d
    found = re.match(r"m\s*(-?[\d.]+)[\s,]+(-?[\d.]+)\s*(.*)", d, re.S)
    if not found:
        return d
    x, y, rest = found.groups()
    rest = rest.strip()
    if not rest:
        return f"M{x},{y}"
    return f"M{x},{y} " + (rest if rest[0].isalpha() else f"l{rest}")


def bounding_box(path):
    """Rough extent of a path: enough to catch a shape placed outside its canvas."""
    tokens = re.findall(r"[A-Za-z]|-?\d*\.?\d+(?:e-?\d+)?", path)
    x = y = 0.0
    start = (0.0, 0.0)
    low = [math.inf, math.inf]
    high = [-math.inf, -math.inf]
    command = ""
    index = 0

    def see(px, py):
        low[0], low[1] = min(low[0], px), min(low[1], py)
        high[0], high[1] = max(high[0], px), max(high[1], py)

    while index < len(tokens):
        token = tokens[index]
        if re.fullmatch(r"[A-Za-z]", token):
            command = token
            index += 1
            if command in "Zz":
                x, y = start
                continue
        relative = command.islower()
        upper = command.upper()
        counts = {"M": 2, "L": 2, "T": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "A": 7}
        need = counts.get(upper)
        if need is None:
            break
        # A command's numbers run until the next letter: anything shorter means
        # the path moved on, and reading past it would mix two commands together.
        run = tokens[index:index + need]
        if len(run) < need or any(re.fullmatch(r"[A-Za-z]", token) for token in run):
            break
        values = [float(value) for value in run]
        index += need

        if upper == "H":
            x = x + values[0] if relative else values[0]
        elif upper == "V":
            y = y + values[0] if relative else values[0]
        elif upper == "A":
            x = x + values[5] if relative else values[5]
            y = y + values[6] if relative else values[6]
        else:
            px, py = values[-2], values[-1]
            x = x + px if relative else px
            y = y + py if relative else py
        if upper == "M":
            start = (x, y)
        see(x, y)
        if upper == "M" and command == "M":
            command = "L"
        elif upper == "M":
            command = "l"

    return low[0], low[1], high[0], high[1]


def pascal(name):
    return "".join(word.capitalize() for word in name.split("-"))


def main():
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    geometries = []

    for icon, replaces in ICONS.items():
        with urllib.request.urlopen(f"{BASE}/{icon}.svg", timeout=30) as response:
            svg = response.read().decode("utf-8")
        geometry = to_geometry(svg)
        if not geometry:
            print(f"no geometry for {icon}", file=sys.stderr)
            return 1
        left, top, right, bottom = bounding_box(geometry)
        if left < -1 or top < -1 or right > 25 or bottom > 25:
            print(f"{icon} is drawn outside its 24x24 canvas: "
                  f"({left:.1f},{top:.1f})-({right:.1f},{bottom:.1f})", file=sys.stderr)
            return 1
        geometries.append((icon, replaces, geometry))
        print(f"  {icon:<22} {replaces}")

    lines = [
        "<!--",
        f"    Lucide {VERSION}, vendored. Regenerate with build/icons/generate.py; do not",
        "    hand-edit. Each icon is a 24x24 stroked path: Lucide has no filled",
        "    variants, so where SF Symbols used .fill to mean \"active\", colour says it.",
        "-->",
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
    ]
    for icon, replaces, geometry in geometries:
        lines.append(f"    <!-- {replaces} -->")
        lines.append(f'    <StreamGeometry x:Key="Icon{pascal(icon)}">{geometry}</StreamGeometry>')
    lines.append("</ResourceDictionary>")
    OUTPUT.write_text("\n".join(lines) + "\n", encoding="utf-8")

    with urllib.request.urlopen(f"https://unpkg.com/lucide-static@{VERSION}/LICENSE", timeout=30) as response:
        LICENCE.write_text(response.read().decode("utf-8"), encoding="utf-8")

    print(f"\nwrote {len(geometries)} icons to {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
