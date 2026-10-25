using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenCord.Media;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

/// <summary>Prévia da câmera antes de ligá-la: Cancelar fecha sem transmitir, Ativar câmera confirma.</summary>
public partial class CameraPreviewWindow : Window
{
    private readonly CameraPreviewViewModel _viewModel;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private WriteableBitmap? _bitmap;
    private byte[]? _bgra;

    public CameraPreviewWindow(CameraPreviewViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _timer.Tick += (_, _) => DrawFrame();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await _viewModel.LoadAsync();
        };
        Closed += (_, _) => _timer.Stop();
    }

    public ShareRequest? Result { get; private set; }

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.BuildRequest();
        if (Result is not null)
        {
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void DrawFrame()
    {
        if (!_viewModel.TryReadFrame(ref _bgra, out var width, out var height))
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            Preview.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _bgra!, width * 4, 0);
    }
}
