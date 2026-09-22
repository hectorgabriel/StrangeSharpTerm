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
/// Decoding a bitmap needs a renderer, and the plain unit tests build views
/// with no application and so no renderer: a Source set in markup would throw
/// there. Asked outside an application, this answers nothing, as
/// <see cref="IconConverter"/> does.
/// </summary>
public sealed class IllustrationConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, Bitmap> Loaded = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name || Application.Current is null)
            return null;
        return Loaded.GetOrAdd(name, static name =>
            new Bitmap(AssetLoader.Open(new Uri($"avares://StrangeSharpTerm/Illustrations/{name}.png"))));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
