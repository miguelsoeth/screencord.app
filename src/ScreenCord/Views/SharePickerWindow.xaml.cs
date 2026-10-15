using System.Windows;
using System.Windows.Input;
using ScreenCord.Media;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

public partial class SharePickerWindow : Window
{
    private readonly SharePickerViewModel _viewModel;

    public SharePickerWindow(SharePickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => SourceList.Focus();
    }

    public ShareRequest? Result { get; private set; }

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
