using System.Windows;
using System.Windows.Media;

namespace ScreenCord.Views;

/// <summary>
/// Recorta um elemento num retângulo de cantos arredondados que acompanha o tamanho dele. Serve para o conteúdo
/// de um <c>Border</c> com <c>CornerRadius</c> (ex.: o vídeo no cartão do palco) não cobrir os cantos e a borda,
/// o que <c>ClipToBounds</c> não resolve, porque recorta em retângulo.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnCornerRadiusChanged));

    public static double GetCornerRadius(DependencyObject element) => (double)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, double value) => element.SetValue(CornerRadiusProperty, value);

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue > 0)
        {
            element.SizeChanged += OnSizeChanged;
        }

        Update(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element)
    {
        var radius = GetCornerRadius(element);
        element.Clip = radius > 0
            ? new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius)
            : null;
    }
}
