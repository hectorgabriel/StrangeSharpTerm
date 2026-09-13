using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Store;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>What a delete is waiting to be confirmed for.</summary>
public abstract record PendingDeletion(NodeId Id)
{
    public sealed record Connection(NodeId Id) : PendingDeletion(Id);

    public sealed record Folder(NodeId Id) : PendingDeletion(Id);

    public sealed record Credential(NodeId Id) : PendingDeletion(Id);
}

/// <summary>
/// The inventory: what is in it, what is selected, and every change to it.
///
/// The Swift app kept this in a single 1,908-line object along with tabs, panes,
/// splits, sheet flags and a headless driver. Its own section comments named the
/// seams, and this is the first of them. Nothing here knows about windows, so it
/// can be tested without one.
/// </summary>
public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly IInventoryPersistence? _store;
    private readonly HashSet<NodeId> _expanded;

    /// <param name="store">
    /// Null when the inventory could not be opened; edits then stay in memory
    /// rather than failing silently on every keystroke.
    /// </param>
    public InventoryViewModel(IInventoryPersistence? store = null, InventoryTree? tree = null)
    {
        _store = store;
        Tree = tree ?? new InventoryTree();
        // Start fully expanded: a collapsed sidebar on first launch hides the
        // very thing the user came to see.
        _expanded = [.. Tree.Folders.Keys];
    }

    public InventoryTree Tree { get; private set; }

    [ObservableProperty]
    public partial NodeId? Selection { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => OnPropertyChanged(nameof(Rows));

    // The rows carry which one is selected, so moving the selection redraws them.
    partial void OnSelectionChanged(NodeId? value) => OnPropertyChanged(nameof(Rows));

    /// <summary>Why the last save failed, for the UI to show without blocking the edit.</summary>
    [ObservableProperty]
    public partial string? StoreError { get; private set; }

    /// <summary>
    /// Raised before a connection is deleted, so whatever has it open can close
    /// it first. The workspace listens; the inventory does not need to know that.
    /// </summary>
    public event EventHandler<NodeId>? ConnectionRemoving;

    public Model.Connection? SelectedConnection =>
        Selection is { } id ? Tree.Connections.GetValueOrDefault(id) : null;

    // MARK: reading the tree

    /// <summary>Connections sitting outside any folder.</summary>
    public IReadOnlyList<Model.Connection> LooseConnections => Tree.Children(null).Connections;

    /// <summary>
    /// The same, narrowed by the search.
    ///
    /// The Swift sidebar filtered a folder's contents but not the hosts outside
    /// one, so searching left them all on screen. Corrected here: a search is a
    /// search.
    /// </summary>
    public IReadOnlyList<Model.Connection> VisibleLooseConnections =>
        Search.Length == 0 ? LooseConnections : [.. LooseConnections.Where(Matches)];

    public IReadOnlyList<Model.Folder> RootFolders => Tree.Children(null).Folders;

    public bool IsExpanded(NodeId folder) => _expanded.Contains(folder);

    public void Toggle(NodeId folder)
    {
        if (!_expanded.Remove(folder))
            _expanded.Add(folder);
        OnPropertyChanged(nameof(Rows));
    }

    /// <summary>
    /// Connections beneath a folder, filtered by the search field.
    ///
    /// Search matches the display name, the hostname and any tag, so typing
    /// "prod" finds both a host named prod-web and one tagged production.
    /// </summary>
    public IReadOnlyList<Model.Connection> ConnectionsIn(NodeId folder)
    {
        var children = Tree.Children(folder).Connections;
        return Search.Length == 0 ? children : [.. children.Where(Matches)];
    }

    public IReadOnlyList<Model.Folder> SubfoldersOf(NodeId folder) => Tree.Children(folder).Folders;

    /// <summary>Whether a folder should appear at all, given the current search.</summary>
    public bool FolderIsVisible(NodeId folder)
    {
        if (Search.Length == 0)
            return true;
        return ConnectionsIn(folder).Count > 0 || SubfoldersOf(folder).Any(child => FolderIsVisible(child.Id));
    }

    public int ConnectionCount => Tree.Connections.Count;

    public IReadOnlyList<Model.Credential> SortedCredentials =>
        [.. Tree.Credentials.Values.OrderBy(c => c.SortIndex).ThenBy(c => c.Name, StringComparer.Ordinal)];

    /// <summary>Snippets offered for the focused connection, or all of them when nothing is selected.</summary>
    public IReadOnlyList<Snippet> VisibleSnippets =>
        Selection is { } id
            ? Tree.SnippetsFor(id)
            : [.. Tree.Snippets.Values.OrderBy(s => s.SortIndex).ThenBy(s => s.Name, StringComparer.Ordinal)];

    /// <summary>Folders flattened for a picker, depth-first, with the depth to indent by.</summary>
    public IReadOnlyList<(Model.Folder Folder, int Depth)> FolderChoices => [.. Walk(null, 0)];

    /// <summary>Places a new item at the end of its folder.</summary>
    public int NextSortIndex(NodeId? parent)
    {
        var children = Tree.Children(parent);
        var indices = children.Folders.Select(f => f.SortIndex).Concat(children.Connections.Select(c => c.SortIndex));
        return indices.DefaultIfEmpty(-1).Max() + 1;
    }

    // MARK: changing it

    public void Upsert(Model.Connection connection)
    {
        Tree.Upsert(connection);
        Selection = connection.Id;
        Persist();
    }

    public void Upsert(Model.Folder folder)
    {
        Tree.Upsert(folder);
        _expanded.Add(folder.Id);
        Persist();
    }

    public void Upsert(Model.Credential credential)
    {
        Tree.Upsert(credential);
        Persist();
    }

    public void Upsert(Snippet snippet)
    {
        Tree.Upsert(snippet);
        Persist();
    }

    /// <summary>Removes a host, after telling whatever has it open to close.</summary>
    public void Delete(Model.Connection connection) => DeleteConnection(connection.Id);

    public void DeleteConnection(NodeId id)
    {
        ConnectionRemoving?.Invoke(this, id);
        Tree.RemoveConnection(id);
        if (Selection == id)
            Selection = null;
        Persist();
    }

    /// <summary>Removes a folder and everything inside it, deepest first.</summary>
    public void DeleteFolder(NodeId id)
    {
        foreach (var connection in Tree.DescendantConnections(id))
            DeleteConnection(connection);
        foreach (var folder in Tree.DescendantFolders(id))
            Tree.RemoveFolder(folder);

        Tree.RemoveFolder(id);
        _expanded.Remove(id);
        if (Selection == id)
            Selection = null;
        Persist();
    }

    public void DeleteCredential(NodeId id)
    {
        Tree.RemoveCredential(id);
        Persist();
    }

    public void DeleteSnippet(NodeId id)
    {
        Tree.RemoveSnippet(id);
        Persist();
    }

    // MARK: confirming a delete

    [ObservableProperty]
    public partial PendingDeletion? Pending { get; set; }

    public void ConfirmDelete(PendingDeletion deletion) => Pending = deletion;

    public void PerformPendingDeletion()
    {
        switch (Pending)
        {
            case PendingDeletion.Connection connection: DeleteConnection(connection.Id); break;
            case PendingDeletion.Folder folder: DeleteFolder(folder.Id); break;
            case PendingDeletion.Credential credential: DeleteCredential(credential.Id); break;
        }
        Pending = null;
    }

    /// <summary>What the confirmation should say, so it can name the consequences.</summary>
    public (string Title, string Detail)? PendingDeletionMessage
    {
        get
        {
            switch (Pending)
            {
                case PendingDeletion.Connection pending
                    when Tree.Connections.GetValueOrDefault(pending.Id) is { } connection:
                    return ($"Delete {connection.Name}?", "Its settings and port forwards will be removed.");

                case PendingDeletion.Folder pending when Tree.Folders.GetValueOrDefault(pending.Id) is { } folder:
                    var hosts = Tree.DescendantConnections(pending.Id).Count;
                    return ($"Delete {folder.Name}?", hosts == 0
                        ? "The folder is empty."
                        : $"{hosts} host{(hosts == 1 ? "" : "s")} inside will be deleted too.");

                case PendingDeletion.Credential pending
                    when Tree.Credentials.GetValueOrDefault(pending.Id) is { } credential:
                    var users = Tree.UsersOfCredential(pending.Id);
                    var count = users.Folders.Count + users.Connections.Count;
                    return ($"Delete {credential.Name}?", count == 0
                        ? "Nothing is using it."
                        : $"{count} host{(count == 1 ? "" : "s")} or folder{(count == 1 ? "" : "s")} "
                          + "using it will be left without a credential.");

                default:
                    return null;
            }
        }
    }

    private bool Matches(Model.Connection connection)
    {
        var needle = Search;
        return connection.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
            || connection.Hostname.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
            || connection.Tags.Any(tag => tag.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
    }

    private IEnumerable<(Model.Folder Folder, int Depth)> Walk(NodeId? parent, int depth) =>
        Tree.Children(parent).Folders.SelectMany(folder =>
            new[] { (folder, depth) }.Concat(Walk(folder.Id, depth + 1)));

    /// <summary>Saves after every change. A failure is reported, never thrown at a keystroke.</summary>
    private void Persist()
    {
        OnPropertyChanged(nameof(Tree));
        OnPropertyChanged(nameof(Rows));
        if (_store is null)
            return;
        try
        {
            _store.Save(Tree);
            StoreError = null;
        }
        catch (Exception e)
        {
            StoreError = e.Message;
        }
    }
}
