# 2. Terminal emulation

Status: **accepted** — both halves proven by `spikes/TerminalSpike`

## Context

SwiftTerm's `LocalProcessTerminalView` owned a local pty running `ssh`, drew the
terminal, and handled resize via `TIOCSWINSZ`. The app never saw an output byte.
There is no SwiftTerm for .NET.

## Decision

**XTerm.NET** for emulation and **Iciclecreek.Avalonia.Terminal** for rendering,
with an SSH-backed `IPtyConnection` shim between them
(`spikes/TerminalSpike/SshPtyConnection.cs`).

There is no local pty anywhere in the design. The server allocates the pty,
SSH.NET's `ShellStream` carries the bytes, and `IPtyConnection` is only two
`Stream`s, a `Resize(cols, rows)`, and an exit signal. That is why this reaches
Windows: no ConPTY, no `openpty`, nothing platform-specific.

## What the spike proved

Against a real sshd on loopback:

- `AttachConnection` accepts an SSH-backed `IPtyConnection`. The fallback plan
  (our own Avalonia control over XTerm.NET) is not needed.
- Remote output renders correctly, including a themed zsh prompt.
- The buffer reads back as text through `TerminalControl.Terminal.Buffer.Lines`
  — this is what feeds the assistant its context, replacing
  `TerminalRegistry.visibleText`.
- Resize propagates to the remote pty end to end.

## Corrections to earlier assumptions

**Resize is not extra work.** The migration plan called it "the one thing that
was free and no longer is" and told us to budget for it. Wrong on both counts:
`ShellStream.ChangeWindowSize` sends the `window-change` request, and
`IPtyConnection.Resize` is the control calling it on layout. It is one line.

**`ssh-keyscan` is not needed.** `HostKeyEventArgs` carries `FingerPrintSHA256`
directly, and it matches `ssh-keygen -lf` byte for byte (asserted in
`spikes/SshSpike`). The Swift app shelled out to `ssh-keyscan` before connecting
because it needed a fingerprint to show the user; here the fingerprint arrives on
the connection attempt itself.

## Known limitation of the seam

`Porta.Pty.PtyExitedEventArgs` has an **internal** constructor, so no
`IPtyConnection` implemented outside that assembly can raise `ProcessExited`.
The shim declares the event and never fires it.

This costs nothing. The control's exit handling is about a local child process,
and there isn't one. We own the `SshClient`, so we know when a session ends and
surface it ourselves — `SshPtyConnection.Exited`. The "session exited" overlay
binds to that.

## The contract, as ported

| StrangeTerm | Here | Status |
|---|---|---|
| `startProcess(executable:args:)` | `SshClient.CreateShellStream` | proven |
| `BroadcastingTerminalView.send` override | `TerminalControl.InputSent` event | an event, not a subclass |
| `TerminalRegistry.visibleText` / `recentText` | `Terminal.Buffer.Lines` | proven |
| `TerminalRegistry.send(text:to:)` | write to `ShellStream` | proven |
| `installColors` for 16 ANSI slots | `TerminalOptions` palette | not yet exercised |
| `setTerminalTitle` | title event | not yet exercised |
| `processTerminated` | `SshPtyConnection.Exited` | ours, see above |
| `sizeChanged` — a no-op, the library's job | `IPtyConnection.Resize` | proven, still not ours |

`TerminalRegistry` itself does not port. It was a weak-reference singleton that
existed only because `NSViewRepresentable` hands back no view reference; an
Avalonia control is an ordinary bound object.
