using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// Turns an illustration's name into its picture: one of the 3dicons PNGs in
/// <c>Illustrations/</c>, vendored by <c>build/icons/fetch_3dicons.py</c>.
///
/// It also takes a Lucide key, for the icons a view model names rather than a
/// view: a row does not know whether its folder is a picture, so it asks for
/// <c>IconFolder</c> from both this and <see cref="IconConverter"/>, and
/// <see cref="ForIcon"/> decides which of the two answers. A key with no picture
/// answers nothing here, and its line icon draws instead.
///
/// Decoding a bitmap needs a renderer, and the plain unit tests build views
/// with no application and so no renderer: a Source set in markup would throw
/// there. Asked outside an application, this answers nothing, as
/// <see cref="IconConverter"/> does.
/// </summary>
public sealed class IllustrationConverter : IValueConverter
{
    /// <summary>
    /// The Lucide keys a view model can name that have a picture. Only those:
    /// a view that knows its icon names the picture directly.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForIcon { get; } = new Dictionary<string, string>
    {
        ["IconServer"] = "computer",
        ["IconFolder"] = "folder",
        ["IconFolderOpen"] = "folder",
        ["IconFile"] = "file",
    };

    private static readonly ConcurrentDictionary<string, Bitmap> Loaded = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name || Application.Current is null)
            return null;
        if (ForIcon.TryGetValue(name, out var picture))
            name = picture;
        else if (name.StartsWith("Icon", StringComparison.Ordinal))
            return null;
        return Loaded.GetOrAdd(name, static name =>
            new Bitmap(AssetLoader.Open(new Uri($"avares://StrangeSharpTerm/Illustrations/{name}.png"))));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
