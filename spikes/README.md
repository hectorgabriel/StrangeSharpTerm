# Spikes

Throwaway programs that answer one risky question each, kept because the answer
matters more than the code. They are deliberately outside `StrangeSharpTerm.slnx`
and are not built by CI.

Both need a local sshd:

```sh
WORK=$(./build/local-sshd.sh start)
dotnet run --project spikes/SshSpike -- "$(id -un)" 127.0.0.1 22022 "$WORK/client" "$WORK"
dotnet run --project spikes/TerminalSpike -- "$(id -un)" 127.0.0.1 22022 "$WORK/client" --headless
./build/local-sshd.sh stop "$WORK"
```

Drop `--headless` on the terminal spike to watch the window.

## SshSpike — does SSH.NET cover what ControlMaster gave us?

**Yes.** All checks pass on macOS. It asserts, against a real sshd:

- the SHA256 host key fingerprint matches `ssh-keygen -lf` exactly
- three `exec`s cost exactly one authentication (the multiplexing property that
  was the entire point of ControlMaster)
- a pty shell channel carries bytes both ways, and resizes
- a local port forward carries traffic and releases its port on stop
- 700 KiB round-trips through SFTP matching by SHA-256 (the same payload size
  the Swift suite used, chosen to span several chunks)
- `SshNet.Agent` both enumerates agent identities **and authenticates with one**

To exercise the agent path, load a key first: `ssh-add -t 120 "$WORK/client"`.

**Still open:** all of this was verified on macOS only. On Windows the OpenSSH
agent is a named pipe rather than a Unix socket, and `SshNet.Agent` is the one
third-party dependency in the credential path. That is the last unclosed M0 risk.

## TerminalSpike — can Avalonia's terminal control be driven by SSH?

**Yes.** See `docs/adr/0002`. `SshPtyConnection.cs` is the shim and is written to
be lifted into `src/StrangeSharpTerm.Terminal/` largely as-is.
