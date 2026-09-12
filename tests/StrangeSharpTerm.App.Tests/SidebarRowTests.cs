using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.Tests;

public class SidebarRowTests
{
    private static InventoryViewModel Arrange(out Folder outer, out Folder inner, out Connection host, out Connection loose)
    {
        outer = new Folder { Name = "Production" };
        inner = new Folder { ParentId = outer.Id, Name = "Databases" };
        host = new Connection { ParentId = inner.Id, Name = "db-01", Hostname = "db-01.internal" };
        loose = new Connection { Name = "scratch", Hostname = "scratch.example.com" };
        return new InventoryViewModel(null, new InventoryTree([outer, inner], [host, loose]));
    }

    [Fact]
    public void TheTreeFlattensTopToBottomWithFoldersBeforeLooseHosts()
    {
        var model = Arrange(out _, out _, out _, out _);

        model.Rows.Select(row => row.Name).ShouldBe(new[] { "Production", "Databases", "db-01", "scratch" });
    }

    [Fact]
    public void IndentationIsANumberRatherThanNestedMarkup()
    {
        var model = Arrange(out _, out var inner, out var host, out _);

        model.Rows.Single(row => row.Id == inner.Id).Depth.ShouldBe(1);
        model.Rows.Single(row => row.Id == host.Id).Depth.ShouldBe(2);
        model.Rows.Single(row => row.Id == host.Id).Indent.Left.ShouldBe(28);
    }

    [Fact]
    public void ACollapsedFolderHidesWhatIsInsideIt()
    {
        var model = Arrange(out var outer, out _, out _, out _);

        model.Toggle(outer.Id);

        model.Rows.Select(row => row.Name).ShouldBe(new[] { "Production", "scratch" });
    }

    [Fact]
    public void ASearchOpensCollapsedFoldersToShowMatches()
    {
        // Honouring the collapse would hide the very thing the user is looking for.
        var model = Arrange(out var outer, out _, out _, out _);
        model.Toggle(outer.Id);

        model.Search = "db-01";

        model.Rows.Select(row => row.Name).ShouldBe(new[] { "Production", "Databases", "db-01" });
    }

    [Fact]
    public void RowsNameTheirIconRatherThanHoldingOne()
    {
        // Keeps the view models testable without a running application.
        var model = Arrange(out var outer, out _, out var host, out _);

        var folder = model.Rows.Single(row => row.Id == outer.Id);
        folder.IconKey.ShouldBe("IconFolderOpen");
        folder.ChevronKey.ShouldBe("IconChevronDown");

        model.Toggle(outer.Id);
        model.Rows.Single(row => row.Id == outer.Id).IconKey.ShouldBe("IconFolder");
        model.Rows.Single(row => row.Id == outer.Id).ChevronKey.ShouldBe("IconChevronRight");

        model.Toggle(outer.Id);
        var server = model.Rows.Single(row => row.Id == host.Id);
        server.IconKey.ShouldBe("IconServer");
        server.ChevronKey.ShouldBeNull();
    }

    [Fact]
    public void AFolderCarriesTheNumberOfHostsBeneathIt()
    {
        var model = Arrange(out var outer, out _, out _, out _);

        model.Rows.Single(row => row.Id == outer.Id).HostCount.ShouldBe(1);
    }
}
