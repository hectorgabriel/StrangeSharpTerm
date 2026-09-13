using System.Text;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>A stream a provider actually sent, read back as one.</summary>
internal static class Recorded
{
    internal static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static Stream Stream(string name) => new MemoryStream(Encoding.UTF8.GetBytes(Text(name)));
}
