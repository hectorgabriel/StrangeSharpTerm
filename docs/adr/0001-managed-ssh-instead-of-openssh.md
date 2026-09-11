# 1. Managed SSH (SSH.NET) instead of driving the OpenSSH binary

Status: accepted

## Context

StrangeTerm spawned `/usr/bin/ssh` and multiplexed every terminal tab, SFTP
session, Finder mount and port forward over a single `ControlMaster` connection
per host. About 1,200 lines went into argv construction, control-socket path
budgeting (Darwin's `sun_path` is 104 bytes, and ssh's `%C` hash plus its
temp-rename suffix leave only 46 characters for the directory), handshake
polling, and classifying OpenSSH's stderr prose into failure cases — everything
exits 255, so the prose is the only signal.

Win32-OpenSSH does not implement `ControlMaster` or `ControlPath`.

## Decision

Use SSH.NET. One `SshClient` per host; shell channels, `SftpClient`,
`ForwardedPortLocal/Remote/Dynamic`, and `RunCommand` all ride it.

## Consequences

Deleted outright: the invocation builder, the ControlMaster supervisor, the
control-path arithmetic, the `ssh-keyscan` subprocess, the hand-written SFTP v3
client (690 lines), and the askpass FIFO channel with its helper binary — a
passphrase is now just a string passed in-process.

Kept, re-based: the failure taxonomy (now mapped from SSH.NET exception types
rather than stderr prose) and the server probe with its Linux and macOS parsers.

Built new: jump hosts. `ssh -J` becomes connect-to-jump, open a local forward,
connect the second client through it.

Not lost, contrary to first appearances: `~/.ssh/config` fidelity. The parser
and importer were always ours, not ssh's, and they port directly. What we do
lose is anything ssh resolves that our parser does not — `Match` blocks and
canonicalisation, both of which the importer already skipped.

Still true by choice: host keys are read from and written to the real
`~/.ssh/known_hosts`, so trust stays shared with the `ssh` CLI.
