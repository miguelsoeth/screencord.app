using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenCord.Media;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

/// <summary>Prévia da câmera antes de ligá-la: Cancelar fecha sem transmitir, Ativar câmera confirma.</summary>
public partial class CameraPreviewWindow : Window
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;

    private readonly CameraPreviewViewModel _viewModel;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    // Plugar um dispositivo gera uma rajada de avisos: procura uma vez só, depois que ela passa.
    private readonly DispatcherTimer _deviceChange = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private WriteableBitmap? _bitmap;
    private byte[]? _bgra;

    public CameraPreviewWindow(CameraPreviewViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _timer.Tick += (_, _) => DrawFrame();
        _deviceChange.Tick += async (_, _) =>
        {
            _deviceChange.Stop();
            await _viewModel.LoadAsync();
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await _viewModel.LoadAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _deviceChange.Stop();
        };
    }

    public ShareRequest? Result { get; private set; }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDeviceChange && wParam == DbtDevNodesChanged)
        {
            _deviceChange.Stop();
            _deviceChange.Start();
        }

        return IntPtr.Zero;
    }

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
