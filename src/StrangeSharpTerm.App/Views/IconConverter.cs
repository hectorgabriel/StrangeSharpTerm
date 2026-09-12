using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// Turns an icon's resource key into its geometry.
///
/// The view models name an icon; they do not hold one. This is the one place
/// that knows the names are resource keys, which keeps <see cref="ViewModels.SidebarRow"/>
/// testable without a running application.
/// </summary>
public sealed class IconConverter : IValueConverter
{
    public static IconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Application.Current is null)
            return null;
        return Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out var found)
            ? found as Geometry
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
