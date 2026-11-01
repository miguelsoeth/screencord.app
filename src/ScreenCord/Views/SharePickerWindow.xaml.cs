using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenCord.Media;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

public partial class SharePickerWindow : Window
{
    private readonly SharePickerViewModel _viewModel;

    // O Windows não avisa de forma simples quando uma janela que serve para compartilhar abre ou fecha: a lista se confere.
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _isRefreshing;

    public SharePickerWindow(SharePickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _refresh.Tick += OnRefreshTick;
        Loaded += (_, _) =>
        {
            SourceList.Focus();
            _refresh.Start();
        };
        Closed += (_, _) => _refresh.Stop();
    }

    public ShareRequest? Result { get; private set; }

    private async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            await _viewModel.RefreshAsync();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnShareClick(object sender, RoutedEventArgs e) => Confirm();

    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSourceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanShare)
        {
            Confirm();
        }
    }

    private void Confirm()
    {
        Result = _viewModel.BuildRequest();
        if (Result is not null)
        {
            DialogResult = true;
        }
    }
}
