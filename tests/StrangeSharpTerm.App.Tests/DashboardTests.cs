using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// What a server says about itself, and what the card does with it.
///
/// The parsing is the transport's and has its own tests from M2; these are
/// about the decisions on top: when to ask, what to show before anything has
/// been asked, how full is too full, and what a refusal looks like.
/// </summary>
public class DashboardTests
{
    private sealed class FakeHealth : IServerHealth
    {
        public int Collected { get; private set; }

        public Exception? Refuses { get; set; }

        public ServerMetrics Answer { get; set; } = new();

        public ServerMetrics Collect()
        {
            Collected++;
            if (Refuses is { } refusal)
            {
                Refuses = null;
                throw refusal;
            }
            return Answer;
        }
    }

    private static Task Here(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    private static DashboardViewModel Card(FakeHealth health) => new(health, Here);

    private static ServerMetrics Busy => new()
    {
        Uptime = "up 12 days, 4:31",
        LoadAverages = [0.84, 1.12, 0.97],
        MemoryUsedBytes = 6_453_000_000,
        MemoryTotalBytes = 18_030_000_000,
        DiskUsedBytes = 44_270_000_000,
        DiskTotalBytes = 56_600_000_000,
        Containers = [new ServerMetrics.Container("postgres", "Up 12 days"), new ServerMetrics.Container("backup", "Exited (0) 6 hours ago")],
    };

    [Fact]
    public void ItAsksNothingUntilItIsAskedTo()
    {
        // Selecting a host deliberately does not connect to it. A card that
        // probed as soon as it was built would undo that decision quietly.
        var health = new FakeHealth();

        var card = Card(health);

        health.Collected.ShouldBe(0);
        card.HasAnswer.ShouldBeFalse();
        card.Uptime.ShouldBeNull();
    }

    [Fact]
    public async Task AskingOnceFillsItIn()
    {
        var health = new FakeHealth { Answer = Busy };
        var card = Card(health);

        await card.RefreshCommand.ExecuteAsync(null);

        health.Collected.ShouldBe(1);
        card.HasAnswer.ShouldBeTrue();
        card.Uptime.ShouldBe("up 12 days, 4:31");
        card.Load.ShouldBe("0.84  1.12  0.97");
        card.Containers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ASizeIsSaidTheWayADashboardSaysIt()
    {
        var card = Card(new FakeHealth { Answer = Busy });
        await card.RefreshCommand.ExecuteAsync(null);

        // 6.01 GB, not 6453000000 bytes.
        card.Memory.ShouldNotBeNull().Detail.ShouldBe("6.01 GB / 16.79 GB");
        card.Memory.PercentText.ShouldBe("36%");
    }

    [Theory]
    [InlineData(0.10, Pressure.Fine)]
    [InlineData(0.74, Pressure.Fine)]
    [InlineData(0.75, Pressure.Warning)]
    [InlineData(0.89, Pressure.Warning)]
    [InlineData(0.90, Pressure.Critical)]
    [InlineData(0.99, Pressure.Critical)]
    public void HowFullIsTooFull(double fraction, Pressure expected) =>
        DashboardViewModel.PressureOf(fraction).ShouldBe(expected);

    [Fact]
    public async Task ADiskAtFourFifthsIsWorthNoticing()
    {
        var card = Card(new FakeHealth { Answer = Busy });
        await card.RefreshCommand.ExecuteAsync(null);

        card.Disk.ShouldNotBeNull().Pressure.ShouldBe(Pressure.Warning);
        card.Memory.ShouldNotBeNull().Pressure.ShouldBe(Pressure.Fine);
    }

    [Fact]
    public async Task TheSparklineIsWhatThisCardHasSeen()
    {
        // The server keeps no history for us: the line is the one-minute load
        // from each refresh, oldest first.
        var health = new FakeHealth { Answer = Busy };
        var card = Card(health);

        await card.RefreshCommand.ExecuteAsync(null);
        card.LoadHistory.ShouldBe([0.84]);

        health.Answer = Busy with { LoadAverages = [1.5, 1.2, 1.0] };
        await card.RefreshCommand.ExecuteAsync(null);

        card.LoadHistory.ShouldBe([0.84, 1.5]);
    }

    [Fact]
    public async Task ItRemembersATrendRatherThanAHistory()
    {
        var health = new FakeHealth { Answer = Busy };
        var card = Card(health);

        for (var sample = 0; sample < DashboardViewModel.Samples + 10; sample++)
        {
            health.Answer = Busy with { LoadAverages = [sample, 1, 1] };
            await card.RefreshCommand.ExecuteAsync(null);
        }

        card.LoadHistory.Count.ShouldBe(DashboardViewModel.Samples);
        // The oldest ones fell off the front, not the back.
        card.LoadHistory[^1].ShouldBe(DashboardViewModel.Samples + 9);
    }

    [Fact]
    public async Task AHostThatCannotBeReachedSaysSoInTheCard()
    {
        var health = new FakeHealth { Refuses = new System.Net.Sockets.SocketException(61) };
        var card = Card(health);

        await card.RefreshCommand.ExecuteAsync(null);

        card.Failure.ShouldNotBeNull();
        card.HasAnswer.ShouldBeFalse();
        card.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task AskingAgainAfterAFailureClearsIt()
    {
        var health = new FakeHealth { Refuses = new System.Net.Sockets.SocketException(61), Answer = Busy };
        var card = Card(health);
        await card.RefreshCommand.ExecuteAsync(null);
        card.Failure.ShouldNotBeNull();

        await card.RefreshCommand.ExecuteAsync(null);

        card.Failure.ShouldBeNull();
        card.HasAnswer.ShouldBeTrue();
    }

    [Fact]
    public async Task AServerWithNoneOfTheToolsIsNotAFailure()
    {
        // A container without uptime, free or df answers with nothing, which is
        // a different thing from not answering.
        var card = Card(new FakeHealth { Answer = new ServerMetrics() });

        await card.RefreshCommand.ExecuteAsync(null);

        card.Failure.ShouldBeNull();
        card.HasAnswer.ShouldBeTrue();
        card.AnsweredWithNothing.ShouldBeTrue();
        card.Memory.ShouldBeNull();
    }

    [Fact]
    public async Task WhatWasMeasuredShowsEvenWhenTheRestWasNot()
    {
        // Every field is optional on purpose: reporting a confident zero for
        // something that could not be measured would be worse than nothing.
        var card = Card(new FakeHealth { Answer = new ServerMetrics { Uptime = "up 3 days", LoadAverages = [0.1, 0.2, 0.3] } });

        await card.RefreshCommand.ExecuteAsync(null);

        card.Uptime.ShouldBe("up 3 days");
        card.Memory.ShouldBeNull();
        card.Disk.ShouldBeNull();
        card.AnsweredWithNothing.ShouldBeFalse();
    }

    [Fact]
    public void TheCardOutlivesAnEditToTheHost()
    {
        // It holds what the server last said and the little history the
        // sparkline draws; renaming a host in the middle of watching it should
        // not throw that away.
        var host = new Connection { Name = "db-01", Hostname = "db-01.example.com" };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            new FakeSessions { OnHealth = _ => new FakeHealth { Answer = Busy } });
        shell.Inventory.Selection = host.Id;
        var dashboard = shell.Detail.ShouldNotBeNull().Dashboard;

        shell.Inventory.Upsert(host with { Name = "db-primary" });

        shell.Detail.ShouldNotBeNull().Name.ShouldBe("db-primary");
        shell.Detail.Dashboard.ShouldBeSameAs(dashboard);
    }

    [Fact]
    public void EachHostHasItsOwn()
    {
        var first = new Connection { Name = "db-01", Hostname = "db-01.example.com" };
        var second = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [first, second])),
            new FakeSessions { OnHealth = _ => new FakeHealth() });

        shell.Inventory.Selection = first.Id;
        var one = shell.Detail!.Dashboard;
        shell.Inventory.Selection = second.Id;
        var other = shell.Detail!.Dashboard;

        other.ShouldNotBeSameAs(one);
    }

    [Fact]
    public void SelectingAHostStillDoesNotConnectToIt()
    {
        // The rule the detail pane was built on in M4, restated here because the
        // dashboard is the thing most likely to break it.
        var host = new Connection { Name = "db-01", Hostname = "db-01.example.com" };
        var health = new FakeHealth();
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            new FakeSessions { OnHealth = _ => health });

        shell.Inventory.Selection = host.Id;

        health.Collected.ShouldBe(0);
    }
}
