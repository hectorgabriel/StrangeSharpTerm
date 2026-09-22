using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// Turns an icon's resource key into its geometry, or its image.
///
/// The view models name an icon; they do not hold one. This is the one place
/// that knows the names are resource keys, which keeps <see cref="ViewModels.SidebarRow"/>
/// testable without a running application.
///
/// A Lucide icon is a geometry for a Path's Data; a Fluent colour icon is a
/// drawing for an Image's Source. Which one comes back is whichever the target
/// can take, so a key given to the wrong kind of control draws nothing rather
/// than throwing.
/// </summary>
public sealed class IconConverter : IValueConverter
{
    public static IconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Application.Current is null)
            return null;
        return Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out var found)
            && targetType.IsInstanceOfType(found)
            ? found
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
