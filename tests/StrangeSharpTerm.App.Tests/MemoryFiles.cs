using System.Text;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// A server's filesystem, in a dictionary.
///
/// Richer than the browser's fake on purpose: a workspace reads and writes file
/// contents, so a fake that only lists directories cannot exercise the half that
/// matters. Everything a test needs to arrange — what is there, what it says,
/// when it was last written, and what refuses — is here.
/// </summary>
public sealed class MemoryFiles : IRemoteFiles
{
    private readonly Dictionary<string, byte[]> _files = [];
    private readonly Dictionary<string, DateTime> _modified = [];
    private readonly HashSet<string> _directories = ["/", "/home", "/home/ops"];

    public string Home { get; set; } = "/home/ops";

    /// <summary>Every call that reached this, in order.</summary>
    public List<string> Did { get; } = [];

    /// <summary>Thrown by the next call, whatever it is.</summary>
    public Exception? Refuses { get; set; }

    public MemoryFiles With(string path, string content, DateTime? modified = null)
    {
        _files[path] = Encoding.UTF8.GetBytes(content);
        _modified[path] = modified ?? new DateTime(2026, 9, 2, 11, 30, 0);
        for (var parent = PosixPath.Parent(path); parent is not null; parent = PosixPath.Parent(parent))
            _directories.Add(parent);
        return this;
    }

    public MemoryFiles WithBytes(string path, params byte[] content)
    {
        _files[path] = content;
        _modified[path] = new DateTime(2026, 9, 2, 11, 30, 0);
        return this;
    }

    public MemoryFiles WithDirectory(string path)
    {
        _directories.Add(path);
        return this;
    }

    /// <summary>What a file says now, which is how a save is checked.</summary>
    public string Text(string path) => Encoding.UTF8.GetString(_files[path]);

    public bool Has(string path) => _files.ContainsKey(path) || _directories.Contains(path);

    /// <summary>Stands in for somebody else writing to the file while it is open here.</summary>
    public void Touch(string path, string content, DateTime modified) => With(path, content, modified);

    public IReadOnlyList<RemoteEntry> List(string path)
    {
        Check($"list {path}");
        return
        [
            .. _directories.Where(directory => PosixPath.Parent(directory) == path)
                .Select(directory => new RemoteEntry(
                    PosixPath.Name(directory), directory, true, false, 4096, new DateTime(2026, 9, 1))),
            .. _files.Where(file => PosixPath.Parent(file.Key) == path)
                .Select(file => new RemoteEntry(
                    PosixPath.Name(file.Key), file.Key, false, false, file.Value.Length, _modified[file.Key])),
        ];
    }

    public RemoteEntry? Stat(string path)
    {
        Check($"stat {path}");
        if (_directories.Contains(path))
            return new RemoteEntry(PosixPath.Name(path), path, true, false, 4096, new DateTime(2026, 9, 1));
        return _files.TryGetValue(path, out var content)
            ? new RemoteEntry(PosixPath.Name(path), path, false, false, content.Length, _modified[path])
            : null;
    }

    public byte[] Read(string path, long limit)
    {
        Check($"read {path}");
        return _files.TryGetValue(path, out var content)
            ? content[..(int)Math.Min(content.Length, limit)]
            : throw new FileNotFoundException(path);
    }

    public void Write(string path, byte[] content)
    {
        Check($"write {path}");
        _files[path] = content;
        _modified[path] = new DateTime(2026, 9, 3, 9, 0, 0);
    }

    public void Download(string remotePath, string localPath)
    {
        Check($"download {remotePath}");
        File.WriteAllBytes(localPath, _files[remotePath]);
    }

    public void Upload(string localPath, string remotePath)
    {
        Check($"upload {localPath} -> {remotePath}");
        _files[remotePath] = File.ReadAllBytes(localPath);
        _modified[remotePath] = new DateTime(2026, 9, 3, 9, 0, 0);
    }

    public void Delete(RemoteEntry entry)
    {
        Check($"delete {entry.Path}");
        _files.Remove(entry.Path);
        _directories.Remove(entry.Path);
    }

    public void Rename(string path, string newPath)
    {
        Check($"rename {path} -> {newPath}");
        if (_files.Remove(path, out var content))
        {
            _files[newPath] = content;
            _modified[newPath] = _modified[path];
        }
    }

    public void CreateDirectory(string path)
    {
        Check($"mkdir {path}");
        _directories.Add(path);
    }

    private void Check(string what)
    {
        Did.Add(what);
        if (Refuses is { } refusal)
        {
            Refuses = null;
            throw refusal;
        }
    }
}
