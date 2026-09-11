namespace StrangeSharpTerm.Model;

public abstract class InventoryException(string message) : Exception(message);

public sealed class UnknownConnectionException(NodeId id)
    : InventoryException($"No connection has id {id}.")
{
    public NodeId Id { get; } = id;
}

public sealed class UnknownFolderException(NodeId id)
    : InventoryException($"No folder has id {id}.")
{
    public NodeId Id { get; } = id;
}

/// <summary>
/// The parent chain loops back on itself. Reachable through a corrupted file or a
/// bad reparent, and must never be allowed to spin forever.
/// </summary>
public sealed class InventoryCycleException(NodeId at)
    : InventoryException($"The folder chain loops back on itself at {at}.")
{
    public NodeId At { get; } = at;
}

/// <summary>
/// The whole connection inventory, held in memory.
///
/// Kept as flat maps rather than a nested tree, so reparenting is a single field
/// write and lookups stay O(1); the hierarchy is derived on demand. The maps keep
/// insertion order, so an inventory loaded from a file saves back in the same order.
/// </summary>
public sealed class InventoryTree
{
    private readonly OrderedDictionary<NodeId, Folder> _folders = new();
    private readonly OrderedDictionary<NodeId, Connection> _connections = new();
    private readonly OrderedDictionary<NodeId, Snippet> _snippets = new();
    private readonly OrderedDictionary<NodeId, Credential> _credentials = new();

    /// <exception cref="ArgumentException">Two items of one kind share an id.</exception>
    public InventoryTree(
        IEnumerable<Folder>? folders = null,
        IEnumerable<Connection>? connections = null,
        IEnumerable<Snippet>? snippets = null,
        IEnumerable<Credential>? credentials = null)
    {
        foreach (var folder in folders ?? []) _folders.Add(folder.Id, folder);
        foreach (var connection in connections ?? []) _connections.Add(connection.Id, connection);
        foreach (var snippet in snippets ?? []) _snippets.Add(snippet.Id, snippet);
        foreach (var credential in credentials ?? []) _credentials.Add(credential.Id, credential);
    }

    public IReadOnlyDictionary<NodeId, Folder> Folders => _folders;
    public IReadOnlyDictionary<NodeId, Connection> Connections => _connections;
    public IReadOnlyDictionary<NodeId, Snippet> Snippets => _snippets;
    public IReadOnlyDictionary<NodeId, Credential> Credentials => _credentials;

    public void Upsert(Folder folder) => _folders[folder.Id] = folder;
    public void Upsert(Connection connection) => _connections[connection.Id] = connection;
    public void Upsert(Snippet snippet) => _snippets[snippet.Id] = snippet;
    public void Upsert(Credential credential) => _credentials[credential.Id] = credential;

    public void RemoveFolder(NodeId id) => _folders.Remove(id);
    public void RemoveConnection(NodeId id) => _connections.Remove(id);
    public void RemoveSnippet(NodeId id) => _snippets.Remove(id);
    public void RemoveCredential(NodeId id) => _credentials.Remove(id);

    /// <summary>
    /// Hosts and folders currently pointing at a credential, so deleting one can
    /// say what it would affect.
    /// </summary>
    public (IReadOnlyList<Folder> Folders, IReadOnlyList<Connection> Connections) UsersOfCredential(NodeId id) =>
    (
        [.. _folders.Values.Where(f => f.Settings.CredentialId == id).OrderBy(f => f.Name, StringComparer.Ordinal)],
        [.. _connections.Values.Where(c => c.Settings.CredentialId == id).OrderBy(c => c.Name, StringComparer.Ordinal)]
    );

    /// <summary>
    /// Snippets offered for a connection: the unscoped ones, plus any scoped to a
    /// folder the connection sits in.
    /// </summary>
    public IReadOnlyList<Snippet> SnippetsFor(NodeId connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var target))
            return [];

        HashSet<NodeId> ancestors;
        try
        {
            ancestors = [.. Ancestors(target.ParentId).Select(f => f.Id)];
        }
        catch (InventoryException)
        {
            ancestors = [];
        }

        return
        [
            .. _snippets.Values
                .Where(s => s.FolderId is not { } scope || ancestors.Contains(scope))
                .OrderBy(s => s.SortIndex)
                .ThenBy(s => s.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>Every folder beneath <paramref name="id"/>, deepest first, so callers can delete safely.</summary>
    public IReadOnlyList<NodeId> DescendantFolders(NodeId id)
    {
        var result = new List<NodeId>();
        var visited = new HashSet<NodeId> { id };
        Collect(id);
        return result;

        void Collect(NodeId parent)
        {
            foreach (var child in _folders.Values.Where(f => f.ParentId == parent).Select(f => f.Id).ToArray())
            {
                // A cycle. Ancestors() reports those; here it only must not recurse forever.
                if (!visited.Add(child))
                    continue;
                Collect(child);
                result.Add(child);
            }
        }
    }

    /// <summary>Every connection anywhere beneath <paramref name="id"/>.</summary>
    public IReadOnlyList<NodeId> DescendantConnections(NodeId id)
    {
        var parents = new HashSet<NodeId>(DescendantFolders(id));
        return
        [
            .. _connections.Values.Where(c => c.ParentId == id).Select(c => c.Id),
            .. _connections.Values.Where(c => c.ParentId is { } p && p != id && parents.Contains(p)).Select(c => c.Id),
        ];
    }

    /// <summary>
    /// Folders from the given node up to the root, <b>outermost first</b>.
    ///
    /// Ordering matters: <see cref="ConnectionSettings.InheritingFrom"/> folds a
    /// child onto a parent, so the caller must walk root-downwards for the nearest
    /// ancestor to win.
    /// </summary>
    /// <exception cref="InventoryCycleException">The chain loops.</exception>
    /// <exception cref="UnknownFolderException">The chain names a folder that does not exist.</exception>
    public IReadOnlyList<Folder> Ancestors(NodeId? parentId)
    {
        var chain = new List<Folder>();
        var seen = new HashSet<NodeId>();

        for (var cursor = parentId; cursor is { } id;)
        {
            if (!seen.Add(id))
                throw new InventoryCycleException(id);
            if (!_folders.TryGetValue(id, out var folder))
                throw new UnknownFolderException(id);
            chain.Add(folder);
            cursor = folder.ParentId;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Applies folder inheritance and default fallbacks to produce a connection
    /// the transport layer can act on without further decisions.
    /// </summary>
    /// <exception cref="UnknownConnectionException">No connection has this id.</exception>
    public ResolvedConnection Resolve(NodeId connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
            throw new UnknownConnectionException(connectionId);

        var chain = Ancestors(connection.ParentId);
        var inherited = chain.Aggregate(ConnectionSettings.Empty, (accumulated, folder) => folder.Settings.InheritingFrom(accumulated));
        var effective = connection.Settings.InheritingFrom(inherited);

        // The credential fills in what nothing else specified. Applied after
        // inheritance so a host's own username still wins over the shared one.
        if (effective.CredentialId is { } credentialId && _credentials.TryGetValue(credentialId, out var credential))
            effective = credential.AppliedTo(effective);

        return new ResolvedConnection(connection, new ResolvedSettings(effective), [.. chain.Select(f => f.Id)]);
    }

    public (IReadOnlyList<Folder> Folders, IReadOnlyList<Connection> Connections) Children(NodeId? parentId) =>
    (
        [.. _folders.Values.Where(f => f.ParentId == parentId).OrderBy(f => f.SortIndex).ThenBy(f => f.Name, StringComparer.Ordinal)],
        [.. _connections.Values.Where(c => c.ParentId == parentId).OrderBy(c => c.SortIndex).ThenBy(c => c.Name, StringComparer.Ordinal)]
    );

    /// <summary>
    /// Whether <paramref name="folder"/> may be moved under <paramref name="newParent"/>
    /// without forming a loop. The UI calls this to reject an illegal drag before
    /// it corrupts the tree.
    /// </summary>
    public bool CanReparent(NodeId folder, NodeId? newParent)
    {
        if (newParent == folder)
            return false;

        var seen = new HashSet<NodeId>();
        for (var cursor = newParent; cursor is { } id;)
        {
            if (id == folder || !seen.Add(id))
                return false;
            cursor = _folders.TryGetValue(id, out var parent) ? parent.ParentId : null;
        }
        return true;
    }
}
