#!/bin/sh
# End-to-end test of the ControlMaster supervisor against a real sshd.
#
# Runs an unprivileged sshd on a high port bound to loopback, the way OpenSSH's
# own regression suite does, so this needs neither Docker nor admin rights.
#
# The assertions that matter are the multiplexing ones: several commands and a
# port forward must all ride ONE connection and cost exactly ONE authentication.
set -eu

PORT="${PORT:-22022}"
FORWARD_PORT="${FORWARD_PORT:-18080}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
USER_NAME="$(id -un)"
FAILURES=0

cleanup() {
    [ -f "$WORK/sshd.pid" ] && kill "$(cat "$WORK/sshd.pid")" 2>/dev/null || true
    pkill -f "sshd -f $WORK/sshd_config" 2>/dev/null || true
    rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

check() {
    if [ "$2" = "$3" ]; then
        printf '  ok    %s (%s)\n' "$1" "$2"
    else
        printf '  FAIL  %s: expected %s, got %s\n' "$1" "$3" "$2"
        FAILURES=$((FAILURES + 1))
    fi
}

echo "building..."
cd "$ROOT/Packages/StrangeTermCore"
swift build --product stctl >/dev/null
STCTL="$(swift build --show-bin-path)/stctl"

echo "starting sshd on 127.0.0.1:$PORT ..."
chmod 700 "$WORK"
ssh-keygen -t ed25519 -f "$WORK/host" -N '' -q
ssh-keygen -t ed25519 -f "$WORK/client" -N '' -q
cp "$WORK/client.pub" "$WORK/authorized_keys"
chmod 600 "$WORK/authorized_keys" "$WORK/host" "$WORK/client"

cat > "$WORK/sshd_config" <<CFG
Port $PORT
ListenAddress 127.0.0.1
HostKey $WORK/host
AuthorizedKeysFile $WORK/authorized_keys
StrictModes no
UsePAM no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
PidFile $WORK/sshd.pid
LogLevel VERBOSE
AllowTcpForwarding yes
Subsystem sftp /usr/libexec/sftp-server
AllowUsers $USER_NAME
CFG

/usr/sbin/sshd -f "$WORK/sshd_config" -D -e >"$WORK/sshd.log" 2>&1 &
i=0
while [ $i -lt 40 ]; do
    nc -z 127.0.0.1 "$PORT" 2>/dev/null && break
    i=$((i + 1))
done
nc -z 127.0.0.1 "$PORT" 2>/dev/null || { echo "sshd failed to start"; tail -5 "$WORK/sshd.log"; exit 1; }

TARGET="$USER_NAME@127.0.0.1:$PORT"
# A fresh host key is generated per run, so the trust store must be per run too.
# It also keeps loopback test hosts out of the developer's ~/.ssh/known_hosts.
run() { "$STCTL" --key "$WORK/client" --known-hosts "$WORK/known_hosts" --insecure "$@"; }
auths() { count=$(grep -c 'Accepted publickey' "$WORK/sshd.log" 2>/dev/null) || count=0; echo "$count"; }

echo
echo "connection:"
run connect "$TARGET" >/dev/null
check "master is alive" "$(run status "$TARGET" || true)" "alive"
check "one master process" "$(pgrep -f '[s]sh -M -N' | wc -l | tr -d ' ')" "1"

BASELINE="$(auths)"
[ -n "$BASELINE" ] || BASELINE=0
echo
echo "multiplexing:"
check "remote command output" "$(run exec "$TARGET" -- echo multiplexed || true)" "multiplexed"
run exec "$TARGET" -- uname -s >/dev/null
run exec "$TARGET" -- id -un >/dev/null
check "no re-authentication for 3 execs" "$(( $(auths) - BASELINE ))" "0"

echo
echo "tunnels:"
run forward "$TARGET" -L "$FORWARD_PORT:127.0.0.1:$PORT" >/dev/null
BANNER="$(nc -w 2 127.0.0.1 "$FORWARD_PORT" 2>/dev/null | head -1 | cut -c1-8)"
check "traffic flows through forward" "$BANNER" "SSH-2.0-"
run cancel "$TARGET" -L "$FORWARD_PORT:127.0.0.1:$PORT" >/dev/null
nc -z 127.0.0.1 "$FORWARD_PORT" 2>/dev/null && CLOSED=open || CLOSED=closed
check "forward released the port" "$CLOSED" "closed"
check "tunnel work caused no reconnect" "$(( $(auths) - BASELINE ))" "0"

echo
echo "sftp:"
SANDBOX="$WORK/remote"
mkdir -p "$SANDBOX"
# A file big enough to span several 32 KiB chunks, so the pipelined read path
# is actually exercised rather than a single request.
dd if=/dev/urandom of="$WORK/payload.bin" bs=1024 count=700 2>/dev/null
SFTP_BASELINE="$(auths)"

run put "$TARGET" "$WORK/payload.bin" "$SANDBOX/uploaded.bin" >/dev/null
check "upload lands on the server" "$(test -f "$SANDBOX/uploaded.bin" && echo yes || echo no)" "yes"
check "uploaded bytes match" \
    "$(shasum -a 256 < "$WORK/payload.bin" | cut -d' ' -f1)" \
    "$(shasum -a 256 < "$SANDBOX/uploaded.bin" | cut -d' ' -f1)"

run get "$TARGET" "$SANDBOX/uploaded.bin" "$WORK/downloaded.bin" >/dev/null
check "round-tripped bytes match" \
    "$(shasum -a 256 < "$WORK/payload.bin" | cut -d' ' -f1)" \
    "$(shasum -a 256 < "$WORK/downloaded.bin" | cut -d' ' -f1)"

run mkdir "$TARGET" "$SANDBOX/subdir" >/dev/null
check "directory listing sees both entries" \
    "$(run ls "$TARGET" "$SANDBOX" 2>/dev/null | wc -l | tr -d ' ')" "2"
check "directories are listed first" \
    "$(run ls "$TARGET" "$SANDBOX" 2>/dev/null | head -1 | cut -c1)" "d"

run rm "$TARGET" "$SANDBOX/uploaded.bin" >/dev/null
check "delete removes the file" "$(test -f "$SANDBOX/uploaded.bin" && echo yes || echo no)" "no"

check "a missing file reports why" \
    "$(run get "$TARGET" "$SANDBOX/nope" /dev/null 2>&1 | grep -c 'No such file')" "1"

check "all sftp work reused the master" "$(( $(auths) - SFTP_BASELINE ))" "0"

echo
echo "teardown:"
run disconnect "$TARGET" >/dev/null
check "master is gone" "$(pgrep -f '[s]sh -M -N' | wc -l | tr -d ' ')" "0"

echo
echo "failure classification:"
check "bad user" \
    "$(run connect "nosuchuser@127.0.0.1:$PORT" 2>&1 | grep -c 'Authentication failed')" "1"
check "bad hostname" \
    "$(run connect "x@no-such-host.invalid" 2>&1 | grep -c 'could not be resolved')" "1"
check "closed port" \
    "$(run connect "x@127.0.0.1:59999" 2>&1 | grep -c 'refused')" "1"

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "integration: all checks passed"
else
    echo "integration: $FAILURES check(s) failed"
    exit 1
fi
