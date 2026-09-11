#!/bin/sh
# Regenerates the Swift-side golden files in tests/StrangeSharpTerm.Store.Tests/Fixtures.
#
# Compiles main.swift together with the Swift app's own model and store sources,
# so the goldens come from the real JSONEncoder and the real ssh_config importer.
# Needs a Swift toolchain (macOS) and a checkout of the Swift StrangeTerm repo:
#
#   ./build/swift-parity/generate.sh ../StrangeTerm
#
# The goldens are committed; CI never runs this. Rerun it only when the Swift
# side is the thing being checked against.
set -eu
SWIFT_REPO=$(CDPATH= cd -- "${1:?usage: generate.sh <path to the Swift StrangeTerm repo>}" && pwd)
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
CORE="$SWIFT_REPO/Packages/StrangeTermCore/Sources"
FIXTURES="$ROOT/tests/StrangeSharpTerm.Store.Tests/Fixtures"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

cp "$CORE"/STModel/*.swift "$WORK/"
# Built as one module rather than two, so the store sources' `import STModel` goes.
for name in InventoryStore SSHConfigParser SSHConfigImporter; do
  sed '/^import STModel$/d' "$CORE/STStore/$name.swift" > "$WORK/$name.swift"
done
cp "$ROOT/build/swift-parity/main.swift" "$WORK/main.swift"

swiftc -O "$WORK"/*.swift -o "$WORK/generate"
"$WORK/generate" "$FIXTURES" "$FIXTURES/ssh_config.sample"
echo "Regenerated Swift goldens in $FIXTURES"
