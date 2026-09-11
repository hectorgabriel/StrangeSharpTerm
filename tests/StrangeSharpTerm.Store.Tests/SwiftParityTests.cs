using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Store.Tests;

/// <summary>
/// Checks against goldens produced by the Swift app's own code (see
/// build/swift-parity). Each compares whole files, byte for byte.
/// </summary>
public class SwiftParityTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void AnInventoryWrittenBySwiftRoundTripsByteForByte()
    {
        var swift = File.ReadAllBytes(FixturePath("inventory.swift.json"));

        var rewritten = InventoryDocument.From(InventoryDocument.Parse(swift).ToTree()).ToUtf8Json();

        Utf8(rewritten).ShouldBe(Utf8(swift));
    }

    [Fact]
    public void SavingAnInventoryLoadedFromSwiftChangesNoByte()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.PathTo(InventoryStore.FileName);
        Directory.CreateDirectory(directory.Root);
        File.Copy(FixturePath("inventory.swift.json"), path);
        var store = new InventoryStore(path);

        store.Save(store.Load().ShouldNotBeNull());

        Utf8(File.ReadAllBytes(path)).ShouldBe(Utf8(File.ReadAllBytes(FixturePath("inventory.swift.json"))));
    }

    public sealed record EncoderProbe(
        [property: JsonPropertyName("doubles")] double[] Doubles,
        [property: JsonPropertyName("ints")] long[] Ints,
        [property: JsonPropertyName("strings")] string[] Strings,
        [property: JsonPropertyName("keys")] Dictionary<string, int> Keys,
        [property: JsonPropertyName("emptyArray")] int[] EmptyArray,
        [property: JsonPropertyName("emptyObject")] Dictionary<string, int> EmptyObject,
        [property: JsonPropertyName("nested")] Dictionary<string, int[]>[] Nested);

    [Fact]
    public void NumbersStringsAndKeyOrderAreWrittenAsSwiftWritesThem()
    {
        // The same values build/swift-parity/main.swift encodes, built here instead.
        string[] keys =
        [
            "b", "B", "a", "A", "a10", "a9", "a01", "a1", "_x", "Z", "\u00E9", "e", "f", "ab", "a-b", "a_b",
            "AB", "Ab", "aB", "\uFF46", "1", "10", "9", "", " ", "x2y", "x10y",
        ];
        var probe = new EncoderProbe(
            Doubles:
            [
                0, -0.0, 1, 13, 13.5, -2.5, 0.1, 1.0 / 3.0, 0.0001, 0.00001, 123_456.789,
                779_000_000.123456, 1e15, 1e16, 9_007_199_254_740_992, 18_014_398_509_481_984,
                1.5e17, 1e21, 1e22, 1e100, 5e-324, 1.7976931348623157e308, 2.2250738585072014e-308,
                100, 1_234_567,
            ],
            Ints: [0, -1, long.MaxValue, long.MinValue],
            Strings:
            [
                "plain", "slash/inside", "quote\"", "back\\slash", "tab\t", "newline\n", "cr\r",
                "\b\f", "\0\u0001\u001F", "\u007F", "caf\u00E9", "\U0001F600", "\u2028\u2029", "<>&'+", "\uFEFF",
            ],
            Keys: keys.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index),
            EmptyArray: [],
            EmptyObject: [],
            Nested: [new() { ["inner"] = [1, 2] }, [], new() { ["empty"] = [] }]);

        Utf8(SwiftJson.Serialize(probe)).ShouldBe(File.ReadAllText(FixturePath("encoder-probe.swift.json")));
    }

    [Fact]
    public void ANumberJsonCannotSpellIsRefusedRatherThanWritten()
    {
        Should.Throw<JsonException>(() => SwiftJson.Serialize(new[] { double.NaN }));
        Should.Throw<JsonException>(() => SwiftJson.Serialize(new[] { double.PositiveInfinity }));
    }

    public sealed record ImportGolden(
        [property: JsonPropertyName("document")] InventoryDocument Document,
        [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

    [Fact]
    public void TheSshConfigImporterAgreesWithSwiftsOnARepresentativeFile()
    {
        var (tree, warnings) = SshConfigImporter.Inventory(SshConfigParser.Parse(File.ReadAllText(FixturePath("ssh_config.sample"))));

        // Normalised exactly as main.swift normalises the Swift side: the home
        // directory becomes $HOME, and ids are renumbered in document order.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nextForward = 1000;
        string Unhome(string path) => path.StartsWith(home, StringComparison.Ordinal) ? "$HOME" + path[home.Length..] : path;
        ConnectionSettings Normalize(ConnectionSettings settings) => settings with
        {
            IdentityFiles = settings.IdentityFiles?.Select(Unhome).ToArray(),
            KnownHostsFile = settings.KnownHostsFile is { } knownHosts ? Unhome(knownHosts) : null,
            PortForwards = settings.PortForwards?.Select(forward => forward with { Id = Id(++nextForward) }).ToArray(),
        };

        var imported = tree.Folders.Values.Single();
        var folder = imported with { Id = Id(1), Settings = Normalize(imported.Settings) };
        Connection[] connections =
        [
            .. tree.Connections.Values.OrderBy(c => c.SortIndex)
                .Select(c => c with { Id = Id(10 + c.SortIndex), ParentId = folder.Id, Settings = Normalize(c.Settings) }),
        ];
        var golden = new ImportGolden(
            new InventoryDocument { Version = InventoryDocument.CurrentVersion, Folders = [folder], Connections = connections },
            [.. warnings.Select(Describe)]);

        Utf8(SwiftJson.Serialize(golden)).ShouldBe(File.ReadAllText(FixturePath("ssh_config.import.swift.json")));
    }

    private static NodeId Id(int n) => new(Guid.Parse($"00000000-0000-4000-8000-{n:D12}"));

    private static string Describe(SshConfigWarning warning)
    {
        var line = warning.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "-";
        return warning.Reason switch
        {
            SshConfigWarningReason.MatchBlockSkipped skipped => $"{line} matchBlockSkipped {string.Join(' ', skipped.Criteria)}",
            SshConfigWarningReason.UnsupportedKeyword unsupported => $"{line} unsupportedKeyword {unsupported.Keyword}",
            SshConfigWarningReason.MalformedValue malformed => $"{line} malformedValue {malformed.Keyword} {malformed.Value}",
            _ => throw new UnreachableException(),
        };
    }
}
