using System.Windows;
using ScreenCord.Media;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

/// <summary>Abre as janelas do compartilhamento de tela a partir da janela principal.</summary>
public sealed class ScreenShareUi : IScreenShareUi
{
    private readonly Dictionary<StreamViewerViewModel, StreamViewerWindow> _windows = [];

    public ShareRequest? PickShare(ShareSettings initial)
    {
        var picker = new SharePickerWindow(new SharePickerViewModel(initial)) { Owner = Application.Current.MainWindow };
        return picker.ShowDialog() == true ? picker.Result : null;
    }

    public void ShowViewer(StreamViewerViewModel viewer, Action closed)
    {
        var window = new StreamViewerWindow(viewer);
        _windows[viewer] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(viewer);
            closed();
        };
        window.Show();
    }

    public void ActivateViewer(StreamViewerViewModel viewer)
    {
        if (_windows.TryGetValue(viewer, out var window))
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }
    }
}
