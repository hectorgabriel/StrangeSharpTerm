namespace StrangeSharpTerm.Transport;

/// <summary>
/// Somewhere to ask a server how it is.
///
/// The seam the dashboard is written against, as <see cref="IRemoteFiles"/> is
/// for the browser: what a dashboard does with a server that will not answer is
/// a decision, and deciding it should not need a server.
/// </summary>
public interface IServerHealth
{
    /// <summary>One round trip. Throws if the host cannot be reached or the command fails.</summary>
    ServerMetrics Collect();
}

/// <summary>
/// <see cref="IServerHealth"/> over a session: one exec, parsed by the probe
/// that came across in M2 with its own tests.
/// </summary>
public sealed class SshServerHealth(SshNetSession session) : IServerHealth
{
    public ServerMetrics Collect() => ServerProbe.Parse(session.Run(ServerProbe.Command).StandardOutput);
}
