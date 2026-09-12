using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// The focused tab's backing.
///
/// A converter rather than a style trigger because the tab strip is an
/// ItemsControl of plain records: the item knows whether it has focus, and the
/// brush follows from that one fact.
/// </summary>
public sealed class ActiveTabBrush : IValueConverter
{
    public static ActiveTabBrush Instance { get; } = new();

    private static readonly IBrush Active = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Active : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
