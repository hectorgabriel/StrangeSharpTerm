using Renci.SshNet;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Terminal;

/// <summary>
/// The byte pipe a terminal is attached to, and the one thing a pipe must do
/// besides carry bytes: change size.
///
/// An interface rather than a <see cref="ShellStream"/> everywhere, so the engine
/// and everything above it can be tested without a server.
/// </summary>
public interface ITerminalChannel : IDisposable
{
    Stream Stream { get; }

    /// <summary>Tells the far end the window changed. On SSH this is a <c>window-change</c> request.</summary>
    void Resize(int columns, int rows);
}

/// <summary>
/// A terminal channel over an SSH shell.
///
/// The pty lives on the server, so there is no local one to allocate: no ConPTY
/// on Windows, no openpty on macOS, nothing platform-specific anywhere in this
/// file. That is the whole reason this design reaches Windows at all.
/// </summary>
public sealed class SshTerminalChannel(ShellStream shell) : ITerminalChannel
{
    public static SshTerminalChannel Open(
        SshNetSession session, string terminalName = "xterm-256color", int columns = 80, int rows = 24) =>
        new(session.OpenShell(terminalName, (uint)columns, (uint)rows));

    public Stream Stream => shell;

    public void Resize(int columns, int rows) => shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);

    public void Dispose() => shell.Dispose();
}
