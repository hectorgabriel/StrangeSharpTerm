# 2. Terminal emulation

Status: proposed — pending the spike in `spikes/`

## Context

SwiftTerm's `LocalProcessTerminalView` owned a local pty running `ssh`, drew the
terminal, and handled resize via `TIOCSWINSZ`. The app never saw an output byte.
There is no SwiftTerm for .NET.

## Decision

**XTerm.NET** for emulation: MIT, headless, and explicitly byte-fed, so an SSH
channel can drive it with no pty in between. The server allocates the pty;
`ShellStream` carries the bytes; resize is a `window-change` channel request.
This is why the port is tractable on Windows at all — no ConPTY, no `openpty`.

For rendering, **Iciclecreek.Avalonia.Terminal** if its
`AttachConnection(IPtyConnection)` seam accepts an SSH-backed shim; otherwise our
own Avalonia control over the same XTerm.NET engine. The emulator is not at risk
either way — only the renderer is in question.

## The contract to preserve

| StrangeTerm | Here |
|---|---|
| `startProcess(executable:args:)` | `SshClient.CreateShellStream` |
| `BroadcastingTerminalView.send` override | intercept input before writing to `ShellStream` |
| `TerminalRegistry.visibleText` / `recentText` | `terminal.Buffer.Lines` |
| `TerminalRegistry.send(text:to:)` | write to `ShellStream` |
| `installColors` for 16 ANSI slots | `TerminalOptions` palette |
| `setTerminalTitle`, `processTerminated` | title event, stream close |
| `sizeChanged` — a no-op, the library's job | **ours now**: send `window-change` |

Resize is the one thing that was free and no longer is.

`TerminalRegistry` itself does not port. It was a weak-reference singleton that
existed only because `NSViewRepresentable` hands back no view reference; an
Avalonia control is an ordinary bound object.
