using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ScreenCord.Localization;

namespace ScreenCord.Views;

/// <summary>Troca o idioma do app na hora e salva a escolha (a mesma que o instalador grava).</summary>
public partial class LanguagePicker : UserControl
{
    public LanguagePicker()
    {
        InitializeComponent();
        Menu.CustomPopupPlacementCallback = PlaceMenu;
    }

    /// <summary>
    /// Embaixo do botão, com a borda direita do cartão na do botão (o menu cresce para a esquerda, para dentro da
    /// janela). Os tamanhos chegam em pixels; a margem da sombra (14) e o espaço (4) estão em unidades do WPF.
    /// </summary>
    private CustomPopupPlacement[] PlaceMenu(Size popup, Size target, Point offset)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var shadow = 14 * dpi.DpiScaleX;
        var gap = 4 * dpi.DpiScaleY;
        return [new CustomPopupPlacement(new Point(target.Width - popup.Width + shadow, target.Height + gap - shadow), PopupPrimaryAxis.Horizontal)];
    }

    private void OnMenuOpened(object? sender, EventArgs e)
    {
        PortugueseCheck.Visibility = CheckVisibility(AppLanguage.Portuguese);
        EnglishCheck.Visibility = CheckVisibility(AppLanguage.English);
        SpanishCheck.Visibility = CheckVisibility(AppLanguage.Spanish);

        var current = Loc.Language switch
        {
            AppLanguage.English => EnglishOption,
            AppLanguage.Spanish => SpanishOption,
            _ => PortugueseOption,
        };
        Dispatcher.BeginInvoke(() => Keyboard.Focus(current), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static Visibility CheckVisibility(AppLanguage language) =>
        Loc.Language == language ? Visibility.Visible : Visibility.Hidden;

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        Toggle.IsChecked = false;
        if (sender is FrameworkElement { Tag: string code } && Loc.FromCode(code) is { } language)
        {
            Loc.SetLanguage(language);
            LanguagePreference.Save(language);
        }

        Toggle.Focus();
    }
}
