using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// <see cref="IWorkspaceAccess"/> over a real folder on a real host.
///
/// Built around a function rather than a workspace, because the two things that
/// hold one do not agree about when it exists. A conversation is built when a
/// host is first looked at; a workspace exists only once somebody has opened a
/// folder, which may be never and is often later. Asking each time means the
/// tools appear in the same conversation the moment the folder is opened, and
/// disappear again when it is closed — with no second agent and no reconnecting.
///
/// A root of <c>""</c> is how "no folder is open" is said, and
/// <see cref="HostAgent"/> reads it that way: no root, no file tools offered.
/// </summary>
public sealed class RemoteWorkspaceAccess(Func<RemoteWorkspace?> open) : IWorkspaceAccess
{
    public RemoteWorkspaceAccess(RemoteWorkspace workspace)
        : this(() => workspace)
    {
    }

    public string Root => open()?.Root ?? "";

    public string RootLabel => open()?.RootLabel ?? "";

    /// <summary>
    /// The workspace, or a failure a model can act on.
    ///
    /// A folder closed part way through a conversation is not a crash: the model
    /// is told plainly, as it is told about a refusal, and can say what it was
    /// going to do instead.
    /// </summary>
    private RemoteWorkspace Opened =>
        open() ?? throw new InvalidOperationException("No folder is open on this host.");

    public Task<string> List(string path, CancellationToken cancellationToken = default) =>
        // Off the UI thread for the same reason every other call to a host is:
        // SFTP is a network protocol, and the thread this is called on is
        // usually drawing a window.
        Task.Run(
            () =>
            {
                var workspace = Opened;
                var where = workspace.Relative(workspace.Resolve(path));
                return WorkspaceTools.Render(where, workspace.List(path));
            },
            cancellationToken);

    public Task<FileText> Read(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Opened.Read(path), cancellationToken);

    public Task<FileChange> Plan(string path, string text, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var workspace = Opened;
                var absolute = workspace.Resolve(path);
                var existing = workspace.Stat(absolute);

                if (existing is null)
                {
                    // A new file is a Unix file: it gets \n, whatever the
                    // machine that typed it uses.
                    return new FileChange(
                        absolute,
                        workspace.Relative(absolute),
                        text,
                        Creates: true,
                        Diff.Between("", text));
                }

                if (existing.IsDirectory)
                    throw new IOException($"{workspace.Relative(absolute)} is a directory.");

                var before = workspace.Read(absolute);
                if (before.IsBinary)
                    throw new IOException(
                        $"{before.Relative} is not a text file, and this app will not overwrite one.");
                if (before.Truncated)
                    throw new IOException(
                        $"{before.Relative} is larger than {RemoteWorkspace.MaxFileBytes / 1000} kB, "
                            + "so it cannot be rewritten whole.");

                return new FileChange(
                    absolute,
                    before.Relative,
                    text,
                    Creates: false,
                    Diff.Between(before.Text, text),
                    before.Newline,
                    before.HasByteOrderMark);
            },
            cancellationToken);

    public Task Write(FileChange change, CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Opened.Write(change.Path, change.Text, change.Newline, change.ByteOrderMark),
            cancellationToken);
}
