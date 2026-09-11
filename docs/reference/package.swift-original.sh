#!/bin/sh
# Builds a distributable StrangeTerm.dmg.
#
# Distribution is Developer ID plus notarisation, not the App Store: the app and
# its connection agent are deliberately unsandboxed, which the store does not
# permit. See the architecture note in README.
#
#   ./Scripts/package.sh                  full release: sign, notarise, staple
#   ./Scripts/package.sh --skip-notarize  local check of everything but notarisation
#
# Notarisation needs credentials stored once, which is an account operation:
#
#   xcrun notarytool store-credentials StrangeTerm \
#       --apple-id you@example.com --team-id TEAMID --password APP_SPECIFIC_PASSWORD
set -eu

NOTARIZE=1
[ "${1:-}" = "--skip-notarize" ] && NOTARIZE=0

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
OUT="$ROOT/Distribution"
KEYCHAIN_PROFILE="${STRANGETERM_NOTARY_PROFILE:-StrangeTerm}"

say() { printf '\n== %s\n' "$1"; }
die() { printf 'error: %s\n' "$1" >&2; exit 1; }

# ---------------------------------------------------------------- prerequisites
say "checking prerequisites"
command -v xcodegen >/dev/null 2>&1 || die "xcodegen is required: brew install xcodegen"
[ -f Signing.local.xcconfig ] || die "Signing.local.xcconfig is missing; copy Support/Signing.example.xcconfig"

if [ "$NOTARIZE" -eq 1 ]; then
    # A Developer ID certificate is a different thing from the Apple Development
    # one used day to day, and only it produces something other machines will run.
    security find-identity -v -p codesigning 2>/dev/null | grep -q "Developer ID Application" \
        || die "no Developer ID Application certificate found.
       Day-to-day builds use an Apple Development certificate, which macOS will
       refuse on any machine but this one. Re-run with --skip-notarize to build
       an unnotarised disk image for local testing."

    xcrun notarytool history --keychain-profile "$KEYCHAIN_PROFILE" >/dev/null 2>&1 \
        || die "no notarytool credentials stored under profile '$KEYCHAIN_PROFILE'.
       See the header of this script for the store-credentials command."
fi

# ---------------------------------------------------------------------- build
say "building Release"
xcodegen generate --quiet
rm -rf "$OUT"
mkdir -p "$OUT"

# Archiving rather than a plain build: it is what applies the Release signing
# settings and strips the debug entitlements that stop launchd accepting the
# agent.
ARCHIVE="$OUT/StrangeTerm.xcarchive"
xcodebuild archive \
    -project StrangeTerm.xcodeproj \
    -scheme StrangeTerm \
    -configuration Release \
    -archivePath "$ARCHIVE" \
    -skipPackagePluginValidation \
    >"$OUT/build.log" 2>&1 || { tail -30 "$OUT/build.log"; die "archive failed"; }

APP="$ARCHIVE/Products/Applications/StrangeTerm.app"
[ -d "$APP" ] || die "no app in the archive"

say "verifying the signature"
codesign --verify --deep --strict --verbose=2 "$APP" 2>&1 | tail -2
# Everything nested must be signed too, or notarisation rejects the whole thing.
for nested in "$APP/Contents/PlugIns"/*.appex "$APP/Contents/MacOS/STConnectionAgent" \
              "$APP/Contents/MacOS/st-askpass"; do
    [ -e "$nested" ] || continue
    codesign --verify --strict "$nested" || die "unsigned or invalid: $nested"
done

# ------------------------------------------------------------------------ dmg
say "building the disk image"
STAGE="$OUT/stage"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

DMG="$OUT/StrangeTerm.dmg"
hdiutil create -volname "StrangeTerm" -srcfolder "$STAGE" -ov -format UDZO "$DMG" \
    >/dev/null || die "hdiutil failed"
rm -rf "$STAGE"

if [ "$NOTARIZE" -eq 0 ]; then
    say "done (unnotarised)"
    printf '%s\n' "$DMG"
    printf 'This image is signed for local use only. Other machines will refuse it.\n'
    exit 0
fi

# ---------------------------------------------------------------- notarisation
say "signing the disk image"
IDENTITY="$(security find-identity -v -p codesigning | grep "Developer ID Application" \
    | head -1 | sed 's/.*"\(.*\)"/\1/')"
codesign --force --sign "$IDENTITY" --timestamp "$DMG" || die "signing the image failed"

say "notarising (this takes a few minutes)"
xcrun notarytool submit "$DMG" --keychain-profile "$KEYCHAIN_PROFILE" --wait \
    || die "notarisation failed; run 'xcrun notarytool log' with the submission id"

say "stapling"
xcrun stapler staple "$DMG" || die "stapling failed"

say "verifying as a fresh machine would"
# The real test: Gatekeeper's own assessment, not our opinion of the signature.
spctl --assess --type open --context context:primary-signature -vv "$DMG" 2>&1 | tail -3

say "done"
printf '%s\n' "$DMG"
