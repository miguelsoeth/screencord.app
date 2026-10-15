using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

public partial class MainWindow : Window
{
    /// <summary>Largura da lateral de grupos mais o espaço entre ela e a tela.</summary>
    private const double SidebarSpace = 252 + 12;

    /// <summary>Espaço entre o cabeçalho e a tela.</summary>
    private const double StageGap = 12;

    private readonly MainViewModel _viewModel;
    private StreamViewerViewModel? _watching;
    private Action? _watchingClosed;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += async (_, _) => await viewModel.InitializeAsync();

        // Menu de amigos: abaixo do cabeçalho, com a borda direita alinhada à dele.
        FriendsPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width - 13, targetSize.Height - 6), PopupPrimaryAxis.Horizontal)];

        RootGrid.SizeChanged += (_, _) => UpdateStageSize();
        HeaderCard.SizeChanged += (_, _) => UpdateStageSize();
        ProfileError.SizeChanged += (_, _) => UpdateStageSize();
        ProfileError.IsVisibleChanged += (_, _) => UpdateStageSize();

        // Minimizada, ninguém vê o vídeo: só o áudio continua, poupando a banda do servidor.
        StateChanged += (_, _) =>
        {
            if (_watching is { } viewer)
            {
                viewer.IsVideoPaused = WindowState == WindowState.Minimized;
            }
        };
    }

    /// <summary>
    /// Dimensiona a tela no maior 16:9 que cabe ao lado da lateral e abaixo do cabeçalho. O bloco todo fica
    /// centralizado na janela, e a lateral de grupos tem a mesma altura que ele.
    /// </summary>
    private void UpdateStageSize()
    {
        var margin = MainGrid.Margin;
        var width = RootGrid.ActualWidth - margin.Left - margin.Right - SidebarSpace;
        var above = HeaderCard.ActualHeight + (ProfileError.IsVisible ? ProfileError.ActualHeight + ProfileError.Margin.Top : 0);
        var height = RootGrid.ActualHeight - margin.Top - margin.Bottom - above - StageGap;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var stageWidth = Math.Floor(Math.Min(width, height * 16 / 9));
        StageColumn.Width = new GridLength(stageWidth);
        StageRow.Height = new GridLength(Math.Floor(stageWidth * 9 / 16) + StageGap);
    }

    /// <summary>
    /// Mostra a tela de alguém na área principal. A que estava aberta antes é fechada (e <paramref name="closed"/>
    /// dela, chamado). <paramref name="closed"/> desta é chamado quando ela sai da tela.
    /// </summary>
    public void ShowStream(StreamViewerViewModel viewer, Action closed)
    {
        CloseStream();
        _watching = viewer;
        _watchingClosed = closed;
        viewer.CloseRequested += OnStreamCloseRequested;
        viewer.IsVideoPaused = WindowState == WindowState.Minimized;
        Player.DataContext = viewer;
        Player.Visibility = Visibility.Visible;
        NoStreamPanel.Visibility = Visibility.Collapsed;
    }

    private void OnStreamCloseRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (sender == _watching)
        {
            CloseStream();
        }
    });

    private void CloseStream()
    {
        if (_watching is not { } viewer)
        {
            return;
        }

        var closed = _watchingClosed;
        viewer.CloseRequested -= OnStreamCloseRequested;
        _watching = null;
        _watchingClosed = null;
        Player.ExitFullScreen();
        Player.DataContext = null;
        Player.Visibility = Visibility.Collapsed;
        NoStreamPanel.Visibility = Visibility.Visible;
        closed?.Invoke();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Foco no "Cancelar" ao abrir a confirmação: Enter não desconecta por acidente e Esc fecha.
        if (e.PropertyName == nameof(MainViewModel.IsConfirmingDisconnect) && _viewModel.IsConfirmingDisconnect)
        {
            Dispatcher.BeginInvoke(() => CancelDisconnectButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        // Depois do login no navegador, traz o aplicativo de volta para a frente.
        if (e.PropertyName == nameof(MainViewModel.IsConnected) && _viewModel.IsConnected)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }
    }
}
