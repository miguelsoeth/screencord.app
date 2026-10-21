using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ScreenCord.Infrastructure;

namespace ScreenCord.Views;

/// <summary>Liga e desliga o envio dos relatórios de erro (<see cref="TelemetryPreference"/>); vale na hora.</summary>
public partial class PrivacyMenu : UserControl
{
    public PrivacyMenu()
    {
        InitializeComponent();
        Menu.CustomPopupPlacementCallback = PlaceMenu;
    }

    // Mesmo lugar do menu de idioma: embaixo do botão, crescendo para a esquerda (ver LanguagePicker).
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
        Dispatcher.BeginInvoke(() => Keyboard.Focus(ErrorReportsOption), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnErrorReportsClick(object sender, RoutedEventArgs e)
    {
        TelemetryPreference.Set(!TelemetryPreference.Enabled);
        ShowState();
    }

    private void ShowState()
    {
        ErrorReportsCheck.Visibility = TelemetryPreference.Enabled ? Visibility.Visible : Visibility.Hidden;
    }
}
