using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

public static class ViewerCommands
{
    public static RoutedUICommand ToggleFullScreen { get; } = new("Tela cheia", nameof(ToggleFullScreen), typeof(ViewerCommands));

    public static RoutedUICommand ExitFullScreen { get; } = new("Sair da tela cheia", nameof(ExitFullScreen), typeof(ViewerCommands));
}

/// <summary>
/// Mostra a tela de alguém. Os quadros chegam numa thread de fundo; só o mais recente é desenhado, uma
/// vez por ciclo da interface, então um atraso momentâneo descarta quadros em vez de acumular.
/// </summary>
public partial class StreamViewerWindow : Window
{
    private static readonly TimeSpan ControlsHideDelay = TimeSpan.FromSeconds(3);

    private readonly StreamViewerViewModel _viewModel;
    private readonly Lock _frameLock = new();
    private readonly DispatcherTimer _hideControls;
    private byte[] _pending = [];
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _renderScheduled;
    private WriteableBitmap? _bitmap;
    private bool _isFullScreen;
    private WindowState _restoreState;
    private Point _lastMouse;

    public StreamViewerWindow(StreamViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        CommandBindings.Add(new CommandBinding(ViewerCommands.ToggleFullScreen, (_, _) => SetFullScreen(!_isFullScreen)));
        CommandBindings.Add(new CommandBinding(ViewerCommands.ExitFullScreen, (_, _) => SetFullScreen(false)));

        _hideControls = new DispatcherTimer { Interval = ControlsHideDelay };
        _hideControls.Tick += (_, _) =>
        {
            _hideControls.Stop();
            if (_isFullScreen)
            {
                ControlBar.Visibility = Visibility.Collapsed;
                Cursor = Cursors.None;
            }
        };

        viewModel.FrameReady += OnFrameReady;
        viewModel.CloseRequested += (_, _) => Dispatcher.BeginInvoke(Close);
        Closed += (_, _) =>
        {
            viewModel.FrameReady -= OnFrameReady;
            _hideControls.Stop();
        };
    }

    private void OnFrameReady(byte[] bgra, int width, int height, long timestamp)
    {
        lock (_frameLock)
        {
            if (_pending.Length != bgra.Length)
            {
                _pending = new byte[bgra.Length];
            }

            Buffer.BlockCopy(bgra, 0, _pending, 0, bgra.Length);
            _pendingWidth = width;
            _pendingHeight = height;
            if (_renderScheduled)
            {
                return;
            }

            _renderScheduled = true;
        }

        Dispatcher.BeginInvoke(Render, DispatcherPriority.Render);
    }

    private void Render()
    {
        lock (_frameLock)
        {
            _renderScheduled = false;
            if (_pendingWidth <= 0 || _pendingHeight <= 0)
            {
                return;
            }

            if (_bitmap is null || _bitmap.PixelWidth != _pendingWidth || _bitmap.PixelHeight != _pendingHeight)
            {
                _bitmap = new WriteableBitmap(_pendingWidth, _pendingHeight, 96, 96, PixelFormats.Bgr32, null);
                Video.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, _pendingWidth, _pendingHeight), _pending, _pendingWidth * 4, 0);
        }
    }

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetFullScreen(!_isFullScreen);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Pequenos tremores do mouse não devem reexibir os controles.
        var position = e.GetPosition(this);
        if ((position - _lastMouse).Length < 3)
        {
            return;
        }

        _lastMouse = position;
        ControlBar.Visibility = Visibility.Visible;
        Cursor = null;
        if (_isFullScreen)
        {
            _hideControls.Stop();
            _hideControls.Start();
        }
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _isFullScreen)
        {
            return;
        }

        _isFullScreen = fullScreen;
        if (fullScreen)
        {
            _restoreState = WindowState;

            // Precisa sair de "maximizada" antes de tirar a borda, senão a barra de tarefas continua visível.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _hideControls.Start();
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState;
            _hideControls.Stop();
            ControlBar.Visibility = Visibility.Visible;
            Cursor = null;
        }
    }
}
