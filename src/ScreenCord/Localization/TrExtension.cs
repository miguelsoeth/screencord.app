using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace ScreenCord.Localization;

/// <summary>
/// Texto traduzido no XAML: <c>{l:Tr Chave}</c>, uma propriedade de <see cref="Strings"/>. Com <c>Path</c>, a
/// chave é um formato e o <c>{0}</c> vem dessa ligação: <c>{l:Tr WatchScreenOfFormat, Path=DisplayName}</c>.
/// Muda na hora quando o idioma troca.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public string? Path { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (!Loc.HasKey(Key))
        {
            throw new ArgumentException($"Texto \"{Key}\" não existe em Strings.");
        }

        var text = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        if (Path is null)
        {
            return text.ProvideValue(serviceProvider);
        }

        var formatted = new MultiBinding { Converter = FormatConverter.Instance, Mode = BindingMode.OneWay };
        formatted.Bindings.Add(text);
        formatted.Bindings.Add(new Binding(Path) { Mode = BindingMode.OneWay });
        return formatted.ProvideValue(serviceProvider);
    }

    private sealed class FormatConverter : IMultiValueConverter
    {
        public static readonly FormatConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [string format, var argument] ? string.Format(Loc.Culture, format, argument) : "";

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
