#!/usr/bin/env python3
"""Vendors a few Fluent UI System Icons, Color style, as Avalonia drawings.

A trial beside the Lucide set, not a replacement for it: Fluent's Color style has
about two hundred icons, and no folder, server, terminal, trash or chevron among
them. What it does have is the status and settings vocabulary -- a check, a
warning, a gear -- which is where colour already means something here.

Each icon is an SVG of filled paths painted with linear and radial gradients.
Avalonia has every one of those as a drawing, so the SVG is translated to a
DrawingImage at generation time rather than rendered at runtime, and the output
is committed: a build needs no network and no SVG package.

    ./build/icons/generate_color.py      # regenerate ColorIcons.axaml

The 16px masters are used: they are drawn to that pixel grid, and every call
site shows them at 14 to 18.
"""
import math
import re
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

# The commit the icons are pinned to; the repository publishes no tags for them.
COMMIT = "8512d0121f6abd6c8c40f0bc4eb502ccd66ce6e7"
BASE = f"https://raw.githubusercontent.com/microsoft/fluentui-system-icons/{COMMIT}"
SIZE = 16
ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "src/StrangeSharpTerm.App/Icons/ColorIcons.axaml"
LICENCE = ROOT / "src/StrangeSharpTerm.App/Icons/LICENSE-fluentui-system-icons.txt"

SVG = "{http://www.w3.org/2000/svg}"

# Fluent name -> the Lucide icon it stands in for, at the sites being tried.
ICONS = {
    "settings": "IconSettings (the foot of the sidebar)",
    "options": "IconSlidersHorizontal (edit a host)",
    "dismiss_circle": "IconCircleX (close this pane)",
    "bot_sparkle": "IconSparkles (the assistant)",
    "agents": "IconLayers (ask several hosts)",
    "warning": "IconTriangleAlert (a failed connection)",
    "checkmark_circle": "IconCircleCheck (the chosen theme or provider)",
}

NAMED = {"white": "FFFFFF", "black": "000000"}


def number(text):
    return float(text) if text is not None else 0.0


def fmt(value):
    text = f"{value:.4f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


def colour(text, opacity=1.0):
    text = text.strip()
    rgb = NAMED.get(text.lower(), text.lstrip("#"))
    if len(rgb) == 3:
        rgb = "".join(c * 2 for c in rgb)
    if not re.fullmatch(r"[0-9A-Fa-f]{6}", rgb):
        raise ValueError(f"unsupported colour {text!r}")
    return f"#{round(opacity * 255):02X}{rgb.upper()}"


def matrix(transform):
    """translate(), rotate() and scale(), composed in SVG's order, as an
    Avalonia matrix: m11,m12,m21,m22,offsetX,offsetY. Those three are the only
    functions any Color icon's gradients use; anything else fails loudly."""
    a, b, c, d, e, f = 1.0, 0.0, 0.0, 1.0, 0.0, 0.0
    for name, args in re.findall(r"(\w+)\(([^)]*)\)", transform):
        v = [float(x) for x in re.split(r"[\s,]+", args.strip())]
        if name == "translate":
            m = (1, 0, 0, 1, v[0], v[1] if len(v) > 1 else 0)
        elif name == "rotate" and len(v) == 1:
            r = math.radians(v[0])
            m = (math.cos(r), math.sin(r), -math.sin(r), math.cos(r), 0, 0)
        elif name == "scale":
            m = (v[0], 0, 0, v[1] if len(v) > 1 else v[0], 0, 0)
        else:
            raise ValueError(f"unsupported transform {name}({args})")
        # current x m, both as SVG's column-vector matrices
        a, b, c, d, e, f = (a * m[0] + c * m[1], b * m[0] + d * m[1],
                            a * m[2] + c * m[3], b * m[2] + d * m[3],
                            a * m[4] + c * m[5] + e, b * m[4] + d * m[5] + f)
    return ",".join(fmt(x) for x in (a, b, c, d, e, f))


def stops(gradient, indent):
    lines = []
    for stop in gradient.iter(f"{SVG}stop"):
        opacity = float(stop.get("stop-opacity", "1"))
        lines.append(f'{indent}<GradientStop Offset="{fmt(number(stop.get("offset")))}" '
                     f'Color="{colour(stop.get("stop-color", "#000"), opacity)}" />')
    return lines


def brush(fill, gradients, opacity, indent):
    """The brush a fill names, as XAML lines. Gradients are all in user space,
    so their points are absolute: Avalonia reads "x,y" without a % as such."""
    extra = f' Opacity="{fmt(opacity)}"' if opacity < 1 else ""
    reference = re.fullmatch(r"url\(#(.+)\)", fill)
    if not reference:
        return [f'{indent}<SolidColorBrush Color="{colour(fill)}"{extra} />']

    gradient = gradients[reference.group(1)]
    if gradient.get("gradientUnits") != "userSpaceOnUse":
        raise ValueError(f"gradient {reference.group(1)} is not in user space")
    inner = indent + "    "

    if gradient.tag == f"{SVG}linearGradient":
        start = f'{fmt(number(gradient.get("x1")))},{fmt(number(gradient.get("y1")))}'
        end = f'{fmt(number(gradient.get("x2")))},{fmt(number(gradient.get("y2")))}'
        return ([f'{indent}<LinearGradientBrush StartPoint="{start}" EndPoint="{end}"{extra}>']
                + stops(gradient, inner)
                + [f"{indent}</LinearGradientBrush>"])

    cx, cy, r = (number(gradient.get(k)) for k in ("cx", "cy", "r"))
    fx, fy = number(gradient.get("fx", gradient.get("cx"))), number(gradient.get("fy", gradient.get("cy")))
    # A gradient transform is applied about user space's origin, not the
    # brush's centre, which is where Avalonia would otherwise put it.
    transformed = gradient.get("gradientTransform")
    origin = ' TransformOrigin="0,0"' if transformed else ""
    lines = [f'{indent}<RadialGradientBrush Center="{fmt(cx)},{fmt(cy)}" '
             f'GradientOrigin="{fmt(fx)},{fmt(fy)}" RadiusX="{fmt(r)}" RadiusY="{fmt(r)}"'
             f'{origin}{extra}>']
    if transformed:
        lines += [f"{inner}<RadialGradientBrush.Transform>",
                  f'{inner}    <MatrixTransform Matrix="{matrix(gradient.get("gradientTransform"))}" />',
                  f"{inner}</RadialGradientBrush.Transform>"]
    return lines + stops(gradient, inner) + [f"{indent}</RadialGradientBrush>"]


def geometry(element):
    """Either an attribute (path data) or an element (anything else), never both."""
    tag = element.tag.removeprefix(SVG)
    if tag == "path":
        # SVG fills nonzero unless told otherwise; Avalonia's path syntax defaults
        # to even-odd, so the rule is always written out.
        rule = "F0" if element.get("fill-rule") == "evenodd" else "F1"
        return f'Geometry="{rule} {element.get("d")}"', None
    if tag == "circle":
        return None, (f'<EllipseGeometry Center="{fmt(number(element.get("cx")))},{fmt(number(element.get("cy")))}" '
                      f'RadiusX="{fmt(number(element.get("r")))}" RadiusY="{fmt(number(element.get("r")))}" />')
    if tag == "rect":
        rx = number(element.get("rx", element.get("ry")))
        rect = ",".join(fmt(number(element.get(k))) for k in ("x", "y", "width", "height"))
        return None, f'<RectangleGeometry Rect="{rect}" RadiusX="{fmt(rx)}" RadiusY="{fmt(rx)}" />'
    raise ValueError(f"unsupported element <{tag}>")


def drawings(parent, gradients, opacity, indent, name):
    lines = []
    for element in parent:
        tag = element.tag.removeprefix(SVG)
        if tag in ("defs", "filter"):
            continue
        if element.get("filter"):
            # One icon in the set casts a blurred shadow; a drawing cannot, and at
            # 16px the shadow is a pixel. Its shapes are kept, the shadow is not.
            print(f"  {name}: dropped a filter", file=sys.stderr)
        own = opacity * float(element.get("opacity", "1"))
        if tag == "g":
            lines += drawings(element, gradients, own, indent, name)
            continue
        fill = element.get("fill", "#000")
        if fill == "none":
            continue
        own *= float(element.get("fill-opacity", "1"))
        attribute, element_xaml = geometry(element)
        painted = brush(fill, gradients, own, indent + "        ")
        if attribute:
            lines += [f"{indent}<GeometryDrawing {attribute}>",
                      f"{indent}    <GeometryDrawing.Brush>", *painted, f"{indent}    </GeometryDrawing.Brush>",
                      f"{indent}</GeometryDrawing>"]
        else:
            lines += [f"{indent}<GeometryDrawing>",
                      f"{indent}    <GeometryDrawing.Geometry>", f"{indent}        {element_xaml}",
                      f"{indent}    </GeometryDrawing.Geometry>",
                      f"{indent}    <GeometryDrawing.Brush>", *painted, f"{indent}    </GeometryDrawing.Brush>",
                      f"{indent}</GeometryDrawing>"]
    return lines


def to_drawing(svg, name):
    root = ET.fromstring(svg)
    width, height = (float(x) for x in root.get("viewBox").split()[2:])
    gradients = {g.get("id"): g for tag in ("linearGradient", "radialGradient")
                 for g in root.iter(f"{SVG}{tag}")}
    indent = "            "
    return [
        f'        <DrawingGroup ClipGeometry="M0,0 H{fmt(width)} V{fmt(height)} H0 Z">',
        # A drawing's bounds are its ink, so an icon that stops short of its
        # canvas would be stretched to fill it. This pins the canvas.
        f'{indent}<GeometryDrawing Brush="Transparent" Geometry="M0,0 H{fmt(width)} V{fmt(height)} H0 Z" />',
        *drawings(root, gradients, 1.0, indent, name),
        "        </DrawingGroup>",
    ]


def pascal(name):
    return "".join(word.capitalize() for word in name.split("_"))


def fetch(path):
    with urllib.request.urlopen(f"{BASE}/{urllib.parse.quote(path)}", timeout=30) as response:
        return response.read().decode("utf-8")


def main():
    lines = [
        "<!--",
        f"    Fluent UI System Icons, Color style, {SIZE}px, at {COMMIT[:12]}; vendored.",
        "    Regenerate with build/icons/generate_color.py; do not hand-edit. Each",
        "    icon is a DrawingImage for an Image's Source: it carries its own colours,",
        "    so unlike the Lucide set it does not follow Foreground or the theme.",
        "-->",
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
    ]
    for icon, replaces in ICONS.items():
        folder = " ".join(word.capitalize() for word in icon.split("_"))
        svg = fetch(f"assets/{folder}/SVG/ic_fluent_{icon}_{SIZE}_color.svg")
        lines.append(f"    <!-- {icon}: stands in for {replaces} -->")
        lines.append(f'    <DrawingImage x:Key="ColorIcon{pascal(icon)}">')
        lines += to_drawing(svg, icon)
        lines.append("    </DrawingImage>")
        print(f"  {icon:<18} {replaces}")
    lines.append("</ResourceDictionary>")
    OUTPUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    LICENCE.write_text(fetch("LICENSE"), encoding="utf-8")
    print(f"\nwrote {len(ICONS)} icons to {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
