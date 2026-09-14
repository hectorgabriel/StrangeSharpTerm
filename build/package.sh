#!/bin/sh
# Builds StrangeSharpTerm.app, and a disk image around it.
#
# The sequence is transcribed from the Swift app's Scripts/package.sh
# (docs/reference/package.swift-original.sh) rather than rediscovered, because
# the order is the part that is easy to get wrong and expensive to debug: sign
# inside out, verify every nested binary, notarise the image rather than the app,
# staple, then ask Gatekeeper rather than trusting our own opinion of it.
#
#   ./build/package.sh                       # ad-hoc signed, this machine only
#   ./build/package.sh --sign-with "Developer ID Application: Name (TEAMID)"
#   ./build/package.sh --notarize            # implies Developer ID; needs an account
#   ./build/package.sh --arch osx-x64        # the other Mac
#
# Notarisation needs credentials stored once, which is an account operation:
#
#   xcrun notarytool store-credentials StrangeSharpTerm \
#       --apple-id you@example.com --team-id TEAMID --password APP_SPECIFIC_PASSWORD
#
# What a free Apple ID cannot do: a Personal Team issues Apple Development
# certificates only, never a Developer ID, and notarisation is gated on paid
# membership. An ad-hoc build runs here and nowhere else, which is the honest
# outcome rather than a broken one. See docs/adr/0008.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$ROOT"

APP_NAME="StrangeSharpTerm"
BUNDLE="$APP_NAME.app"
ARCH="osx-$(uname -m | sed 's/arm64/arm64/; s/x86_64/x64/')"
IDENTITY="-"
NOTARIZE=0
KEYCHAIN_PROFILE="${STRANGESHARPTERM_NOTARY_PROFILE:-StrangeSharpTerm}"
VERSION="${STRANGESHARPTERM_VERSION:-0.1.0}"

while [ $# -gt 0 ]; do
    case "$1" in
        --sign-with) IDENTITY="$2"; shift 2 ;;
        --notarize) NOTARIZE=1; shift ;;
        --arch) ARCH="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        *) printf 'unknown argument: %s\n' "$1" >&2; exit 2 ;;
    esac
done

say() { printf '\n== %s\n' "$1"; }
die() { printf 'error: %s\n' "$1" >&2; exit 1; }

[ "$(uname)" = "Darwin" ] || die "this builds a macOS bundle and needs macOS."

OUT="$ROOT/artifacts/$ARCH"
APP="$OUT/$BUNDLE"

# ------------------------------------------------------------ the identity
say "signing identity"
if [ "$NOTARIZE" -eq 1 ] && [ "$IDENTITY" = "-" ]; then
    # A Developer ID certificate is a different thing from the Apple Development
    # one a free account issues, and only it produces something another machine
    # will run.
    IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null \
        | grep "Developer ID Application" | head -1 | sed 's/.*"\(.*\)"/\1/') || true
    [ -n "$IDENTITY" ] || die "no Developer ID Application certificate found.
       A free Apple ID (Personal Team) cannot issue one, and notarisation needs
       paid membership. Drop --notarize to build an ad-hoc image for this machine."
fi

if [ "$IDENTITY" = "-" ]; then
    printf 'ad-hoc: this build will run on this machine and be refused on every other.\n'
else
    printf '%s\n' "$IDENTITY"
fi

if [ "$NOTARIZE" -eq 1 ]; then
    xcrun notarytool history --keychain-profile "$KEYCHAIN_PROFILE" >/dev/null 2>&1 \
        || die "no notarytool credentials under profile '$KEYCHAIN_PROFILE'.
       See the header of this script for the store-credentials command."
fi

# ------------------------------------------------------------------- publish
say "publishing $ARCH"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# No debug symbols in a shipped bundle. They are not useful without the build
# that made them, and every file under Contents/MacOS is one more thing codesign
# insists on signing.
dotnet publish src/StrangeSharpTerm.App/StrangeSharpTerm.App.csproj \
    -c Release -r "$ARCH" --self-contained true \
    -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false \
    -o "$APP/Contents/MacOS" \
    --nologo >"$OUT/publish.log" 2>&1 || { tail -30 "$OUT/publish.log"; die "publish failed"; }

[ -x "$APP/Contents/MacOS/$APP_NAME" ] || die "no executable at Contents/MacOS/$APP_NAME"

# --------------------------------------------------------------------- icon
# Optional. There is no icon source in this repo yet; when one arrives,
# build/make-icon.sh turns it into AppIcon.icns and this picks it up.
ICON_LINE=""
if [ -f "$ROOT/build/mac/AppIcon.icns" ]; then
    cp "$ROOT/build/mac/AppIcon.icns" "$APP/Contents/Resources/"
    ICON_LINE="<key>CFBundleIconFile</key>
    <string>AppIcon</string>"
else
    printf 'no build/mac/AppIcon.icns; the bundle takes the default icon.\n'
fi

say "writing Info.plist"
python3 - "$ROOT/build/mac/Info.plist" "$APP/Contents/Info.plist" "$VERSION" "$ICON_LINE" <<'PY'
import sys, pathlib
template, target, version, icon = sys.argv[1:5]
text = pathlib.Path(template).read_text()
pathlib.Path(target).write_text(text.replace("@VERSION@", version).replace("@ICON@", icon))
PY

# ------------------------------------------------------------------ signing
#
# Three things here were each learned by the build failing, and none of them is
# obvious from the outside.
#
# Everything under Contents/MacOS is code as far as codesign is concerned -- not
# only the Mach-O files, but the managed assemblies and anything else living
# there. A .NET publish has to put its payload beside the apphost, so all of it
# lands in Contents/MacOS and all of it has to be signed. Signing only the
# dylibs fails on the first managed .dll; signing the .dlls too fails on the
# next thing along.
#
# The order is inside out. A bundle's signature covers what is inside it, so a
# nested file signed afterwards invalidates the bundle.
#
# And library validation: a process will only load libraries whose Team ID
# matches its own. Ad-hoc signatures have no Team ID at all, so an ad-hoc bundle
# signs and verifies perfectly and then dies on launch, unable to load
# libhostfxr. Turning library validation off is the price of an ad-hoc build; a
# Developer ID build signs every library with the same identity and does not need
# it, which is why this is added only when it is.
ENTITLEMENTS="$ROOT/build/mac/Entitlements.plist"
if [ "$IDENTITY" = "-" ]; then
    ENTITLEMENTS="$OUT/Entitlements.adhoc.plist"
    sed 's|</dict>|    <key>com.apple.security.cs.disable-library-validation</key>\
    <true/>\
</dict>|' "$ROOT/build/mac/Entitlements.plist" > "$ENTITLEMENTS"
fi

NESTED="$OUT/nested.txt"
find "$APP/Contents/MacOS" -type f ! -name "$APP_NAME" > "$NESTED"

# Only the Mach-O files are loaded by dyld, and only they have a Team ID worth
# comparing. Kept apart from the list above, which is everything codesign
# insists on signing.
MACHO="$OUT/macho.txt"
while IFS= read -r file; do
    file -b "$file" | grep -q "Mach-O" && printf '%s\n' "$file"
done < "$NESTED" > "$MACHO"

# A timestamp is a network round trip per signature, and there are a couple of
# hundred of them. It is required for notarisation and worthless on an ad-hoc
# signature nothing will trust anyway, so it is spent only where it buys
# something.
STAMP="--timestamp"
[ "$IDENTITY" = "-" ] && STAMP="--timestamp=none"

say "signing $(wc -l < "$NESTED" | tr -d ' ') nested files"
# In batches: codesign takes many paths, and the per-process cost over a few
# hundred files is most of the wall clock.
tr '\n' '\0' < "$NESTED" | xargs -0 -n 40 codesign --force $STAMP --options runtime \
    --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" >/dev/null 2>&1 \
    || die "signing a nested file failed"

say "signing the bundle"
codesign --force $STAMP --options runtime \
    --entitlements "$ENTITLEMENTS" \
    --sign "$IDENTITY" "$APP" || die "signing the bundle failed"

# ------------------------------------------------------------- verification
#
# Individually, not only --deep. --deep validates what it walks, and the failure
# worth catching is the one file the walk missed: notarisation rejects a whole
# submission for a single unsigned nested file and says so unhelpfully.
say "verifying every nested file"
tr '\n' '\0' < "$NESTED" | xargs -0 -n 40 codesign --verify --strict \
    || die "a nested file is not correctly signed"

codesign --verify --deep --strict --verbose=2 "$APP" 2>&1 | tail -2

say "checking the JIT entitlements survived"
# Signed without these, the app is notarised and then killed by the kernel the
# moment the runtime compiles anything.
codesign -d --entitlements - --xml "$APP" 2>/dev/null | grep -q "allow-jit" \
    || die "the bundle is signed without com.apple.security.cs.allow-jit"

say "checking it starts"
# --version loads the runtime and exits without opening a window. A signature
# being valid says nothing about this, and it is the step that catches a publish
# that is wrong in a way codesign cannot see.
"$APP/Contents/MacOS/$APP_NAME" --version >"$OUT/launch.log" 2>&1 \
    || { tail -3 "$OUT/launch.log"; die "the bundle is signed but will not start"; }
printf 'version %s\n' "$(cat "$OUT/launch.log")"

say "checking the runtime will load on another machine's terms"
#
# A valid signature says nothing about the app starting. A process loads only
# libraries whose Team ID matches its own, so an ad-hoc bundle -- which has no
# Team ID at all -- signs and verifies perfectly and then dies on launch, unable
# to open libhostfxr. Either library validation is off, or every binary shares
# one Team ID; anything else is a bundle that will not run, and finding that out
# here is much cheaper than finding it out after notarisation.
if codesign -d --entitlements - --xml "$APP" 2>/dev/null | grep -q "disable-library-validation"; then
    printf 'library validation is off, so the Team IDs need not match.\n'
else
    TEAM=$(codesign -dv --verbose=4 "$APP" 2>&1 | sed -n 's/^TeamIdentifier=//p')
    [ -n "$TEAM" ] && [ "$TEAM" != "not set" ] \
        || die "the bundle has no Team ID and library validation is on: it will not load its own runtime."

    MISMATCHED=0
    while IFS= read -r nested; do
        NESTED_TEAM=$(codesign -dv --verbose=4 "$nested" 2>&1 | sed -n 's/^TeamIdentifier=//p')
        [ "$NESTED_TEAM" = "$TEAM" ] || {
            printf 'Team ID %s (want %s): %s\n' "${NESTED_TEAM:-none}" "$TEAM" "$nested"
            MISMATCHED=$((MISMATCHED + 1))
        }
    done < "$MACHO"
    [ "$MISMATCHED" -eq 0 ] || die "$MISMATCHED libraries have the wrong Team ID; the app would not start"
    printf 'every binary is team %s.\n' "$TEAM"
fi

# ---------------------------------------------------------------------- dmg
say "building the disk image"
STAGE="$OUT/stage"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

DMG="$OUT/$APP_NAME-$ARCH.dmg"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO "$DMG" \
    >/dev/null || die "hdiutil failed"
rm -rf "$STAGE"

if [ "$NOTARIZE" -eq 0 ]; then
    say "done"
    printf '%s\n' "$DMG"
    [ "$IDENTITY" = "-" ] && printf \
        'Ad-hoc signed: this runs here and is refused on every other machine.\n'
    exit 0
fi

# ---------------------------------------------------------------- notarising
say "signing the disk image"
codesign --force --sign "$IDENTITY" --timestamp "$DMG" || die "signing the image failed"

say "notarising (this takes a few minutes)"
xcrun notarytool submit "$DMG" --keychain-profile "$KEYCHAIN_PROFILE" --wait \
    || die "notarisation failed; run 'xcrun notarytool log' with the submission id"

say "stapling"
xcrun stapler staple "$DMG" || die "stapling failed"

say "verifying as a fresh machine would"
# Gatekeeper's own assessment, not our opinion of the signature.
spctl --assess --type open --context context:primary-signature -vv "$DMG" 2>&1 | tail -3

say "done"
printf '%s\n' "$DMG"
