using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScreenCord.Views;

/// <summary>Inverte um booleano (ex.: desabilitar o clique enquanto um popup está aberto).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Verdadeiro → Collapsed; falso → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// [id da pessoa, ids de quem compartilha] → Visible se a pessoa está compartilhando a tela.
/// Reavaliado sempre que o conjunto de quem compartilha muda.
/// </summary>
public sealed class IsSharingToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is Guid userId && values[1] is IReadOnlySet<Guid> sharing && sharing.Contains(userId)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
