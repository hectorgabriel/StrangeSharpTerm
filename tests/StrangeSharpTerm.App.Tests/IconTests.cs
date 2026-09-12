using System.Xml.Linq;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The icon set is generated (build/icons/generate.py) and committed. These guard
/// the two ways a regeneration can go wrong quietly: an icon the app relies on
/// disappearing, and a geometry arriving empty or unparseable.
/// </summary>
public class IconTests
{
    private static readonly XDocument Icons = XDocument.Load(IconPath());

    private static string IconPath()
    {
        // Walk out of the test's bin directory to the source that is committed.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src/StrangeSharpTerm.App/Icons/Icons.axaml");
    }

    private static IReadOnlyDictionary<string, string> Geometries =>
        Icons.Root!.Elements()
            .ToDictionary(
                element => element.Attributes().First(a => a.Name.LocalName == "Key").Value,
                element => element.Value);

    [Fact]
    public void EverySymbolTheSwiftAppUsedHasAReplacement()
    {
        // 34 SF Symbols across the Swift UI, mapped in one reviewed pass rather
        // than glyph by glyph. See docs/adr/0003.
        string[] required =
        [
            "IconRefreshCw", "IconArrowLeftRight", "IconArrowRightToLine", "IconArrowUp", "IconZap",
            "IconCircleCheck", "IconChevronDown", "IconChevronRight", "IconChevronUp", "IconChevronsUpDown",
            "IconRadioTower", "IconEllipsis", "IconOctagonAlert", "IconTriangleAlert", "IconFolder",
            "IconSettings", "IconInfo", "IconKey", "IconShieldCheck", "IconSearch", "IconCircleMinus",
            "IconPlus", "IconRows2", "IconColumns2", "IconServer", "IconSlidersHorizontal", "IconSparkles",
            "IconLayers", "IconSquare", "IconTextCursorInput", "IconTrash2", "IconX", "IconCircleX",
        ];

        foreach (var icon in required)
            Geometries.ShouldContainKey(icon);
    }

    [Fact]
    public void EveryGeometryStartsSomewhereAndDrawsSomething()
    {
        foreach (var (key, geometry) in Geometries)
        {
            geometry.ShouldNotBeNullOrWhiteSpace($"{key} has no geometry");
            geometry.TrimStart().ShouldStartWith("M", Case.Sensitive,
                $"{key} does not begin with an absolute move, so it would be drawn wherever the last one ended");
        }
    }

    [Fact]
    public void TheSetIsTheGeneratedOneRatherThanHandEdited()
    {
        // A hand-edited icon is one nobody can regenerate; the header says so and
        // this keeps the header there.
        var text = File.ReadAllText(IconPath());

        text.ShouldContain("build/icons/generate.py");
        text.ShouldContain("Lucide 1.45.0");
        File.Exists(Path.Combine(Path.GetDirectoryName(IconPath())!, "LICENSE-lucide.txt")).ShouldBeTrue();
    }
}
