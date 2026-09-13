using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// Asking the user something, as a question with an answer.
///
/// The Swift app kept eight sheet flags and a matching optional on its god
/// object, each with a hand-written binding, and could not present a sheet from
/// a sheet. A dialog here is a call that returns what the user chose, so the
/// nesting is ordinary and the flags are gone.
/// </summary>
public interface IDialogService
{
    /// <summary>Asks a question whose answer costs something. False when dismissed.</summary>
    Task<bool> Confirm(string title, string detail, string confirmLabel);

    /// <summary>
    /// Puts a host in front of the user to edit. True when they saved, and the
    /// draft then holds what they typed; false when they did not, and the draft
    /// is thrown away.
    /// </summary>
    Task<bool> Edit(HostDraft draft);

    /// <inheritdoc cref="Edit(HostDraft)"/>
    Task<bool> Edit(FolderDraft draft);

    /// <inheritdoc cref="Edit(HostDraft)"/>
    Task<bool> Edit(CredentialDraft draft);

    /// <inheritdoc cref="Edit(HostDraft)"/>
    Task<bool> Edit(SnippetDraft draft);

    /// <summary>Puts the credential library in front of the user until they close it.</summary>
    Task Manage(CredentialsViewModel credentials);

    /// <inheritdoc cref="Manage(CredentialsViewModel)"/>
    Task Manage(SnippetsViewModel snippets);

    /// <summary>Puts the settings sheet in front of the user until they close it.</summary>
    Task Manage(SettingsViewModel settings);

    /// <summary>
    /// Asks for a snippet's placeholders before it runs. False when the user
    /// backed out, which must leave nothing typed into the shell.
    /// </summary>
    Task<bool> Fill(SnippetRunViewModel snippet);

    /// <summary>Asks for files from this machine. Empty when the user picked none.</summary>
    Task<IReadOnlyList<string>> PickFiles(string title);
}

/// <summary>Answers without asking. For tests, and for a headless run.</summary>
public sealed class ScriptedDialogService(bool answer = false) : IDialogService
{
    public List<(string Title, string Detail)> Asked { get; } = [];

    /// <summary>Every draft that was put up for editing, in order.</summary>
    public List<object> Edited { get; } = [];

    /// <summary>Stands in for the typing: fills a draft in, and says whether Save was pressed.</summary>
    public Func<HostDraft, bool>? EditHost { get; set; }

    /// <inheritdoc cref="EditHost"/>
    public Func<FolderDraft, bool>? EditFolder { get; set; }

    public Task<bool> Confirm(string title, string detail, string confirmLabel)
    {
        Asked.Add((title, detail));
        return Task.FromResult(answer);
    }

    public Task<bool> Edit(HostDraft draft)
    {
        Edited.Add(draft);
        return Task.FromResult(EditHost?.Invoke(draft) ?? answer);
    }

    public Task<bool> Edit(FolderDraft draft)
    {
        Edited.Add(draft);
        return Task.FromResult(EditFolder?.Invoke(draft) ?? answer);
    }

    /// <inheritdoc cref="EditHost"/>
    public Func<CredentialDraft, bool>? EditCredential { get; set; }

    /// <summary>The credential libraries that were opened, for a test to drive.</summary>
    public List<CredentialsViewModel> Managed { get; } = [];

    public Task<bool> Edit(CredentialDraft draft)
    {
        Edited.Add(draft);
        return Task.FromResult(EditCredential?.Invoke(draft) ?? answer);
    }

    public Task Manage(CredentialsViewModel credentials)
    {
        Managed.Add(credentials);
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="EditHost"/>
    public Func<SnippetDraft, bool>? EditSnippet { get; set; }

    /// <summary>The snippet libraries that were opened, for a test to drive.</summary>
    public List<SnippetsViewModel> ManagedSnippets { get; } = [];

    /// <summary>Stands in for filling the placeholders in, and says whether Run was pressed.</summary>
    public Func<SnippetRunViewModel, bool>? FillSnippet { get; set; }

    /// <summary>Every snippet that was put up to be filled in, in order.</summary>
    public List<SnippetRunViewModel> Filled { get; } = [];

    public Task<bool> Edit(SnippetDraft draft)
    {
        Edited.Add(draft);
        return Task.FromResult(EditSnippet?.Invoke(draft) ?? answer);
    }

    public Task Manage(SnippetsViewModel snippets)
    {
        ManagedSnippets.Add(snippets);
        return Task.CompletedTask;
    }

    /// <summary>The settings sheets that were opened, for a test to drive.</summary>
    public List<SettingsViewModel> Settings { get; } = [];

    public Task Manage(SettingsViewModel settings)
    {
        Settings.Add(settings);
        return Task.CompletedTask;
    }

    public Task<bool> Fill(SnippetRunViewModel snippet)
    {
        Filled.Add(snippet);
        return Task.FromResult(FillSnippet?.Invoke(snippet) ?? answer);
    }

    /// <summary>What the file picker would have returned. Nothing, unless a test says otherwise.</summary>
    public IReadOnlyList<string> Files { get; set; } = [];

    public Task<IReadOnlyList<string>> PickFiles(string title)
    {
        Asked.Add((title, ""));
        return Task.FromResult(Files);
    }
}

public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public Task<bool> Edit(HostDraft draft) => Show(new HostEditor(draft));

    public Task<bool> Edit(FolderDraft draft) => Show(new FolderEditor(draft));

    public Task<bool> Edit(CredentialDraft draft) => Show(new CredentialEditor(draft));

    public Task Manage(CredentialsViewModel credentials) => Show(new CredentialsWindow(credentials));

    public Task<bool> Edit(SnippetDraft draft) => Show(new SnippetEditor(draft));

    public Task Manage(SnippetsViewModel snippets) => Show(new SnippetsWindow(snippets));

    public Task Manage(SettingsViewModel settings) => Show(new SettingsWindow(settings));

    public Task<bool> Fill(SnippetRunViewModel snippet) => Show(new SnippetRunDialog(snippet));

    /// <summary>
    /// The platform's own file picker, through Avalonia's storage provider —
    /// <c>NSOpenPanel</c> on macOS and the Win32 dialog on Windows, without this
    /// knowing which.
    /// </summary>
    public async Task<IReadOnlyList<string>> PickFiles(string title)
    {
        if (owner() is not { StorageProvider: { } storage })
            return [];

        var chosen = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
        });

        // Local files only: a picked item that has no path is something like an
        // iCloud placeholder, and uploading it would need a stream rather than a
        // name.
        return [.. chosen.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }

    /// <summary>
    /// Modal to the window that asked, where there is one. A dialog with no
    /// owner still opens rather than throwing: the headless driver has no window
    /// and must not crash for want of one.
    /// </summary>
    private async Task<bool> Show(Window dialog)
    {
        if (owner() is { } parent)
            return await dialog.ShowDialog<bool>(parent);
        dialog.Show();
        return false;
    }

    public async Task<bool> Confirm(string title, string detail, string confirmLabel)
    {
        var answered = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirm = new Button { Content = confirmLabel, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        confirm.Click += (_, _) => { answered.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { answered.TrySetResult(false); dialog.Close(); };
        // Dismissing the window is a "no": the safe answer when the question was
        // about losing something.
        dialog.Closed += (_, _) => answered.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            MaxWidth = 420,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = detail, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm },
                },
            },
        };

        if (owner() is { } parent)
            await dialog.ShowDialog(parent);
        else
            dialog.Show();
        return await answered.Task;
    }
}
