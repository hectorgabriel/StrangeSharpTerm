using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// One row of the file tree: an entry, its children once anybody asks, and
/// whether it is open.
///
/// Children are read when the row is expanded and not before. A tree that read
/// itself whole would be one <c>readdir</c> per directory on a server, over a
/// network, to draw rows nobody has looked at — and on a home directory with a
/// <c>node_modules</c> in it, that is the whole afternoon.
/// </summary>
public sealed partial class WorkspaceNode : ObservableObject
{
    private readonly Func<WorkspaceNode, Task> _load;

    public WorkspaceNode(RemoteEntry entry, string relative, Func<WorkspaceNode, Task> load)
    {
        Entry = entry;
        Relative = relative;
        _load = load;

        // A directory nobody has opened still has to look openable, so it gets
        // one placeholder child: a TreeView with no children draws no arrow, and
        // a folder with no arrow reads as an empty folder.
        if (entry.IsDirectory)
            Children.Add(Placeholder);
    }

    public RemoteEntry Entry { get; }

    /// <summary>Where it is in the workspace: <c>conf/nginx.conf</c>.</summary>
    public string Relative { get; }

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>Which icon: a folder, a link, or a file. See docs/adr/0003.</summary>
    public string IconKey => Entry.IsDirectory
        ? IsExpanded ? "IconFolderOpen" : "IconFolder"
        : Entry.IsSymbolicLink ? "IconArrowRightToLine" : "IconFile";

    /// <summary>A size a person reads, and nothing at all for a directory.</summary>
    public string Size => Entry.IsDirectory ? "" : FileRow.Bytes(Entry.Length);

    public ObservableCollection<WorkspaceNode> Children { get; } = [];

    /// <summary>The one child a closed directory has, so that it draws an arrow.</summary>
    private static WorkspaceNode Placeholder { get; } = new(
        new RemoteEntry("…", "", false, false, 0, default),
        "",
        _ => Task.CompletedTask);

    public bool IsPlaceholder => Relative.Length == 0 && Name == "…";

    /// <summary>Whether this row's children have been read from the server.</summary>
    public bool IsLoaded { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IconKey));
        if (value && !IsLoaded && !IsPlaceholder)
            _ = Fill();
    }

    /// <summary>Reads this directory and puts its rows in, replacing whatever was there.</summary>
    public async Task Fill()
    {
        await _load(this);
        IsLoaded = true;
    }

    /// <summary>Reads it again, but only if anybody has looked: a closed folder is not worth a round trip.</summary>
    public async Task Refresh()
    {
        if (IsLoaded)
            await Fill();
    }

    /// <summary>Puts a directory's rows in place of whatever it had, placeholder included.</summary>
    public void Replace(IEnumerable<WorkspaceNode> children)
    {
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
    }

    /// <summary>This row and everything under it that has been read, for finding a path again.</summary>
    public IEnumerable<WorkspaceNode> Reachable()
    {
        yield return this;
        foreach (var child in Children.Where(child => !child.IsPlaceholder))
        {
            foreach (var found in child.Reachable())
                yield return found;
        }
    }
}
