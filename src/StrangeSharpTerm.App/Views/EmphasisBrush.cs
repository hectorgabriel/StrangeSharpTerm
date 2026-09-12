using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// How a field's emphasis is painted.
///
/// The view model says what a value means; this is the only place that decides
/// what that looks like, so a warning is the same colour wherever it appears.
/// </summary>
public sealed class EmphasisBrush : IValueConverter
{
    public static EmphasisBrush Instance { get; } = new();

    public static IBrush Warning { get; } = new SolidColorBrush(Color.FromRgb(0xE2, 0xB0, 0x4A));

    public static IBrush Danger { get; } = new SolidColorBrush(Color.FromRgb(0xE5, 0x67, 0x5F));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FieldEmphasis.Warning => Warning,
        FieldEmphasis.Danger => Danger,
        // Not null: a null brush paints nothing, so an ordinary value would be
        // invisible while the alarming ones were the only ones legible.
        _ => Default,
    };

    private static IBrush Default =>
        Application.Current?.TryGetResource("ThemeForegroundBrush", Application.Current.ActualThemeVariant, out var found) is true
            && found is IBrush brush
                ? brush
                : Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
