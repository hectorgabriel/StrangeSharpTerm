#!/bin/sh
# Builds StrangeSharpTerm and installs it into /Applications, for your own Mac.
#
#   ./build/install.sh              # build, sign ad-hoc, install
#   ./build/install.sh --uninstall  # undo it
#
# No Developer ID certificate and no notarisation are needed for this: they are
# what a *second* machine requires, and this is the first one. The app is signed
# ad-hoc, which is not optional -- on Apple Silicon an unsigned binary will not
# execute at all -- and is exactly as much signing as running it here needs.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
APP_NAME="StrangeSharpTerm"
TARGET="/Applications/$APP_NAME.app"

if [ "${1:-}" = "--uninstall" ]; then
    if [ -d "$TARGET" ]; then
        rm -rf "$TARGET"
        printf 'removed %s\n' "$TARGET"
    else
        printf 'nothing installed at %s\n' "$TARGET"
    fi
    # Deliberately left alone: the inventory, the preferences beside it, and
    # anything in the login keychain. Uninstalling an app should not throw away
    # the servers someone spent an afternoon describing.
    printf 'Your inventory, preferences and saved secrets are untouched.\n'
    exit 0
fi

ARCH="osx-$(uname -m | sed 's/x86_64/x64/')"
"$ROOT/build/package.sh" --arch "$ARCH" >/dev/null

BUILT="$ROOT/artifacts/$ARCH/$APP_NAME.app"
[ -d "$BUILT" ] || { printf 'error: the build produced no bundle\n' >&2; exit 1; }

rm -rf "$TARGET"
cp -R "$BUILT" "$TARGET"
printf 'installed %s\n' "$TARGET"
printf 'open it with: open -a %s\n' "$APP_NAME"
