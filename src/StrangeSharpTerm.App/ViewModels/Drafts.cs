using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>A folder as a picker offers it, indented by depth. A null id is the top level.</summary>
public sealed record FolderChoice(string Label, NodeId? Id)
{
    public static FolderChoice Root { get; } = new("No folder", null);

    /// <summary>Every folder, depth-first, with the top level first.</summary>
    public static IReadOnlyList<FolderChoice> Of(InventoryTree tree, NodeId? excluding = null)
    {
        var choices = new List<FolderChoice> { Root };
        Walk(null, 0);
        return choices;

        void Walk(NodeId? parent, int depth)
        {
            foreach (var folder in tree.Children(parent).Folders)
            {
                // A folder cannot be moved inside itself, so it is not offered:
                // refusing the move afterwards explains less than never offering it.
                if (folder.Id == excluding)
                    continue;
                choices.Add(new FolderChoice(new string(' ', depth * 4) + folder.Name, folder.Id));
                Walk(folder.Id, depth + 1);
            }
        }
    }
}

/// <summary>
/// A host being written.
///
/// The Swift app had a draft type for the same reason: an editor must be able to
/// hold a half-finished host — a port that is still "22x", a name not yet typed —
/// and the model cannot. Nothing here reaches the inventory until
/// <see cref="Applied"/> is asked for, and the dialog only asks when
/// <see cref="Problems"/> is empty.
/// </summary>
public sealed partial class HostDraft : ObservableObject
{
    private readonly InventoryTree _tree;
    private readonly Connection _original;

    private HostDraft(InventoryTree tree, Connection original, bool isNew)
    {
        _tree = tree;
        _original = original;
        IsNew = isNew;
        Name = original.Name;
        Hostname = original.Hostname;
        Tags = string.Join(", ", original.Tags);
        Settings = new SettingsDraft(original.Settings, [.. tree.Credentials.Values]);
        Folders = FolderChoice.Of(tree);
        Parent = Folders.FirstOrDefault(choice => choice.Id == original.ParentId) ?? FolderChoice.Root;
        Settings.PropertyChanged += (_, _) => Revalidate();
        ShowInheritance();
    }

    /// <summary>A host that does not exist yet, in the folder the user was looking at.</summary>
    public static HostDraft New(InventoryTree tree, NodeId? parent, int sortIndex) =>
        new(tree, new Connection { Name = "", Hostname = "", ParentId = parent, SortIndex = sortIndex }, isNew: true);

    /// <summary>A host that does, with its own values in the form.</summary>
    public static HostDraft For(InventoryTree tree, Connection connection) => new(tree, connection, isNew: false);

    public bool IsNew { get; }

    public string Title => IsNew ? "New host" : $"Edit {_original.Name}";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Hostname { get; set; }

    /// <summary>Comma-separated, and normalised by the model on the way in.</summary>
    [ObservableProperty]
    public partial string Tags { get; set; }

    [ObservableProperty]
    public partial FolderChoice Parent { get; set; }

    public SettingsDraft Settings { get; }

    public IReadOnlyList<FolderChoice> Folders { get; }

    /// <summary>What a blank username would mean here, so the field can say so.</summary>
    public string UsernameHint => Inheritance.Describe(_tree, Parent.Id, settings => settings.Username)
        ?? $"{Environment.UserName} — the account you are running as";

    /// <summary>Likewise for a blank port.</summary>
    public string PortHint => Inheritance.Describe(_tree, Parent.Id, settings => settings.Port)
        ?? "22 — the default";

    public string InheritanceNote => Inheritance.Note(_tree, Parent.Id);

    public IEnumerable<string> Problems()
    {
        if (Name.Trim().Length == 0)
            yield return "A host needs a name.";
        if (Hostname.Trim().Length == 0)
            yield return "A host needs an address to connect to.";
        foreach (var problem in Settings.Problems())
            yield return problem;
    }

    public bool IsValid => !Problems().Any();

    /// <summary>Every problem in one line, for the dialog to show once saving is tried.</summary>
    public string ProblemSummary => string.Join(" ", Problems());

    /// <summary>
    /// The host as it would be saved. Folded onto the original record, so a
    /// setting this form does not show — an environment variable, a port forward,
    /// a credential — survives being edited around.
    /// </summary>
    public Connection Applied() => _original with
    {
        Name = Name.Trim(),
        Hostname = Hostname.Trim(),
        ParentId = Parent.Id,
        Tags = [.. Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
        Settings = Settings.Applied(),
    };

    partial void OnParentChanged(FolderChoice value)
    {
        // Moving a host to another folder changes what its blank fields mean.
        OnPropertyChanged(nameof(UsernameHint));
        OnPropertyChanged(nameof(PortHint));
        OnPropertyChanged(nameof(InheritanceNote));
        ShowInheritance();
    }

    private void ShowInheritance()
    {
        Settings.UsernameWatermark = UsernameHint;
        Settings.PortWatermark = PortHint;
    }

    partial void OnNameChanged(string value) => Revalidate();

    partial void OnHostnameChanged(string value) => Revalidate();

    private void Revalidate()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ProblemSummary));
    }
}

/// <summary>
/// A folder being written. Folders carry the same partial settings as hosts, so
/// the same <see cref="SettingsDraft"/> edits them; what differs is that a folder
/// can be moved, and moving one into itself would cost the tree.
/// </summary>
public sealed partial class FolderDraft : ObservableObject
{
    private readonly InventoryTree _tree;
    private readonly Folder _original;

    private FolderDraft(InventoryTree tree, Folder original, bool isNew)
    {
        _tree = tree;
        _original = original;
        IsNew = isNew;
        Name = original.Name;
        Settings = new SettingsDraft(original.Settings, [.. tree.Credentials.Values]);
        // Its own subtree is not offered as a destination, and neither is it.
        Folders = FolderChoice.Of(tree, excluding: isNew ? null : original.Id);
        Parent = Folders.FirstOrDefault(choice => choice.Id == original.ParentId) ?? FolderChoice.Root;
        Settings.PropertyChanged += (_, _) => Revalidate();
        ShowInheritance();
    }

    public static FolderDraft New(InventoryTree tree, NodeId? parent, int sortIndex) =>
        new(tree, new Folder { Name = "", ParentId = parent, SortIndex = sortIndex }, isNew: true);

    public static FolderDraft For(InventoryTree tree, Folder folder) => new(tree, folder, isNew: false);

    public bool IsNew { get; }

    public string Title => IsNew ? "New folder" : $"Edit {_original.Name}";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial FolderChoice Parent { get; set; }

    public SettingsDraft Settings { get; }

    public IReadOnlyList<FolderChoice> Folders { get; }

    public string InheritanceNote => Inheritance.Note(_tree, Parent.Id);

    public IEnumerable<string> Problems()
    {
        if (Name.Trim().Length == 0)
            yield return "A folder needs a name.";

        // FolderChoice already leaves this folder's subtree out of the picker.
        // Checked again here because a draft can be built and applied without a
        // dialog, and a cycle is unrecoverable from the file.
        if (!IsNew && !_tree.CanReparent(_original.Id, Parent.Id))
            yield return "A folder cannot be moved inside itself.";

        foreach (var problem in Settings.Problems())
            yield return problem;
    }

    public bool IsValid => !Problems().Any();

    /// <inheritdoc cref="HostDraft.ProblemSummary"/>
    public string ProblemSummary => string.Join(" ", Problems());

    public Folder Applied() => _original with
    {
        Name = Name.Trim(),
        ParentId = Parent.Id,
        Settings = Settings.Applied(),
    };

    partial void OnParentChanged(FolderChoice value)
    {
        OnPropertyChanged(nameof(InheritanceNote));
        ShowInheritance();
        Revalidate();
    }

    private void ShowInheritance()
    {
        Settings.UsernameWatermark = Inheritance.Describe(_tree, Parent.Id, s => s.Username) ?? "Not set";
        Settings.PortWatermark = Inheritance.Describe(_tree, Parent.Id, s => s.Port) ?? "22 — the default";
    }

    partial void OnNameChanged(string value) => Revalidate();

    private void Revalidate()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ProblemSummary));
    }
}

/// <summary>
/// What a blank field would inherit, and from where.
///
/// The Swift detail pane explained inheritance after the fact — "Username
/// inherited from Production". An editor has to say it beforehand, or leaving a
/// field blank is a guess.
/// </summary>
public static class Inheritance
{
    /// <summary>The nearest ancestor that sets this field, as "value — from Folder", or null when none does.</summary>
    public static string? Describe(InventoryTree tree, NodeId? parent, Func<ConnectionSettings, object?> pick)
    {
        foreach (var folder in Ancestors(tree, parent))
            if (pick(folder.Settings) is { } value)
                return $"{value} — from {folder.Name}";
        return null;
    }

    /// <summary>A line for the form: what blank means, given where this node sits.</summary>
    public static string Note(InventoryTree tree, NodeId? parent) =>
        Ancestors(tree, parent).FirstOrDefault() is { } folder
            ? $"Blank, or Inherited, takes the value from {folder.Name}."
            : "Blank, or Inherited, takes the default. Nothing above this sets one.";

    /// <summary>Nearest first, which is the order inheritance resolves in.</summary>
    private static IReadOnlyList<Folder> Ancestors(InventoryTree tree, NodeId? parent)
    {
        try
        {
            // Ancestors() gives outermost first, because that is the order they
            // fold in; the nearest one is the one that wins.
            return [.. tree.Ancestors(parent).Reverse()];
        }
        catch (InventoryException)
        {
            // A corrupted parent chain: the editor still opens, and says nothing
            // about inheritance rather than refusing to draw.
            return [];
        }
    }
}
