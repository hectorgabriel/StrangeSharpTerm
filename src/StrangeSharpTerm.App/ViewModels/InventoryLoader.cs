using StrangeSharpTerm.Model;
using StrangeSharpTerm.Store;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// Where the inventory comes from on launch.
///
/// Saved inventory first, then a one-time ssh_config import, then nothing.
/// Reading the hosts someone already has is what makes a first launch useful
/// rather than empty, and an import is written out immediately so the file on
/// disk matches what is on screen.
///
/// No sample data. A real launch with nothing saved and no ssh_config shows an
/// empty state inviting the user to add a host; shipping fake hosts that cannot
/// connect would be worse than showing nothing.
/// </summary>
public static class InventoryLoader
{
    /// <summary>Keeps development and tests out of the real Application Support directory.</summary>
    public const string InventoryVariable = "STRANGESHARPTERM_INVENTORY";

    /// <summary>Points the one-time import at something other than <c>~/.ssh/config</c>.</summary>
    public const string SshConfigVariable = "STRANGESHARPTERM_SSH_CONFIG";

    public static InventoryViewModel Load() => Load(
        Environment.GetEnvironmentVariable(InventoryVariable) ?? InventoryStore.DefaultPath(),
        Environment.GetEnvironmentVariable(SshConfigVariable) ?? DefaultSshConfigPath(),
        InventoryStore.SwiftAppInventoryPaths());

    public static InventoryViewModel Load(string inventoryPath, string? sshConfigPath, IEnumerable<string> inherited)
    {
        // A first run on a machine that has the Swift app inherits its hosts,
        // copied once and never overwriting anything already here.
        TryInherit(inventoryPath, inherited);

        var store = new InventoryStore(inventoryPath);
        InventoryTree? saved = null;
        string? failure = null;
        try
        {
            saved = store.Load();
        }
        catch (Exception e)
        {
            // An unreadable inventory must not cost the user their session: the
            // app opens empty, says why, and does not overwrite the file.
            failure = e.Message;
        }

        if (saved is { } tree && tree.Connections.Count > 0)
            return new InventoryViewModel(store, tree);

        if (Import(sshConfigPath) is { } imported)
        {
            // Written out at once rather than on the first edit, so the file on
            // disk matches what the window shows from the moment it opens.
            try
            {
                store.Save(imported);
            }
            catch (Exception)
            {
                // In memory is still better than nothing; the next save reports it.
            }
            return new InventoryViewModel(store, imported);
        }

        // A store that could not be read is not handed back: edits stay in memory
        // rather than overwriting a file this version did not understand.
        return new InventoryViewModel(failure is null ? store : null, saved ?? new InventoryTree());
    }

    public static string DefaultSshConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    private static void TryInherit(string destination, IEnumerable<string> candidates)
    {
        try
        {
            InventoryStore.ImportIfAbsent(destination, candidates);
        }
        catch (Exception)
        {
            // An inventory this version cannot read is not a reason to refuse to
            // start; the app opens empty instead.
        }
    }

    private static InventoryTree? Import(string? sshConfigPath)
    {
        if (sshConfigPath is null || !File.Exists(sshConfigPath))
            return null;
        try
        {
            var (tree, _) = SshConfigImporter.Inventory(SshConfigParser.Parse(File.ReadAllText(sshConfigPath)));
            return tree.Connections.Count > 0 ? tree : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
