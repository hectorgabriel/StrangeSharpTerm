using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>One snippet in the library, as the list draws it.</summary>
public sealed record SnippetRow(Snippet Snippet, string Scope)
{
    public string Name => Snippet.Name;

    public string Command => Snippet.Command;

    /// <summary>Where it is offered, and what it will ask for. The second line of the row.</summary>
    public string Detail => Snippet.Placeholders.Count == 0
        ? Scope
        : $"{Scope} · asks for {string.Join(", ", Snippet.Placeholders)}";
}

/// <summary>
/// The snippet library: commands worth keeping, and where each is offered.
///
/// A snippet scoped to a folder is offered on the hosts beneath it and nowhere
/// else, which is how "restart the app server" stays away from the database
/// boxes.
/// </summary>
public sealed partial class SnippetsViewModel(InventoryViewModel inventory, IDialogService dialogs) : ObservableObject
{
    public ObservableCollection<SnippetRow> Rows { get; } = [];

    [ObservableProperty]
    public partial SnippetRow? Selected { get; set; }

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Whether there is a row for Edit and Delete to be about.</summary>
    public bool HasSelection => Selected is not null;

    public void Refresh()
    {
        var chosen = Selected?.Snippet.Id;
        Rows.Clear();

        // Every snippet, not the visible ones: this is the library, and a
        // snippet scoped to a folder still has to be findable from anywhere.
        foreach (var snippet in inventory.Tree.Snippets.Values
                     .OrderBy(snippet => snippet.SortIndex)
                     .ThenBy(snippet => snippet.Name, StringComparer.Ordinal))
            Rows.Add(new SnippetRow(snippet, ScopeOf(snippet)));

        Selected = Rows.FirstOrDefault(row => row.Snippet.Id == chosen);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    public async Task Add()
    {
        var next = inventory.Tree.Snippets.Count;
        await Save(SnippetDraft.New(inventory.Tree, next));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    public async Task Edit()
    {
        if (Selected is { } row)
            await Save(SnippetDraft.For(inventory.Tree, row.Snippet));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    public async Task Delete()
    {
        if (Selected is not { } row)
            return;

        inventory.ConfirmDelete(new PendingDeletion.Snippet(row.Snippet.Id));
        if (inventory.PendingDeletionMessage is not { } message)
            return;

        if (await dialogs.Confirm(message.Title, message.Detail, "Delete"))
        {
            inventory.PerformPendingDeletion();
            Refresh();
        }
        else
        {
            inventory.Pending = null;
        }
    }

    partial void OnSelectedChanged(SnippetRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private async Task Save(SnippetDraft draft)
    {
        if (!await dialogs.Edit(draft))
            return;

        inventory.Upsert(draft.Applied());
        Refresh();
    }

    private string ScopeOf(Snippet snippet) =>
        snippet.FolderId is { } folder && inventory.Tree.Folders.GetValueOrDefault(folder) is { } named
            ? $"In {named.Name}"
            : "Everywhere";
}
