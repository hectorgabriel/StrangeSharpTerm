using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// A snippet being written: a command worth keeping, with a name to find it by.
///
/// The placeholders are not a separate field. They are whatever
/// <c>{{markers}}</c> the command contains, read back out of it as it is typed,
/// so the form cannot disagree with the command about what will be asked for.
/// </summary>
public sealed partial class SnippetDraft : ObservableObject
{
    private readonly Snippet _original;

    private SnippetDraft(InventoryTree tree, Snippet original, bool isNew)
    {
        _original = original;
        IsNew = isNew;
        Name = original.Name;
        Command = original.Command;
        Tags = string.Join(", ", original.Tags);
        // "Everywhere", not "No folder": for a snippet an unset folder is a
        // scope — offered on every host — rather than a place it has not been put.
        Scopes = FolderChoice.Of(tree, root: "Everywhere");
        Scope = Scopes.FirstOrDefault(choice => choice.Id == original.FolderId) ?? Scopes[0];
    }

    public static SnippetDraft New(InventoryTree tree, int sortIndex) =>
        new(tree, new Snippet { Name = "", Command = "", SortIndex = sortIndex }, isNew: true);

    public static SnippetDraft For(InventoryTree tree, Snippet snippet) => new(tree, snippet, isNew: false);

    public bool IsNew { get; }

    public string Title => IsNew ? "New snippet" : $"Edit {_original.Name}";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Command { get; set; }

    /// <summary>Comma-separated, and normalised by the model on the way in.</summary>
    [ObservableProperty]
    public partial string Tags { get; set; }

    /// <summary>Which folder this is offered under, or everywhere.</summary>
    [ObservableProperty]
    public partial FolderChoice Scope { get; set; }

    public IReadOnlyList<FolderChoice> Scopes { get; }

    /// <summary>What the command will ask for, in the order it asks.</summary>
    public IReadOnlyList<string> Placeholders => SnippetTemplate.Placeholders(Command);

    /// <summary>
    /// The placeholders as a sentence, so the form says what running this will
    /// ask for before anyone runs it.
    /// </summary>
    public string PlaceholderNote => Placeholders.Count switch
    {
        0 => "No placeholders. Write {{name}} in the command to be asked for a value.",
        1 => $"Asks for {Placeholders[0]} before running.",
        _ => $"Asks for {string.Join(", ", Placeholders)} before running.",
    };

    public IEnumerable<string> Problems()
    {
        if (Name.Trim().Length == 0)
            yield return "A snippet needs a name.";

        if (Command.Trim().Length == 0)
            yield return "A snippet needs a command.";

        // An unclosed marker is a placeholder that will never be asked for and a
        // literal "{{path" that reaches the shell. Saying so now costs nothing.
        if (Command.Contains("{{", StringComparison.Ordinal)
            && Command.IndexOf("}}", Command.IndexOf("{{", StringComparison.Ordinal), StringComparison.Ordinal) < 0)
            yield return "A placeholder is missing its closing }}.";
    }

    public bool IsValid => !Problems().Any();

    public string ProblemSummary => string.Join(" ", Problems());

    /// <summary>
    /// The snippet as it would be saved, folded onto the original so an id and a
    /// sort index survive an edit.
    /// </summary>
    public Snippet Applied() => _original with
    {
        Name = Name.Trim(),
        Command = Command.Trim(),
        FolderId = Scope.Id,
        Tags = [.. Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
    };

    partial void OnNameChanged(string value) => Revalidate();

    partial void OnCommandChanged(string value)
    {
        OnPropertyChanged(nameof(Placeholders));
        OnPropertyChanged(nameof(PlaceholderNote));
        Revalidate();
    }

    private void Revalidate()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ProblemSummary));
    }
}
