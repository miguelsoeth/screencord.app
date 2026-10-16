using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ScreenCord.Views;

/// <summary>
/// O nome do app em tamanho grande, desenhado como geometria (o contorno dos glifos) em vez de texto. O rasterizador
/// de texto do WPF quase não suaviza na vertical, e as curvas mais horizontais (o alto do "S", do "C", do "o")
/// ficam em degraus; a geometria passa pelo mesmo antisserrilhado das formas, liso nas duas direções. Para texto
/// pequeno o texto comum continua melhor, por causa do hinting.
/// </summary>
public sealed class WordmarkText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(WordmarkText), Affects(string.Empty, measure: true));

    public static readonly DependencyProperty AccentTextProperty = DependencyProperty.Register(
        nameof(AccentText), typeof(string), typeof(WordmarkText), Affects(string.Empty, measure: true));

    public static readonly DependencyProperty AccentForegroundProperty = DependencyProperty.Register(
        nameof(AccentForeground), typeof(Brush), typeof(WordmarkText), Affects(null, measure: false));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(WordmarkText), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(WordmarkText), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(WordmarkText), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutChanged));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(WordmarkText), new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutChanged));

    private Geometry? _text;
    private Geometry? _accent;
    private Size _size;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string AccentText
    {
        get => (string)GetValue(AccentTextProperty);
        set => SetValue(AccentTextProperty, value);
    }

    public Brush? AccentForeground
    {
        get => (Brush?)GetValue(AccentForegroundProperty);
        set => SetValue(AccentForegroundProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_text is null)
        {
            Build();
        }

        return _size;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_text is null)
        {
            Build();
        }

        drawingContext.DrawGeometry(Foreground, null, _text);
        drawingContext.DrawGeometry(AccentForeground ?? Foreground, null, _accent);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _text = null;
        InvalidateMeasure();
    }

    private void Build()
    {
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = Format(Text, typeface, pixelsPerDip);
        var accent = Format(AccentText, typeface, pixelsPerDip);

        // A parte de destaque começa onde a primeira termina, como duas Runs na mesma linha.
        _text = Frozen(text.BuildGeometry(new Point(0, 0)));
        _accent = Frozen(accent.BuildGeometry(new Point(text.WidthIncludingTrailingWhitespace, 0)));
        _size = new Size(
            text.WidthIncludingTrailingWhitespace + accent.WidthIncludingTrailingWhitespace,
            Math.Max(text.Height, accent.Height));
    }

    private FormattedText Format(string value, Typeface typeface, double pixelsPerDip) =>
        new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, FontSize, Brushes.Black, pixelsPerDip);

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WordmarkText)d)._text = null;

    private static FrameworkPropertyMetadata Affects(object? defaultValue, bool measure) =>
        new(defaultValue,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure : FrameworkPropertyMetadataOptions.AffectsRender,
            measure ? OnLayoutChanged : null);
}
