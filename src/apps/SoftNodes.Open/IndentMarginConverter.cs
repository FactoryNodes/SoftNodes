using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SoftNodesOpen;

/// <summary>Una sangría en píxeles como margen izquierdo (las filas de la columna «Variables» del depurador). Solo pintar.</summary>
public sealed class IndentMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is double indent ? indent : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
