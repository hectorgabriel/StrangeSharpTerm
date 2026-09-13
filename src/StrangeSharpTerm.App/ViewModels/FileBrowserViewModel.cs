using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>One row in the browser: an entry, and how to say it.</summary>
public sealed record FileRow(RemoteEntry Entry)
{
    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>Which icon: a folder, a link, or a file. See docs/adr/0003.</summary>
    public string IconKey => Entry.IsDirectory ? "IconFolder" : Entry.IsSymbolicLink ? "IconArrowRightToLine" : "IconFile";

    /// <summary>
    /// A size a person reads, or nothing at all for a directory — the number of
    /// bytes a directory entry occupies is true and useless.
    /// </summary>
    public string Size => Entry.IsDirectory ? "" : Bytes(Entry.Length);

    public string Modified => Entry.Modified.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// Powers of 1024 with one decimal, which is what a file manager shows. Not
    /// a locale-aware format: this is a size, and 1.5 KB reads the same
    /// everywhere.
    /// </summary>
    public static string Bytes(long length)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = length;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{length} B" : $"{size:0.#} {units[unit]}";
    }
}

/// <summary>
/// A directory on a server, and the few things anyone does to one.
///
/// Everything slow happens off the UI thread and every failure lands in
/// <see cref="Failure"/> rather than in an exception: a file browser is one long
/// argument with a network, and a pane that throws when a directory cannot be
/// read is a pane that disappears.
/// </summary>
public sealed partial class FileBrowserViewModel : ObservableObject
{
    private readonly IRemoteFiles _files;
    private readonly IDialogService _dialogs;
    private readonly Func<Action, Task> _run;

    /// <param name="offThread">
    /// How work leaves the UI thread. Replaced in tests by something that runs
    /// it there and now, so a test does not have to wait for a thread pool.
    /// </param>
    public FileBrowserViewModel(
        IRemoteFiles files,
        string host,
        IDialogService? dialogs = null,
        Func<Action, Task>? offThread = null)
    {
        _files = files;
        Host = host;
        _dialogs = dialogs ?? new ScriptedDialogService();
        _run = offThread ?? (work => Task.Run(work));
        Path = files.Home;
    }

    /// <summary>
    /// Does something slow, somewhere else, and keeps what happened.
    ///
    /// One place to be busy and one place for a failure to land: a file browser
    /// is one long argument with a network, and every one of these calls can
    /// lose it.
    /// </summary>
    private async Task Do(string description, Action work)
    {
        IsBusy = true;
        Failure = null;
        try
        {
            await _run(work);
        }
        catch (Exception e)
        {
            Failure = $"{description}: {Explain(e)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string Host { get; }

    [ObservableProperty]
    public partial string Path { get; private set; } = "/";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>What went wrong, in the pane, where the user was looking.</summary>
    [ObservableProperty]
    public partial string? Failure { get; private set; }

    /// <summary>What the last transfer did, so a download is not silent.</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    [ObservableProperty]
    public partial FileRow? Selected { get; set; }

    public ObservableCollection<FileRow> Rows { get; } = [];

    /// <summary>Whether there is anywhere above this. The root has no parent.</summary>
    public bool CanGoUp => Parent(Path) is not null;

    /// <summary>Reads the current directory again.</summary>
    [RelayCommand]
    public async Task Refresh() => await Load(Path);

    /// <summary>Opens a directory, or does nothing for a file.</summary>
    [RelayCommand]
    public async Task Open(FileRow? row)
    {
        if (row is { IsDirectory: true })
            await Load(row.Entry.Path);
    }

    [RelayCommand]
    public async Task GoUp()
    {
        if (Parent(Path) is { } parent)
            await Load(parent);
    }

    [RelayCommand]
    public async Task GoHome() => await Load(_files.Home);

    /// <summary>
    /// Copies the selected file into the Downloads folder.
    ///
    /// Somewhere rather than nowhere: asking where to put it needs a save
    /// dialog, and until there is one, the folder every browser uses is a better
    /// answer than refusing. The note says where it went.
    /// </summary>
    [RelayCommand]
    public async Task Download()
    {
        if (Selected is not { IsDirectory: false } row)
            return;

        var destination = System.IO.Path.Combine(DownloadsFolder(), row.Name);
        Note = null;
        await Do($"Downloading {row.Name}", () => _files.Download(row.Entry.Path, destination));
        if (Failure is null)
            Note = $"Saved to {destination}";
    }

    /// <summary>Uploads whatever the user picks, into the directory on screen.</summary>
    [RelayCommand]
    public async Task Upload()
    {
        var chosen = await _dialogs.PickFiles("Upload to " + Path);
        if (chosen.Count == 0)
            return;

        var into = Path;
        await Do(
            "Uploading",
            () =>
            {
                foreach (var local in chosen)
                    _files.Upload(local, Join(into, System.IO.Path.GetFileName(local)));
            });
        Note = Failure is null ? $"Uploaded {chosen.Count} file{(chosen.Count == 1 ? "" : "s")}" : null;
        await Refresh();
    }

    /// <summary>Deletes the selection, after asking. A directory says what goes with it.</summary>
    [RelayCommand]
    public async Task Delete()
    {
        if (Selected is not { } row)
            return;

        var detail = row.IsDirectory
            ? "Everything inside it goes too, and none of it can be undone."
            : "It cannot be undone.";
        if (!await _dialogs.Confirm($"Delete {row.Name}?", detail, "Delete"))
            return;

        await Do($"Deleting {row.Name}", () => _files.Delete(row.Entry));
        await Refresh();
    }

    /// <summary>Reads a directory and shows it, or says why it could not.</summary>
    public async Task Load(string path)
    {
        IReadOnlyList<RemoteEntry> entries = [];
        await Do($"Reading {path}", () => entries = _files.List(path));
        if (Failure is not null)
            return;

        // The note survives a refresh of the same directory, because that is
        // what an upload does right after saying what it did. Going somewhere
        // else is a new context and clears it.
        if (path != Path)
            Note = null;
        Path = path;
        Selected = null;
        Rows.Clear();

        // Directories first, then by name: the order every file manager uses,
        // and the order that makes a deep tree navigable by eye.
        foreach (var entry in entries
                     .OrderByDescending(entry => entry.IsDirectory)
                     .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new FileRow(entry));

        OnPropertyChanged(nameof(CanGoUp));
    }

    /// <summary>
    /// The directory above, or null at the root.
    ///
    /// Always with forward slashes: this is a remote POSIX path, and
    /// <see cref="System.IO.Path"/> would helpfully turn it into a Windows one on
    /// Windows.
    /// </summary>
    public static string? Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return null;
        var cut = trimmed.LastIndexOf('/');
        return cut switch
        {
            < 0 => null,
            0 => "/",
            _ => trimmed[..cut],
        };
    }

    /// <summary>A remote path joined the remote way, whatever this machine is.</summary>
    public static string Join(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : $"{directory}/{name}";

    private static string DownloadsFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = System.IO.Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }

    /// <summary>
    /// What to tell the user. SSH.NET's messages are usable as they are, and the
    /// permission one is the case worth naming.
    /// </summary>
    private static string Explain(Exception error) => error switch
    {
        Renci.SshNet.Common.SftpPermissionDeniedException => "permission denied",
        Renci.SshNet.Common.SftpPathNotFoundException => "no such file or directory",
        _ => error.Message,
    };
}
