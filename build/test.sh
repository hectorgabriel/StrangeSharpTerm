#!/bin/sh
# Run every test project.
#
# Two non-obvious arguments, both of which cost an hour to find once:
#
#   --solution is required. `dotnet new sln` on the .NET 10 SDK writes the newer
#   .slnx format, and a bare `dotnet test` does not discover it -- it reports
#   "Zero tests ran" with exit code 5, which reads like broken test projects
#   rather than a missing argument.
#
#   --nologo must NOT be passed. In Microsoft.Testing.Platform mode (opted into
#   by the "test" section of global.json) it is forwarded to the test executable,
#   which rejects it as an unknown option -- again exit code 5, again looking
#   like a test failure rather than a bad flag.
set -eu
CONFIG="${1:-Debug}"
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec dotnet test --solution "$ROOT/StrangeSharpTerm.slnx" -c "$CONFIG"
