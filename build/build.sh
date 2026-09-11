#!/bin/sh
# Build everything. Config as $1, default Debug.
set -eu
CONFIG="${1:-Debug}"
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec dotnet build "$ROOT/StrangeSharpTerm.slnx" -c "$CONFIG" --nologo
