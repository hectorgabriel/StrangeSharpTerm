using System.Globalization;
using Avalonia.Data.Converters;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// Whether a gauge is at a given pressure, as a style class needs it: a bool.
///
/// Two instances rather than a parameter, because a class binding takes no
/// parameter and the alternative is a converter that reads a string.
/// </summary>
public sealed class PressureIs(Pressure pressure) : IValueConverter
{
    public static PressureIs Warning { get; } = new(ViewModels.Pressure.Warning);

    public static PressureIs Critical { get; } = new(ViewModels.Pressure.Critical);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Pressure actual && actual == pressure;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
