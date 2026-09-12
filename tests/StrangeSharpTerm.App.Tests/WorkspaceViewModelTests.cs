using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.Tests;

public class WorkspaceViewModelTests
{
    private static Pane Terminal(string title = "web-01", NodeId? host = null) =>
        new() { Title = title, Kind = new PaneKind.Terminal(), ConnectionId = host ?? NodeId.New() };

    [Fact]
    public void TheFirstPaneOpensATab()
    {
        var workspace = new WorkspaceViewModel();
        var pane = Terminal();

        workspace.Open(pane);

        workspace.Tabs.Count.ShouldBe(1);
        workspace.ActivePane.ShouldBe(pane);
        workspace.TitleOf(workspace.Tabs[0]).ShouldBe("web-01");
    }

    [Fact]
    public void ASplitIsASecondPaneInTheSameTab()
    {
        // Tabs own panes rather than being sessions, which is what makes a split
        // expressible at all.
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));
        var second = Terminal("db-01");

        workspace.Open(second, SplitAxis.Vertical);

        workspace.Tabs.Count.ShouldBe(1);
        workspace.Tabs[0].PaneIds.Count.ShouldBe(2);
        workspace.Tabs[0].Axis.ShouldBe(SplitAxis.Vertical);
        workspace.ActivePane.ShouldBe(second);
    }

    [Fact]
    public void ATabWithASplitCountsTheRestInItsTitle()
    {
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));
        workspace.Open(Terminal("db-01"), SplitAxis.Horizontal);

        workspace.TitleOf(workspace.Tabs[0]).ShouldBe("db-01 +1");
    }

    [Fact]
    public void OpeningWithoutSplittingStartsANewTab()
    {
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));

        workspace.Open(Terminal("db-01"));

        workspace.Tabs.Count.ShouldBe(2);
    }

    [Fact]
    public void ClosingTheLastPaneClosesItsTab()
    {
        var workspace = new WorkspaceViewModel();
        var pane = Terminal();
        workspace.Open(pane);

        workspace.ClosePane(pane.Id);

        workspace.Tabs.ShouldBeEmpty();
        workspace.Panes.ShouldBeEmpty();
        workspace.ActiveTabId.ShouldBeNull();
    }

    [Fact]
    public void ClosingOnePaneOfASplitLeavesTheTabAndMovesFocus()
    {
        var workspace = new WorkspaceViewModel();
        var first = Terminal("web-01");
        var second = Terminal("db-01");
        workspace.Open(first);
        workspace.Open(second, SplitAxis.Horizontal);

        workspace.ClosePane(second.Id);

        workspace.Tabs.Count.ShouldBe(1);
        workspace.ActivePane.ShouldBe(first);
    }

    [Fact]
    public void ClosingATabTakesEveryPaneInIt()
    {
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));
        workspace.Open(Terminal("db-01"), SplitAxis.Horizontal);

        workspace.CloseTab(workspace.Tabs[0].Id);

        workspace.Tabs.ShouldBeEmpty();
        workspace.Panes.ShouldBeEmpty();
    }

    [Fact]
    public void FocusingAPaneTellsTheSidebarWhichHostItIsAbout()
    {
        var workspace = new WorkspaceViewModel();
        var host = NodeId.New();
        var pane = Terminal("web-01", host);
        workspace.Open(Terminal("other"));
        workspace.Open(pane);
        var focused = new List<NodeId>();
        workspace.HostFocused += (_, id) => focused.Add(id);

        workspace.FocusPane(pane.Id);

        focused.ShouldBe(new[] { host });
        workspace.ActivePane.ShouldBe(pane);
    }

    [Fact]
    public void APaneAboutNoOneHostLeavesTheSelectionAlone()
    {
        // An orchestrator spans several hosts; clearing or moving the selection
        // for it would be a lie about what is on screen.
        var workspace = new WorkspaceViewModel();
        var orchestrator = new Pane { Title = "Fan-out", Kind = new PaneKind.Orchestrator() };
        workspace.Open(orchestrator);
        var focused = 0;
        workspace.HostFocused += (_, _) => focused++;

        workspace.FocusPane(orchestrator.Id);

        focused.ShouldBe(0);
    }

    [Fact]
    public void AShortcutForATabThatIsNotOpenDoesNothing()
    {
        // Rather than jumping somewhere odd.
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));
        var first = workspace.ActiveTabId;

        workspace.FocusTabAt(7);

        workspace.ActiveTabId.ShouldBe(first);
    }

    [Fact]
    public void AnExitIsRecordedOnThePaneRatherThanClosingIt()
    {
        // A shell that exited leaves its output on screen until the pane is closed.
        var workspace = new WorkspaceViewModel();
        var pane = Terminal();
        workspace.Open(pane);

        workspace.PaneExited(pane.Id, 130);

        workspace.Pane(pane.Id).ShouldNotBeNull().HasExited.ShouldBeTrue();
        workspace.Pane(pane.Id)!.ExitCode.ShouldBe(130);
        workspace.Tabs.Count.ShouldBe(1);
    }

    [Fact]
    public void ATitleChangeKeepsThePaneIdentity()
    {
        // Rebuilding the pane would mint a new id and break tab selection and
        // closing, which key off it.
        var workspace = new WorkspaceViewModel();
        var pane = Terminal("web-01");
        workspace.Open(pane);

        workspace.UpdateTitle(pane.Id, "db-01 — postgres");
        workspace.UpdateTitle(pane.Id, "");

        workspace.Pane(pane.Id).ShouldNotBeNull().Title.ShouldBe("db-01 — postgres");
        workspace.ActivePaneId.ShouldBe(pane.Id);
    }
}

public class BroadcastTests
{
    private static Pane Terminal(string title) =>
        new() { Title = title, Kind = new PaneKind.Terminal(), ConnectionId = NodeId.New() };

    [Fact]
    public void OneTerminalIsNotEnoughToBroadcast()
    {
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));

        workspace.CanBroadcast.ShouldBeFalse();
    }

    [Fact]
    public void ASplitOfTerminalsCanBroadcast()
    {
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));
        workspace.Open(Terminal("db-01"), SplitAxis.Horizontal);

        workspace.CanBroadcast.ShouldBeTrue();
    }

    [Fact]
    public void OnlyTerminalsJoinTheGroup()
    {
        // A file browser in the split is not something keystrokes should reach.
        var workspace = new WorkspaceViewModel();
        workspace.Open(Terminal("web-01"));
        workspace.Open(new Pane { Title = "Files", Kind = new PaneKind.Files(), ConnectionId = NodeId.New() },
            SplitAxis.Horizontal);

        workspace.CanBroadcast.ShouldBeFalse();
    }
}
