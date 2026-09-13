using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>One credential in the library, with what depends on it.</summary>
public sealed record CredentialRow(Credential Credential, int Users, bool HasSecret)
{
    public string Name => Credential.Name;

    public string Method => Credential.Method.Label();

    /// <summary>What it supplies, for the row's second line.</summary>
    public string Detail => string.Join(
        " · ",
        new[]
        {
            Credential.Username is { } user ? $"as {user}" : null,
            Credential.IdentityFile,
            HasSecret ? "secret stored" : null,
        }.Where(part => part is not null));

    /// <summary>Whether <see cref="Detail"/> has anything in it: an agent credential does not.</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>
    /// How many hosts and folders point at it. Said in the list, because the
    /// answer to "can I delete this" is a number.
    /// </summary>
    public string Usage => Users switch
    {
        0 => "used by nothing",
        1 => "used by 1 host or folder",
        _ => $"used by {Users} hosts and folders",
    };
}

/// <summary>
/// The credential library: a key or a password described once and pointed at
/// from any number of hosts.
///
/// Nothing here holds a secret. The list says whether the store has one, which
/// is the only thing about it worth showing.
/// </summary>
public sealed partial class CredentialsViewModel(
    InventoryViewModel inventory,
    ISecretStore secrets,
    IDialogService dialogs) : ObservableObject
{
    public ObservableCollection<CredentialRow> Rows { get; } = [];

    [ObservableProperty]
    public partial CredentialRow? Selected { get; set; }

    /// <summary>Why the secret store could not be read, when it could not.</summary>
    [ObservableProperty]
    public partial string? Failure { get; private set; }

    public bool IsEmpty => Rows.Count == 0;

    partial void OnSelectedChanged(CredentialRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    public void Refresh()
    {
        var chosen = Selected?.Credential.Id;
        Rows.Clear();

        foreach (var credential in inventory.SortedCredentials)
        {
            var users = inventory.Tree.UsersOfCredential(credential.Id);
            Rows.Add(new CredentialRow(credential, users.Folders.Count + users.Connections.Count, HasSecret(credential)));
        }

        Selected = Rows.FirstOrDefault(row => row.Credential.Id == chosen);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    public async Task Add()
    {
        var draft = CredentialDraft.New(secrets, inventory.SortedCredentials.Count);
        await Save(draft);
    }

    /// <summary>Whether there is a row for Edit and Delete to be about.</summary>
    public bool HasSelection => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    public async Task Edit()
    {
        if (Selected is { } row)
            await Save(CredentialDraft.For(row.Credential, secrets));
    }

    /// <summary>
    /// Deletes one, after asking. The question says what is left pointing at
    /// nothing, because that is what the answer turns on.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public async Task Delete()
    {
        if (Selected is not { } row)
            return;

        inventory.ConfirmDelete(new PendingDeletion.Credential(row.Credential.Id));
        if (inventory.PendingDeletionMessage is not { } message)
            return;

        if (await dialogs.Confirm(message.Title, message.Detail, "Delete"))
        {
            // The secret goes with it: an account nothing points at is one
            // nobody can clear later.
            Forget(row.Credential);
            inventory.PerformPendingDeletion();
            Refresh();
        }
        else
        {
            inventory.Pending = null;
        }
    }

    private async Task Save(CredentialDraft draft)
    {
        if (!await dialogs.Edit(draft))
            return;

        var saved = draft.Applied();
        inventory.Upsert(saved);

        try
        {
            draft.SaveSecret(saved);
            Failure = null;
        }
        catch (SecretStoreException e)
        {
            // The credential is saved either way: the inventory is ours and the
            // store is the operating system's, and losing the second should not
            // lose the first.
            Failure = e.Message;
        }

        Refresh();
    }

    private bool HasSecret(Credential credential)
    {
        try
        {
            return credential.CanHaveSecret && secrets.HasSecret(credential.SecretAccount);
        }
        catch (SecretStoreException e)
        {
            Failure = e.Message;
            return false;
        }
    }

    private void Forget(Credential credential)
    {
        try
        {
            secrets.RemoveSecret(credential.SecretAccount);
        }
        catch (SecretStoreException e)
        {
            Failure = e.Message;
        }
    }
}
