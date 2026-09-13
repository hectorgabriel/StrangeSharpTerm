using Renci.SshNet;

namespace StrangeSharpTerm.Transport;

/// <summary>What a listing says about one entry.</summary>
/// <param name="Path">Absolute, as the server gives it. The only thing anything else should navigate by.</param>
/// <param name="Modified">In the server's own reckoning; nothing here converts it.</param>
public sealed record RemoteEntry(
    string Name,
    string Path,
    bool IsDirectory,
    bool IsSymbolicLink,
    long Length,
    DateTime Modified)
{
    /// <summary>
    /// A link is followed rather than described. The server resolves it on the
    /// way in; what this says is only what the icon should be.
    /// </summary>
    public bool LooksLikeAFolder => IsDirectory;
}

/// <summary>
/// Files on the far end.
///
/// The seam the file browser is written against, so the browser can be tested
/// without a server: SFTP is a network protocol, and a view model that only
/// works when one is reachable is a view model nobody can test.
/// </summary>
public interface IRemoteFiles
{
    /// <summary>Where a browser starts: the account's own directory.</summary>
    string Home { get; }

    /// <summary>One directory, unsorted. The caller decides the order.</summary>
    IReadOnlyList<RemoteEntry> List(string path);

    /// <summary>Copies a remote file into a local one, which is created or replaced.</summary>
    void Download(string remotePath, string localPath);

    /// <summary>The other way. The remote file is created or replaced.</summary>
    void Upload(string localPath, string remotePath);

    /// <summary>Removes a file, or a directory and everything in it.</summary>
    void Delete(RemoteEntry entry);

    void Rename(string path, string newPath);

    void CreateDirectory(string path);
}

/// <summary>
/// <see cref="IRemoteFiles"/> over SSH.NET's SFTP client.
///
/// Thin on purpose: everything that is a decision — what order to show, what to
/// call a failure, where a download lands — belongs to the view model, and
/// everything here is the protocol.
/// </summary>
public sealed class SftpFiles(SftpClient client) : IRemoteFiles
{
    public string Home => client.WorkingDirectory;

    public IReadOnlyList<RemoteEntry> List(string path) =>
    [
        .. client.ListDirectory(path)
            // "." and ".." are the protocol's, not the user's: going up is a
            // button, not an entry that looks like a folder called two dots.
            .Where(file => file.Name is not ("." or ".."))
            .Select(file => new RemoteEntry(
                file.Name,
                file.FullName,
                file.IsDirectory,
                file.IsSymbolicLink,
                file.IsRegularFile ? file.Length : 0,
                file.LastWriteTime)),
    ];

    public void Download(string remotePath, string localPath)
    {
        using var local = File.Create(localPath);
        client.DownloadFile(remotePath, local);
    }

    public void Upload(string localPath, string remotePath)
    {
        using var local = File.OpenRead(localPath);
        client.UploadFile(local, remotePath);
    }

    public void Delete(RemoteEntry entry)
    {
        if (entry.IsDirectory)
            DeleteTree(entry.Path);
        else
            client.DeleteFile(entry.Path);
    }

    public void Rename(string path, string newPath) => client.RenameFile(path, newPath);

    public void CreateDirectory(string path) => client.CreateDirectory(path);

    /// <summary>
    /// Depth first, because SFTP will not remove a directory that still has
    /// anything in it and says so with an error code rather than doing it.
    /// </summary>
    private void DeleteTree(string path)
    {
        foreach (var entry in List(path))
        {
            if (entry.IsDirectory)
                DeleteTree(entry.Path);
            else
                client.DeleteFile(entry.Path);
        }
        client.DeleteDirectory(path);
    }
}
