#!/usr/bin/env python3
"""Vendors a few 3dicons illustrations: the empty states, and three places.

3dicons (https://3dicons.co, Vijay Verma) are rendered in Blender and published
as 400x400 PNGs under CC0. They are pictures, not glyphs: at the 14px the
toolbar draws an icon they are a smudge, and they cannot take a theme's colour.
So they go where there is room and nothing else to look at -- a pane, a
window or a panel with nothing in it yet -- and the line icons stay Lucide.

Three small ones are the exception, by choice rather than fit: settings, the
assistant and the orchestrator, each a place of its own rather than an action,
wear the same pictures at 14 points.

    ./build/icons/fetch_3dicons.py       # re-download into Illustrations/

The PNGs are committed, so a build needs no network. 400 pixels covers the
empty states' 64 to 96 points at up to 4x; the small ones are scaled down with
high-quality filtering, or they shimmer.
"""
import sys
import urllib.request
from pathlib import Path

CDN = "https://3dicons.sgp1.cdn.digitaloceanspaces.com/v1"
# One of clay, color, gradient or premium -- the finish, for every icon at once
# so the set cannot drift into several. Gradient is the one with colour enough
# to be worth the trial; premium, dark metal and gold, is the quieter choice.
FINISH = "gradient"
# One of dynamic, front or iso: the camera angle, likewise for all of them.
ANGLE = "dynamic"
LICENCE_URL = "https://raw.githubusercontent.com/realvjy/3dicons/8884d59e68a7bae0e0e2163af5b6c2ad992c01c8/LICENSE"
ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "src/StrangeSharpTerm.App/Illustrations"

# 3dicons name -> where it is shown.
ICONS = {
    "computer": "the window, before a host is chosen",
    "chat-bubble": "the assistant, before the first question",
    "key": "the credential library, empty",
    "flash": "the snippet library, empty",
    "link": "a host with no tunnels",
    "folder": "the workspace, before a folder is chosen",
    # The three places, drawn small; chat-bubble above is the assistant's too.
    "setting": "settings, at the foot of the sidebar",
    "cube": "the orchestrator -- SF Symbols' square.stack.3d.up, in the Swift app",
}


def fetch(url):
    with urllib.request.urlopen(url, timeout=30) as response:
        return response.read()


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for icon, where in ICONS.items():
        data = fetch(f"{CDN}/{ANGLE}/{FINISH}/{icon}-{ANGLE}-{FINISH}.png")
        if not data.startswith(b"\x89PNG"):
            print(f"{icon} did not come back as a PNG", file=sys.stderr)
            return 1
        (OUTPUT / f"{icon}.png").write_bytes(data)
        print(f"  {icon:<12} {len(data) // 1024:>3} KB  {where}")
    (OUTPUT / "LICENSE-3dicons.txt").write_bytes(fetch(LICENCE_URL))
    print(f"\nwrote {len(ICONS)} illustrations to {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
