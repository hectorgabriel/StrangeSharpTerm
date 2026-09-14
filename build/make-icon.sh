#!/bin/sh
# Turns an SVG into the icons both platforms want.
#
#   ./build/make-icon.sh build/mac/logo.svg
#
# Writes build/mac/AppIcon.icns and build/windows/AppIcon.ico, which
# build/package.sh and build/package.ps1 pick up when they are there and do
# without when they are not.
#
# Every size is rendered from the vector rather than downscaled from one bitmap:
# the Swift app's note about this is worth keeping, because a glow and rounded
# corners go muddy at 16pt when they are resampled rather than drawn.
#
# Needs librsvg for the rendering: brew install librsvg
set -eu

SOURCE="${1:-}"
[ -n "$SOURCE" ] || { printf 'usage: %s <source.svg>\n' "$0" >&2; exit 2; }
[ -f "$SOURCE" ] || { printf 'error: no file at %s\n' "$SOURCE" >&2; exit 1; }

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
command -v rsvg-convert >/dev/null 2>&1 || {
    printf 'error: rsvg-convert is required: brew install librsvg\n' >&2
    exit 1
}

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
SET="$WORK/AppIcon.iconset"
mkdir -p "$SET"

# The sizes iconutil insists on, each rendered rather than resampled.
for size in 16 32 128 256 512; do
    rsvg-convert -w "$size" -h "$size" "$SOURCE" -o "$SET/icon_${size}x${size}.png"
    rsvg-convert -w $((size * 2)) -h $((size * 2)) "$SOURCE" -o "$SET/icon_${size}x${size}@2x.png"
done

mkdir -p "$ROOT/build/mac" "$ROOT/build/windows"
iconutil -c icns "$SET" -o "$ROOT/build/mac/AppIcon.icns"
printf 'wrote build/mac/AppIcon.icns\n'

# An .ico is a container of PNGs; sips cannot write one, so this needs either
# ImageMagick or Windows. Skipped rather than faked when neither is here.
if command -v magick >/dev/null 2>&1; then
    magick "$SET/icon_16x16.png" "$SET/icon_32x32.png" "$SET/icon_128x128.png" \
        "$SET/icon_256x256.png" "$ROOT/build/windows/AppIcon.ico"
    printf 'wrote build/windows/AppIcon.ico\n'
else
    printf 'no ImageMagick, so no .ico was written: brew install imagemagick\n'
fi
