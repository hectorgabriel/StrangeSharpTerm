namespace StrangeSharpTerm.Transport;

/// <summary>
/// Somewhere to run one command and read what it said.
///
/// The seam the assistant is written against, as <see cref="IServerHealth"/> is
/// for the dashboard and <see cref="IRemoteFiles"/> for the browser. What a
/// gate does with a command should not need a server to decide.
/// </summary>
public interface IRemoteCommands
{
    /// <summary>One exec on the host's existing session. Throws if it cannot be run at all.</summary>
    CommandResult Run(string command, TimeSpan timeout);
}

/// <summary><see cref="IRemoteCommands"/> over a session: one channel on the connection already made.</summary>
public sealed class SshCommands(SshNetSession session) : IRemoteCommands
{
    public CommandResult Run(string command, TimeSpan timeout) => session.Run(command, timeout);
}
