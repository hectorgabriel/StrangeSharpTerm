namespace StrangeSharpTerm.Store.Tests;

/// <summary>
/// Opt-in checks against real files, which never enter the repository. Point an
/// environment variable at a file to run one; unset, they skip.
/// </summary>
public class RealFileTests
{
    private const string InventoryVariable = "STRANGESHARPTERM_REAL_INVENTORY";
    private const string SshConfigVariable = "STRANGESHARPTERM_REAL_SSH_CONFIG";

    [Fact]
    public void ARealInventoryWrittenBySwiftRoundTripsByteForByte()
    {
        var path = Environment.GetEnvironmentVariable(InventoryVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path), $"Set {InventoryVariable} to an inventory.json written by the Swift app.");

        var original = File.ReadAllBytes(path!);
        var rewritten = InventoryDocument.From(InventoryDocument.Parse(original).ToTree()).ToUtf8Json();

        rewritten.SequenceEqual(original).ShouldBeTrue("the rewritten inventory differs from the original");
    }

    [Fact]
    public void ARealSshConfigImportsAndEveryHostResolves()
    {
        var path = Environment.GetEnvironmentVariable(SshConfigVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path), $"Set {SshConfigVariable} to an ssh_config file.");

        var (tree, warnings) = SshConfigImporter.Inventory(SshConfigParser.Parse(File.ReadAllText(path!)));
        foreach (var id in tree.Connections.Keys)
            tree.Resolve(id);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{tree.Connections.Count} hosts, {warnings.Count} warnings: {string.Join("; ", warnings)}");
    }
}
