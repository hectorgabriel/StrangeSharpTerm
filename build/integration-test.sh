#!/bin/sh
# End-to-end test of the transport layer against a real sshd.
#
# Ported from the Swift project's Scripts/integration-test.sh (kept verbatim in
# docs/reference/). It runs an unprivileged sshd on a high loopback port, the way
# OpenSSH's own regression suite does, so it needs neither Docker nor admin
# rights.
#
# The assertions that matter are unchanged in spirit: several operations must
# ride ONE connection and cost exactly ONE authentication. What changed is that a
# connection now lives inside one stctl process rather than in a background
# master, so the multiplexing checks happen within a single invocation.
#
# Added here, because the rule is load-bearing and was never asserted end to end:
# an unknown key is refused unless trust is granted, a granted key is recorded in
# OpenSSH's own file, and a CHANGED key is refused even after that.
set -eu

PORT="${PORT:-22022}"
FORWARD_PORT="${FORWARD_PORT:-18080}"
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
FAILURES=0

check() {
    if [ "$2" = "$3" ]; then
        printf '  ok    %s (%s)\n' "$1" "$2"
    else
        printf '  FAIL  %s: expected %s, got %s\n' "$1" "$3" "$2"
        FAILURES=$((FAILURES + 1))
    fi
}

echo "building stctl..."
dotnet build "$ROOT/src/stctl/stctl.csproj" -c Release --nologo >/dev/null
STCTL="$ROOT/src/stctl/bin/Release/net10.0/stctl"

echo "starting sshd on 127.0.0.1:$PORT ..."
WORK=$("$ROOT/build/local-sshd.sh" start)
trap '"$ROOT/build/local-sshd.sh" stop "$WORK" >/dev/null 2>&1 || true' EXIT INT TERM

TARGET="$(id -un)@127.0.0.1:$PORT"
# A fresh host key per run, so the trust store is per run too. It also keeps
# loopback test hosts out of the developer's own ~/.ssh/known_hosts.
run() { "$STCTL" --key "$WORK/client" --known-hosts "$WORK/known_hosts" "$@"; }
auths() { grep -c 'Accepted publickey' "$WORK/sshd.log" 2>/dev/null || echo 0; }

echo
echo "host keys:"
check "an unknown host key is refused" \
    "$(run exec "$TARGET" true >/dev/null 2>&1 && echo connected || echo refused)" "refused"
check "nothing was recorded for a refused key" \
    "$(test -s "$WORK/known_hosts" && echo recorded || echo empty)" "empty"

run --insecure exec "$TARGET" true >/dev/null
check "an accepted key is recorded in OpenSSH's own format" \
    "$(ssh-keygen -F "[127.0.0.1]:$PORT" -f "$WORK/known_hosts" >/dev/null 2>&1 && echo found || echo missing)" "found"
check "a recorded key needs no further trust" \
    "$(run exec "$TARGET" true >/dev/null 2>&1 && echo connected || echo refused)" "connected"

# Swap in a different key of the same type: what an interception looks like.
CLIENT_KEY="$(awk '{print $2}' "$WORK/client.pub")"
awk -v k="$CLIENT_KEY" '{ $3 = k; print }' "$WORK/known_hosts" > "$WORK/known_hosts.changed"
check "a changed host key is refused" \
    "$("$STCTL" --key "$WORK/client" --known-hosts "$WORK/known_hosts.changed" exec "$TARGET" true >/dev/null 2>&1 \
        && echo connected || echo refused)" "refused"
check "and refused even when told to accept new keys" \
    "$("$STCTL" --key "$WORK/client" --known-hosts "$WORK/known_hosts.changed" --insecure exec "$TARGET" true \
        >/dev/null 2>&1 && echo connected || echo refused)" "refused"

echo
echo "multiplexing:"
check "remote command output" "$(run exec "$TARGET" echo multiplexed)" "multiplexed"
BEFORE="$(auths)"
run exec --repeat 3 "$TARGET" true >/dev/null
check "three execs on one connection cost one authentication" "$(( $(auths) - BEFORE ))" "1"

# The path a host's password takes to sudo. cat reads its input to the end, so
# it only comes back if the input really was closed after the write: left open,
# it would wait for more until the timeout, and so would sudo's command.
check "input reaches the command, and is closed after it" \
    "$(run exec --input 'fed-through-stdin' "$TARGET" -- cat)" "fed-through-stdin"

echo
echo "tunnels:"
FORWARD_OUT="$(run forward --check "$TARGET" -L "$FORWARD_PORT:127.0.0.1:$PORT" || true)"
check "traffic flows through the forward" \
    "$(printf '%s\n' "$FORWARD_OUT" | sed -n 's/^banner=\(SSH-2.0-\).*/\1/p')" "SSH-2.0-"
check "the forward released its port" \
    "$(printf '%s\n' "$FORWARD_OUT" | sed -n 's/^released=//p')" "yes"

echo
echo "terminal:"
# --dump-terminal is the only way to assert on rendered content: this is a real
# pty on the server, interpreted by our own engine, not a captured stdout.
check "the terminal renders what the shell prints" \
    "$(run terminal "$TARGET" --run 'echo MARKER_$((6*7))' --expect MARKER_42 >/dev/null 2>&1 \
        && echo rendered || echo missing)" "rendered"
check "resize reaches the remote pty" \
    "$(run terminal "$TARGET" --resize 120x40 --run 'tput cols' --expect 120 >/dev/null 2>&1 \
        && echo 120 || echo other)" "120"

echo
echo "sftp:"
SANDBOX="$WORK/remote"
mkdir -p "$SANDBOX"
# Big enough to span several chunks, so the pipelined path is exercised rather
# than a single request. The same size the Swift suite used.
dd if=/dev/urandom of="$WORK/payload.bin" bs=1024 count=700 2>/dev/null
run sftp put "$TARGET" "$WORK/payload.bin" "$SANDBOX/uploaded.bin" >/dev/null
check "upload lands on the server" \
    "$(test -f "$SANDBOX/uploaded.bin" && echo yes || echo no)" "yes"
check "uploaded bytes match" \
    "$(shasum -a 256 < "$WORK/payload.bin" | cut -d' ' -f1)" \
    "$(shasum -a 256 < "$SANDBOX/uploaded.bin" | cut -d' ' -f1)"

run sftp get "$TARGET" "$SANDBOX/uploaded.bin" "$WORK/downloaded.bin" >/dev/null
check "700 KiB round-trips by SHA-256" \
    "$(shasum -a 256 < "$WORK/payload.bin" | cut -d' ' -f1)" \
    "$(shasum -a 256 < "$WORK/downloaded.bin" | cut -d' ' -f1)"

echo
echo "workspace:"
# The rule the pane and the assistant's file tools both go through: a root, and
# nothing outside it. Proved against a real server, because a path that climbs
# out of the folder is exactly the case a unit test can only check as text.
WS="$SANDBOX/project"
mkdir -p "$WS/conf"
printf 'server {\n  listen 80;\n}\n' > "$WS/conf/nginx.conf"
check "a file inside the folder reads back" \
    "$(run workspace "$TARGET" --root "$WS" --cat conf/nginx.conf | tail -3 | head -1)" \
    "server {"
check "a file saved from the workspace lands on the server" \
    "$(run workspace "$TARGET" --root "$WS" --write conf/extra.conf --content 'listen 8080;' >/dev/null; \
        cat "$WS/conf/extra.conf")" \
    "listen 8080;"
check "a path climbing out of the folder is refused" \
    "$(run workspace "$TARGET" --root "$WS" --cat ../../../../etc/hostname | grep -c '^refused=')" \
    "1"
check "an absolute path elsewhere is refused too" \
    "$(run workspace "$TARGET" --root "$WS" --cat /etc/hostname | grep -c '^refused=')" \
    "1"

echo
echo "failure classification:"
check "a bad user reports authentication, not a mystery" \
    "$(run exec "nosuchuser@127.0.0.1:$PORT" true 2>&1 >/dev/null | head -1)" \
    "Authentication failed. Check the username, key, or agent."
check "an unresolvable host says so" \
    "$(run exec "$(id -un)@no-such-host.invalid:$PORT" true 2>&1 >/dev/null | head -1)" \
    "The hostname could not be resolved."
check "a refused port says so" \
    "$(run exec "$(id -un)@127.0.0.1:$((PORT + 1))" true 2>&1 >/dev/null | head -1)" \
    "The connection was refused. Check the port and that sshd is running."

echo
echo "ssh-agent:"
# The preferred credential method, and the one with no first-party support in
# SSH.NET. Authentication here uses no --key: only what the agent will sign.
AGENT_STARTED=no
if [ -z "${SSH_AUTH_SOCK:-}" ]; then
    eval "$(ssh-agent -s)" >/dev/null 2>&1 && AGENT_STARTED=yes
fi
ssh-add "$WORK/client" >/dev/null 2>&1 || true
check "authenticates with an agent-held key" \
    "$("$STCTL" --known-hosts "$WORK/known_hosts" exec "$TARGET" echo agent-ok 2>/dev/null || echo failed)" \
    "agent-ok"
ssh-add -d "$WORK/client" >/dev/null 2>&1 || true
[ "$AGENT_STARTED" = yes ] && ssh-agent -k >/dev/null 2>&1 || true

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "all checks passed"
else
    echo "$FAILURES check(s) failed"
fi
exit "$FAILURES"
