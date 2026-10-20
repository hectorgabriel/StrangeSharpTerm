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
    /// <summary>
    /// One exec on the host's existing session. Throws if it cannot be run at
    /// all. One that runs past <paramref name="timeout"/> is stopped and comes
    /// back <see cref="CommandResult.Stopped"/>; cancelling stops it and throws.
    /// </summary>
    CommandResult Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same, with <paramref name="input"/> written to the command's
    /// standard input and the input then closed.
    ///
    /// No default implementation, on purpose. A default that ignored the input
    /// would be a silent answer -- sudo would sit waiting for a password that
    /// never came -- and this project has been bitten by exactly that shape of
    /// default before.
    /// </summary>
    CommandResult RunFeeding(string command, TimeSpan timeout, string input, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IRemoteCommands"/> over a session: one channel on the connection already made.</summary>
public sealed class SshCommands(SshNetSession session) : IRemoteCommands
{
    public CommandResult Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        session.Run(command, timeout, cancellationToken);

    public CommandResult RunFeeding(string command, TimeSpan timeout, string input, CancellationToken cancellationToken = default) =>
        session.RunFeeding(command, timeout, input, cancellationToken);
}
