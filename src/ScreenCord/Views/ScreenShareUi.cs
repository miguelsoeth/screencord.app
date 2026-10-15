using System.Windows;
using ScreenCord.Media;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

/// <summary>
/// Liga o compartilhamento de tela à interface: a escolha do que compartilhar abre numa janela; a tela de
/// quem você assiste aparece na área principal da janela principal, uma de cada vez.
/// </summary>
public sealed class ScreenShareUi : IScreenShareUi
{
    // A janela principal depende (pelos view models) deste serviço, então é buscada na hora em vez de injetada.
    private static MainWindow Main => Application.Current.Windows.OfType<MainWindow>().First();

    public ShareRequest? PickShare(ShareSettings initial, CaptureSource? current = null)
    {
        var picker = new SharePickerWindow(new SharePickerViewModel(initial, current: current)) { Owner = Application.Current.MainWindow };
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
