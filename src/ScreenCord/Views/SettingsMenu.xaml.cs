using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ScreenCord.Infrastructure;
using ScreenCord.Localization;

namespace ScreenCord.Views;

/// <summary>
/// Menu da engrenagem: idioma (troca na hora e salva a escolha), envio dos relatórios de erro
/// (<see cref="TelemetryPreference"/>) e a janela "Sobre".
/// </summary>
public partial class SettingsMenu : UserControl
{
    public SettingsMenu()
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
        ShowState();
        var current = Loc.Language switch
        {
            AppLanguage.English => EnglishOption,
            AppLanguage.Spanish => SpanishOption,
            _ => PortugueseOption,
        };
        Dispatcher.BeginInvoke(() => Keyboard.Focus(current), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowState()
    {
        PortugueseCheck.Visibility = CheckVisibility(Loc.Language == AppLanguage.Portuguese);
        EnglishCheck.Visibility = CheckVisibility(Loc.Language == AppLanguage.English);
        SpanishCheck.Visibility = CheckVisibility(Loc.Language == AppLanguage.Spanish);
        ErrorReportsCheck.Visibility = CheckVisibility(TelemetryPreference.Enabled);
    }

    private static Visibility CheckVisibility(bool isChecked) => isChecked ? Visibility.Visible : Visibility.Hidden;

    // O menu continua aberto: dá para ver o texto trocar e mudar outra opção em seguida.
    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string code } && Loc.FromCode(code) is { } language)
        {
            Loc.SetLanguage(language);
            LanguagePreference.Save(language);
        }

        ShowState();
    }

    private void OnErrorReportsClick(object sender, RoutedEventArgs e)
    {
        TelemetryPreference.Set(!TelemetryPreference.Enabled);
        ShowState();
    }

    // O comando abre a janela; aqui só fecha o menu.
    private void OnAboutClick(object sender, RoutedEventArgs e) => Toggle.IsChecked = false;
}
