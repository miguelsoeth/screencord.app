using System.ComponentModel;
using System.Windows;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    // Depois do login no navegador, traz o aplicativo de volta para a frente.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
