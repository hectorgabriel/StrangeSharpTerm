using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.Mcp.Tests;

/// <summary>
/// Finding a bare command. An app launched from Finder inherits launchd's
/// <c>PATH</c> rather than a shell's, which is the single most common way a
/// local server fails to start — and it fails looking like the server's fault.
/// </summary>
public class CommandPathTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"strangesharpterm-path-{Guid.NewGuid():N}");

    public CommandPathTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ACommandInAnExtraDirectoryIsFound()
    {
        var tool = Make("faketool");

        CommandPath.Resolve("faketool", [_directory]).ShouldBe(tool);
        CommandPath.Exists("faketool", [_directory]).ShouldBeTrue();
    }

    [Fact]
    public void APathWithASeparatorIsTakenAsGiven()
    {
        // Someone who wrote a path has said where the thing is.
        var written = Path.Combine(_directory, "elsewhere");

        CommandPath.Resolve(written, [_directory]).ShouldBe(written);
    }

    [Fact]
    public void SomethingNowhereComesBackUnchanged() =>
        // Launching it anyway produces the operating system's own error, which
        // says more than anything invented here would.
        CommandPath.Resolve("definitely-not-installed-xyz", [_directory])
            .ShouldBe("definitely-not-installed-xyz");

    [Fact]
    public void SomethingNowhereIsSaidToBeMissing() =>
        CommandPath.Exists("definitely-not-installed-xyz", [_directory]).ShouldBeFalse();

    [Fact]
    public void AnEmptyCommandIsNotFound() => CommandPath.Exists("", [_directory]).ShouldBeFalse();

    [Fact]
    public void TheUsualPlacesAreLookedIn()
    {
        var places = CommandPath.ExtraDirectories;

        if (OperatingSystem.IsWindows())
        {
            places.ShouldContain(place => place.Contains("npm", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Homebrew on both architectures, and the per-user prefix the Python
            // and Node tools install into.
            places.ShouldContain("/opt/homebrew/bin");
            places.ShouldContain("/usr/local/bin");
            places.ShouldContain(place => place.EndsWith(Path.Combine(".local", "bin"), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ADirectoryIsNotACommand()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "adirectory"));

        CommandPath.Exists("adirectory", [_directory]).ShouldBeFalse();
    }

    /// <summary>An executable file, however this platform says that.</summary>
    private string Make(string name)
    {
        var path = Path.Combine(_directory, OperatingSystem.IsWindows() ? name + ".cmd" : name);
        File.WriteAllText(path, "#!/bin/sh\necho hello\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
