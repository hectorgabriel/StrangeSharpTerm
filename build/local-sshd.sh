#!/bin/sh
# Start an unprivileged sshd on 127.0.0.1 for spikes and integration tests, the
# way OpenSSH's own regression suite does -- no Docker, no admin rights.
#
# Lifted from the Swift project's Scripts/integration-test.sh (kept in
# docs/reference/). Prints the work directory on stdout; everything the caller
# needs is in it: client key, known_hosts, sshd.log.
#
#   WORK=$(./build/local-sshd.sh start)
#   ./build/local-sshd.sh stop "$WORK"
set -eu
PORT="${PORT:-22022}"

case "${1:-start}" in
start)
    WORK="$(mktemp -d)"
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
AllowUsers $(id -un)
CFG
    /usr/sbin/sshd -f "$WORK/sshd_config" -D -e >"$WORK/sshd.log" 2>&1 &
    i=0
    while [ $i -lt 60 ]; do
        nc -z 127.0.0.1 "$PORT" 2>/dev/null && break
        i=$((i + 1))
    done
    if ! nc -z 127.0.0.1 "$PORT" 2>/dev/null; then
        echo "sshd failed to start:" >&2
        tail -5 "$WORK/sshd.log" >&2
        rm -rf "$WORK"
        exit 1
    fi
    echo "$WORK"
    ;;
stop)
    WORK="${2:?usage: local-sshd.sh stop <workdir>}"
    [ -f "$WORK/sshd.pid" ] && kill "$(cat "$WORK/sshd.pid")" 2>/dev/null || true
    pkill -f "sshd -f $WORK/sshd_config" 2>/dev/null || true
    rm -rf "$WORK"
    ;;
*)
    echo "usage: local-sshd.sh [start|stop <workdir>]" >&2
    exit 2
    ;;
esac
