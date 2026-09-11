using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Transport.Tests;

/// <summary>
/// The Swift suite classified ssh's stderr prose, which no longer exists: in
/// process there are typed exceptions instead. The behaviour under test is the
/// same one — that the app can say what went wrong — re-based onto those.
/// </summary>
public class SshFailureTests
{
    [Fact]
    public void AuthenticationFailureIsRecognisedAndIsNotAHostKeyProblem()
    {
        var failure = SshFailure.Classify(new SshAuthenticationException("Permission denied (publickey)."));

        failure.Kind.ShouldBe(SshFailureKind.AuthenticationFailed);
        failure.IsHostKeyProblem.ShouldBeFalse();
        failure.Summary.ShouldContain("Authentication failed");
    }

    [Fact]
    public void AKeyThatNeedsAPassphraseIsAnAuthenticationFailureNotAMystery()
    {
        SshFailure.Classify(new SshPassPhraseNullOrEmptyException("passphrase required"))
            .Kind.ShouldBe(SshFailureKind.AuthenticationFailed);
    }

    [Fact]
    public void ATimeoutIsRecognised()
    {
        SshFailure.Classify(new SshOperationTimeoutException("timed out")).Kind.ShouldBe(SshFailureKind.TimedOut);
    }

    [Fact]
    public void ARefusedConnectionIsFoundInsideTheWrappingException()
    {
        // SSH.NET reports a refused port as a socket error inside a connection
        // exception. Losing that distinction would leave the user with "the
        // connection failed" and nothing to act on.
        var wrapped = new SshConnectionException("unable to connect", new SocketException((int)SocketError.ConnectionRefused));

        SshFailure.Classify(wrapped).Kind.ShouldBe(SshFailureKind.ConnectionRefused);
    }

    [Fact]
    public void AnUnresolvableHostIsNameResolutionNotAnUnreachableNetwork()
    {
        SshFailure.Classify(new SocketException((int)SocketError.HostNotFound))
            .Kind.ShouldBe(SshFailureKind.NameResolutionFailed);
    }

    [Fact]
    public void AnUnreachableNetworkIsRecognised()
    {
        SshFailure.Classify(new SocketException((int)SocketError.NetworkUnreachable))
            .Kind.ShouldBe(SshFailureKind.HostUnreachable);
    }

    [Fact]
    public void APortAlreadyInUseIsAForwardFailure()
    {
        SshFailure.Classify(new SocketException((int)SocketError.AddressAlreadyInUse))
            .Kind.ShouldBe(SshFailureKind.ForwardFailed);
    }

    [Fact]
    public void AServerRefusingItsOwnHostKeyIsAHostKeyProblem()
    {
        var failure = SshFailure.Classify(
            new SshConnectionException("host key not verifiable", DisconnectReason.HostKeyNotVerifiable));

        failure.Kind.ShouldBe(SshFailureKind.HostKeyUnknown);
        failure.IsHostKeyProblem.ShouldBeTrue();
    }

    [Fact]
    public void AChangedKeyKeepsItsOwnKindSoTheUiCanRefuseRatherThanAsk()
    {
        var changed = SshFailure.Classify(
            new HostKeyRejectedException(new HostKeyVerdict.Changed("SHA256:stored"), "the key changed"));
        var revoked = SshFailure.Classify(
            new HostKeyRejectedException(new HostKeyVerdict.Revoked(), "the key is revoked"));

        changed.Kind.ShouldBe(SshFailureKind.HostKeyChanged);
        revoked.Kind.ShouldBe(SshFailureKind.HostKeyRevoked);
        changed.IsHostKeyProblem.ShouldBeTrue();
        revoked.IsHostKeyProblem.ShouldBeTrue();
    }

    [Fact]
    public void ALostSessionReadsAsNotConnected()
    {
        SshFailure.Classify(new SshConnectionException("client not connected")).Kind.ShouldBe(SshFailureKind.NotConnected);
        SshFailure.Classify(new ObjectDisposedException("SshClient")).Kind.ShouldBe(SshFailureKind.NotConnected);
    }

    [Fact]
    public void AnUnrecognisedFailureKeepsItsTextRatherThanSwallowingIt()
    {
        var failure = SshFailure.Classify(new InvalidDataException("the server said something odd"));

        failure.Kind.ShouldBe(SshFailureKind.Other);
        failure.Summary.ShouldBe("the server said something odd");
    }

    [Fact]
    public void EveryKindHasSomethingFitToShowAPerson()
    {
        foreach (var kind in Enum.GetValues<SshFailureKind>())
            new SshFailure(kind).Summary.ShouldNotBeNullOrWhiteSpace();
    }
}
