using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Terminal.Tests;

public class TerminalRegistryTests
{
    private sealed record Fixture(
        TerminalRegistry Registry,
        TerminalSession A, TerminalSession B, TerminalSession C,
        FakeChannel ChannelA, FakeChannel ChannelB, FakeChannel ChannelC);

    private static Fixture Arrange()
    {
        var registry = new TerminalRegistry();
        FakeChannel a = new(), b = new(), c = new();
        TerminalSession sessionA = new(a), sessionB = new(b), sessionC = new(c);
        registry.Register(sessionA);
        registry.Register(sessionB);
        registry.Register(sessionC);
        return new Fixture(registry, sessionA, sessionB, sessionC, a, b, c);
    }

    [Fact]
    public void TypingInOnePaneReachesTheOthersInTheGroup()
    {
        var f = Arrange();
        f.Registry.SetBroadcastGroup([f.A.Id, f.B.Id]);

        f.A.Send("restart\n");

        f.ChannelB.Written.ShouldBe("restart\n");
        // Written once by the sender itself, and never echoed back to it.
        f.ChannelA.Written.ShouldBe("restart\n");
        f.ChannelC.Written.ShouldBeEmpty();
    }

    [Fact]
    public void AMirroredKeystrokeDoesNotSetOffAnotherRound()
    {
        var f = Arrange();
        f.Registry.SetBroadcastGroup([f.A.Id, f.B.Id, f.C.Id]);

        f.A.Send("x");

        f.ChannelA.Written.ShouldBe("x");
        f.ChannelB.Written.ShouldBe("x");
        f.ChannelC.Written.ShouldBe("x");
    }

    [Fact]
    public void AGroupOfOneIsNotABroadcast()
    {
        // The indicator and the fan-out have to agree about what is happening.
        var f = Arrange();
        f.Registry.SetBroadcastGroup([f.A.Id]);

        f.Registry.IsBroadcasting.ShouldBeFalse();
        f.Registry.BroadcastGroup.ShouldBeEmpty();

        f.A.Send("x");
        f.ChannelB.Written.ShouldBeEmpty();
    }

    [Fact]
    public void TypingOutsideTheGroupGoesNowhereElse()
    {
        var f = Arrange();
        f.Registry.SetBroadcastGroup([f.A.Id, f.B.Id]);

        f.C.Send("private\n");

        f.ChannelA.Written.ShouldBeEmpty();
        f.ChannelB.Written.ShouldBeEmpty();
        f.ChannelC.Written.ShouldBe("private\n");
    }

    [Fact]
    public void ASnippetCanBeSentToSeveralPanesAtOnce()
    {
        var f = Arrange();

        f.Registry.Send("df -h\n", [f.A.Id, f.C.Id]);

        f.ChannelA.Written.ShouldBe("df -h\n");
        f.ChannelC.Written.ShouldBe("df -h\n");
        f.ChannelB.Written.ShouldBeEmpty();
    }

    [Fact]
    public void AForgottenSessionStopsReceivingAndStopsSending()
    {
        var f = Arrange();
        f.Registry.SetBroadcastGroup([f.A.Id, f.B.Id]);
        f.Registry.Forget(f.B.Id);

        f.A.Send("after\n");

        f.ChannelB.Written.ShouldBeEmpty();
        f.Registry.Session(f.B.Id).ShouldBeNull();
    }

    [Fact]
    public void WhatAPaneShowsIsReadableById()
    {
        var f = Arrange();

        f.Registry.VisibleText(f.A.Id).ShouldNotBeNull();
        f.Registry.RecentText(f.A.Id).ShouldBeNull();
        f.Registry.VisibleText(NodeId.New()).ShouldBeNull();
    }
}
