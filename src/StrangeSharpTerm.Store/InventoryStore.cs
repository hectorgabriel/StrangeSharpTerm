using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Store;

public interface IInventoryPersistence
{
    /// <summary>Null when nothing has been saved yet, which is how first launch is detected.</summary>
    InventoryTree? Load();

    void Save(InventoryTree tree);
}

/// <summary>
/// Reads and writes the connection inventory: one JSON document, written
/// atomically. <see cref="IInventoryPersistence"/> is the seam if that ever needs
/// to become a database.
/// </summary>
public sealed class InventoryStore(string filePath) : IInventoryPersistence
{
    public const string FileName = "inventory.json";

    public string FilePath { get; } = filePath;

    /// <summary>
    /// <c>%APPDATA%\StrangeSharpTerm</c> on Windows and
    /// <c>~/Library/Application Support/StrangeSharpTerm</c> on macOS, which is
    /// where .NET maps <see cref="Environment.SpecialFolder.ApplicationData"/> there.
    /// </summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrangeSharpTerm", FileName);

    /// <summary>Where the Swift StrangeTerm app keeps its inventory, newest location first.</summary>
    public static IReadOnlyList<string> SwiftAppInventoryPaths()
    {
        if (!OperatingSystem.IsMacOS())
            return [];
        var library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library");
        return
        [
            Path.Combine(library, "Group Containers", "C56S7AQ6L8.dev.strangeterm.group", FileName),
            Path.Combine(library, "Application Support", "StrangeTerm", FileName),
        ];
    }

    /// <summary>
    /// Copies the first inventory found among <paramref name="candidates"/> to
    /// <paramref name="destination"/>, when the destination has none yet.
    ///
    /// One-way and one-time. The source is left in place, so the Swift app keeps
    /// working until it is archived, and an existing inventory here is never
    /// overwritten. The source is parsed first, so a file this version cannot read
    /// is refused now rather than on every launch after.
    /// </summary>
    /// <returns>Whether an inventory was copied.</returns>
    public static bool ImportIfAbsent(string destination, IEnumerable<string> candidates)
    {
        if (File.Exists(destination))
            return false;
        var source = candidates.FirstOrDefault(File.Exists);
        if (source is null)
            return false;

        InventoryDocument.Parse(File.ReadAllBytes(source));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.Copy(source, destination);
        return true;
    }

    public InventoryTree? Load() =>
        File.Exists(FilePath) ? InventoryDocument.Parse(File.ReadAllBytes(FilePath)).ToTree() : null;

    public void Save(InventoryTree tree)
    {
        var bytes = InventoryDocument.From(tree).ToUtf8Json();
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(directory);

        // Atomic: a crash mid-write must not leave a truncated file where the user's
        // entire host list used to be. Write beside the target, flush to disk, then
        // rename over it, which is rename(2) on macOS and MoveFileEx on Windows.
        var temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
