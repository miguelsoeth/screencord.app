using System.Windows;
using System.Windows.Controls;
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
/// Mostra a tela de alguém (o <see cref="StreamViewerViewModel"/> do DataContext). Os quadros chegam numa
/// thread de fundo; só o mais recente é desenhado, uma vez por ciclo da interface, então um atraso momentâneo
/// descarta quadros em vez de acumular. Na janela principal, a tela cheia abre outra instância numa janela
/// sem borda (<see cref="IsFullScreen"/>), que fecha com Esc, F11 ou duplo clique.
/// </summary>
public partial class StreamPlayer : UserControl
{
    private static readonly TimeSpan ControlsHideDelay = TimeSpan.FromSeconds(3);

    private readonly Lock _frameLock = new();
    private readonly DispatcherTimer _hideControls;
    private StreamViewerViewModel? _viewer;
    private byte[] _pending = [];
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _renderScheduled;
    private WriteableBitmap? _bitmap;
    private Window? _fullScreenWindow;
    private Point _lastMouse;

    public StreamPlayer()
        : this(isFullScreen: false)
    {
    }

    private StreamPlayer(bool isFullScreen)
    {
        InitializeComponent();
        IsFullScreen = isFullScreen;

        // Em tela cheia, sair é pelo Esc/F11/duplo clique; parar de assistir fica na janela principal.
        CloseButton.Visibility = isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenButton.ToolTip = isFullScreen ? "Sair da tela cheia (Esc)" : "Tela cheia (F11)";

        CommandBindings.Add(new CommandBinding(ViewerCommands.ToggleFullScreen, (_, _) => ToggleFullScreen()));
        CommandBindings.Add(new CommandBinding(ViewerCommands.ExitFullScreen, (_, _) => ExitFullScreen()));

        _hideControls = new DispatcherTimer { Interval = ControlsHideDelay };
        _hideControls.Tick += (_, _) =>
        {
            _hideControls.Stop();
            if (IsFullScreen)
            {
                ControlBar.Visibility = Visibility.Collapsed;
                Cursor = Cursors.None;
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                Attach(DataContext as StreamViewerViewModel);
            }
        };
        Loaded += (_, _) =>
        {
            Attach(DataContext as StreamViewerViewModel);
            if (IsFullScreen)
            {
                _hideControls.Start();
                Focus();
            }
        };
        Unloaded += (_, _) =>
        {
            Attach(null);
            _hideControls.Stop();
        };
    }

    /// <summary>Esta instância é a da janela de tela cheia.</summary>
    public bool IsFullScreen { get; }

    /// <summary>Fecha a tela cheia aberta a partir deste player, se houver.</summary>
    public void ExitFullScreen()
    {
        if (IsFullScreen)
        {
            Window.GetWindow(this)?.Close();
        }
        else
        {
            _fullScreenWindow?.Close();
        }
    }

    private void ToggleFullScreen()
    {
        if (IsFullScreen || _fullScreenWindow is not null)
        {
            ExitFullScreen();
            return;
        }

        if (_viewer is null)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Title = _viewer.Title,
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Black,
            ShowInTaskbar = false,
            Content = new StreamPlayer(isFullScreen: true) { DataContext = _viewer },
        };

        // Abre no mesmo monitor da janela principal: posiciona sobre ela e só então maximiza.
        if (owner is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = owner.Left;
            window.Top = owner.Top;
            window.Width = owner.ActualWidth;
            window.Height = owner.ActualHeight;
        }

        window.Loaded += (_, _) => window.WindowState = WindowState.Maximized;
        window.Closed += (_, _) =>
        {
            _fullScreenWindow = null;
            owner?.Activate();
        };
        _fullScreenWindow = window;
        window.Show();
    }

    private void Attach(StreamViewerViewModel? viewer)
    {
        if (viewer == _viewer)
        {
            return;
        }

        if (_viewer is not null)
        {
            _viewer.FrameReady -= OnFrameReady;
            _viewer.CloseRequested -= OnCloseRequested;
        }

        _viewer = viewer;
        lock (_frameLock)
        {
            _pendingWidth = _pendingHeight = 0;
        }

        _bitmap = null;
        Video.Source = null;
        if (viewer is not null)
        {
            viewer.FrameReady += OnFrameReady;
            viewer.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ExitFullScreen);

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
        Focus();
        if (e.ClickCount == 2)
        {
            ToggleFullScreen();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _viewer?.RequestClose();

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
        if (IsFullScreen)
        {
            _hideControls.Stop();
            _hideControls.Start();
        }
    }
}
