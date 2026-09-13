using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// One line in the sidebar, already flattened.
///
/// The tree is turned into rows here rather than by nesting templates in the
/// view, for the same reason the Swift version hand-rolled its rows: the
/// arrangement is then something that can be tested, and indentation is a number
/// rather than a nesting depth in markup.
/// </summary>
/// <param name="Depth">How far to indent. Nesting is not capped.</param>
public sealed record SidebarRow(
    NodeId Id,
    string Name,
    int Depth,
    bool IsFolder,
    bool IsExpanded = false,
    string? Hostname = null,
    int HostCount = 0,
    bool IsSelected = false)
{
    /// <summary>Which icon the row draws, by resource key. See docs/adr/0003.</summary>
    public string IconKey => IsFolder ? (IsExpanded ? "IconFolderOpen" : "IconFolder") : "IconServer";

    /// <summary>The disclosure arrow, or null for a host, which has nothing to disclose.</summary>
    public string? ChevronKey => !IsFolder ? null : IsExpanded ? "IconChevronDown" : "IconChevronRight";

    /// <summary>Left margin for the row's contents: indentation is a number, not nested markup.</summary>
    public Avalonia.Thickness Indent => new(Depth * 14, 0, 0, 0);
}

public sealed partial class InventoryViewModel
{
    /// <summary>
    /// The sidebar, top to bottom: folders with their contents, then the hosts
    /// that sit outside any folder.
    ///
    /// A search narrows the list, and a collapsed folder is opened while it
    /// lasts: honouring the collapse would hide the matches the user is actively
    /// looking for.
    /// </summary>
    public IReadOnlyList<SidebarRow> Rows
    {
        get
        {
            var rows = new List<SidebarRow>();
            foreach (var folder in RootFolders)
                AddFolder(folder, 0, rows);
            rows.AddRange(VisibleLooseConnections.Select(HostRow(0)));
            return rows;
        }
    }

    private void AddFolder(Model.Folder folder, int depth, List<SidebarRow> rows)
    {
        if (!FolderIsVisible(folder.Id))
            return;

        rows.Add(new SidebarRow(
            folder.Id, folder.Name, depth, IsFolder: true,
            IsExpanded: IsExpanded(folder.Id) || Search.Length > 0,
            HostCount: Tree.DescendantConnections(folder.Id).Count));

        if (!IsExpanded(folder.Id) && Search.Length == 0)
            return;

        foreach (var child in SubfoldersOf(folder.Id))
            AddFolder(child, depth + 1, rows);
        rows.AddRange(ConnectionsIn(folder.Id).Select(HostRow(depth + 1)));
    }

    private Func<Model.Connection, SidebarRow> HostRow(int depth) =>
        connection => new SidebarRow(
            connection.Id, connection.Name, depth, IsFolder: false, Hostname: connection.Hostname,
            IsSelected: connection.Id == Selection);
}
