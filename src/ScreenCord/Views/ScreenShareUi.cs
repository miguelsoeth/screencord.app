using System.Windows;
using ScreenCord.Contracts;
using ScreenCord.Media;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

/// <summary>
/// Liga o compartilhamento de tela à interface: a escolha do que compartilhar abre numa janela; a tela de
/// quem você assiste aparece na área principal da janela principal, até duas ao mesmo tempo.
/// </summary>
public sealed class ScreenShareUi : IScreenShareUi
{
    // A janela principal depende (pelos view models) deste serviço, então é buscada na hora em vez de injetada.
    private static MainWindow Main => Application.Current.Windows.OfType<MainWindow>().First();

    public ShareRequest? PickShare(ShareSettings initial, StreamLimits limits, CaptureSource? current = null, GroupSettings? allowed = null)
    {
        var picker = new SharePickerWindow(new SharePickerViewModel(initial, current: current, limits: limits, allowed: allowed)) { Owner = Application.Current.MainWindow };
        return picker.ShowDialog() == true ? picker.Result : null;
    }

    public void ShowViewer(StreamViewerViewModel viewer, Action closed) => Main.ShowStream(viewer, closed);

    public void ActivateViewer(StreamViewerViewModel viewer)
    {
        var window = Main;
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
