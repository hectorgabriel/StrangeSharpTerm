using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Transport;

public enum SshFailureKind
{
    AuthenticationFailed,

    /// <summary>The host is not in known_hosts and the policy would not accept it.</summary>
    HostKeyUnknown,

    /// <summary>
    /// The stored key no longer matches. Always surfaced as a hard block: this is
    /// what a man-in-the-middle looks like, and it must never be a soft warning.
    /// </summary>
    HostKeyChanged,

    /// <summary>The key is marked <c>@revoked</c> in known_hosts.</summary>
    HostKeyRevoked,

    HostUnreachable,
    ConnectionRefused,
    TimedOut,
    NameResolutionFailed,

    /// <summary>A forward could not bind, usually because the local port is taken.</summary>
    ForwardFailed,

    /// <summary>The session is not open, or the server closed it.</summary>
    NotConnected,

    CommandNotFound,

    /// <summary>Anything unrecognised, carrying the text so nothing is swallowed.</summary>
    Other,
}

/// <summary>
/// Why an SSH operation failed.
///
/// The Swift app recovered this from ssh's stderr, because the binary reported
/// everything as exit status 255 plus prose. In-process there are typed
/// exceptions instead, so the classification maps from those — but it stays in
/// one tested place for the same reason as before: so the app can say "the host
/// key changed" rather than "an exception occurred".
/// </summary>
public sealed record SshFailure(SshFailureKind Kind, string? Detail = null)
{
    public bool IsHostKeyProblem =>
        Kind is SshFailureKind.HostKeyChanged or SshFailureKind.HostKeyUnknown or SshFailureKind.HostKeyRevoked;

    /// <summary>A message fit to show a user, phrased around what to do next.</summary>
    public string Summary => Kind switch
    {
        SshFailureKind.AuthenticationFailed => "Authentication failed. Check the username, key, or agent.",
        SshFailureKind.HostKeyUnknown => "The server's host key is not known and the policy will not accept it.",
        SshFailureKind.HostKeyChanged => "The server's host key has changed since it was last trusted.",
        SshFailureKind.HostKeyRevoked => "The server's host key is marked as revoked.",
        SshFailureKind.HostUnreachable => "The host could not be reached.",
        SshFailureKind.ConnectionRefused => "The connection was refused. Check the port and that sshd is running.",
        SshFailureKind.TimedOut => "The connection timed out.",
        SshFailureKind.NameResolutionFailed => "The hostname could not be resolved.",
        SshFailureKind.ForwardFailed => "A port forward could not be bound. The local port may be in use.",
        SshFailureKind.NotConnected => "The connection to the server is not open.",
        SshFailureKind.CommandNotFound => "The remote command was not found.",
        _ => string.IsNullOrWhiteSpace(Detail) ? "The connection failed." : Detail!,
    };

    /// <summary>
    /// Maps an exception onto a failure.
    ///
    /// Specificity is judged across the whole chain, not one exception at a time.
    /// A refused connection arrives as a <see cref="SocketException"/> wrapped in
    /// an <see cref="SshConnectionException"/>, so matching the outer one first
    /// would report "the connection is not open" and leave the user with nothing
    /// to act on.
    /// </summary>
    public static SshFailure Classify(Exception exception)
    {
        var chain = Unwrap(exception).ToArray();

        foreach (var current in chain)
        {
            if (Specific(current) is { } specific)
                return specific;
        }
        foreach (var current in chain)
        {
            if (General(current) is { } general)
                return general;
        }
        return new SshFailure(SshFailureKind.Other, exception.Message.Trim());
    }

    /// <summary>Causes that name what went wrong.</summary>
    private static SshFailure? Specific(Exception exception) => exception switch
    {
        HostKeyRejectedException rejected => new SshFailure(rejected.Verdict switch
        {
            HostKeyVerdict.Changed => SshFailureKind.HostKeyChanged,
            HostKeyVerdict.Revoked => SshFailureKind.HostKeyRevoked,
            _ => SshFailureKind.HostKeyUnknown,
        }, rejected.Message),

        SshAuthenticationException or SshPassPhraseNullOrEmptyException =>
            new SshFailure(SshFailureKind.AuthenticationFailed, exception.Message),

        SshOperationTimeoutException => new SshFailure(SshFailureKind.TimedOut, exception.Message),

        SshConnectionException { DisconnectReason: DisconnectReason.HostKeyNotVerifiable } =>
            new SshFailure(SshFailureKind.HostKeyUnknown, exception.Message),

        SocketException socket => socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => new SshFailure(SshFailureKind.ConnectionRefused, exception.Message),
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                new SshFailure(SshFailureKind.NameResolutionFailed, exception.Message),
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.HostDown =>
                new SshFailure(SshFailureKind.HostUnreachable, exception.Message),
            SocketError.TimedOut => new SshFailure(SshFailureKind.TimedOut, exception.Message),
            SocketError.AddressAlreadyInUse => new SshFailure(SshFailureKind.ForwardFailed, exception.Message),
            _ => null,
        },

        _ => null,
    };

    /// <summary>
    /// Causes that only say the session is gone. Considered after every link in
    /// the chain has been offered to <see cref="Specific"/>, because these are
    /// exactly the exceptions a more informative one arrives inside.
    /// </summary>
    private static SshFailure? General(Exception exception) => exception switch
    {
        SshConnectionException or ObjectDisposedException or InvalidOperationException =>
            new SshFailure(SshFailureKind.NotConnected, exception.Message),
        _ => null,
    };

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 1)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                    yield return inner;
                yield break;
            }
        }
    }
}

/// <summary>
/// The server's host key was not accepted. Carries the verdict, because "unknown"
/// and "changed" call for very different interfaces.
/// </summary>
public sealed class HostKeyRejectedException(HostKeyVerdict verdict, string message) : Exception(message)
{
    public HostKeyVerdict Verdict { get; } = verdict;
}
