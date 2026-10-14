using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenCord.Media;

/// <summary>
/// Captura uma tela ou janela com o Windows Graphics Capture e converte cada quadro, na GPU, para NV12
/// no tamanho de saída (com faixas pretas se a proporção mudar, ex.: a janela foi redimensionada).
/// Não é thread-safe: <see cref="Update"/> e <see cref="ReadNv12"/> devem ser chamados pela mesma thread.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ID3D11Texture2D _output;
    private readonly ID3D11Texture2D _staging;
    private readonly int _frameRate;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorOutputView? _outputView;
    private SizeInt32 _poolSize;
    private (uint Width, uint Height) _processorInput;
    private bool _hasFrame;
    private bool _disposed;

    public ScreenCapture(CaptureSource source, int targetHeight, int frameRate)
    {
        _frameRate = frameRate;
        _item = CreateItem(source);
        _item.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);

        _device = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0]);
        _context = _device.ImmediateContext;
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        _winrtDevice = CreateWinRtDevice(_device);

        (Width, Height) = OutputSize(_item.Size.Width, _item.Size.Height, targetHeight);

        _output = _device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, (uint)Width, (uint)Height, 1, 1, BindFlags.RenderTarget));
        _staging = _device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, (uint)Width, (uint)Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        _poolSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        TryDisableBorder(_session);
        _session.StartCapture();
    }

    /// <summary>A janela capturada foi fechada (ou a tela foi desconectada).</summary>
    public event EventHandler? Closed;

    public int Width { get; }

    public int Height { get; }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>
    /// Tamanho do vídeo: a altura escolhida, com a largura na proporção da origem; origens menores não são
    /// ampliadas. Os dois lados são pares, como o H.264 em 4:2:0 exige.
    /// </summary>
    public static (int Width, int Height) OutputSize(int sourceWidth, int sourceHeight, int targetHeight)
    {
        sourceWidth = Math.Max(sourceWidth, 2);
        sourceHeight = Math.Max(sourceHeight, 2);
        var height = Math.Min(targetHeight, sourceHeight);
        var width = (int)Math.Round(sourceWidth * (double)height / sourceHeight);
        if (width > Contracts.ScreenShareLimits.MaxWidth)
        {
            width = Contracts.ScreenShareLimits.MaxWidth;
            height = (int)Math.Round(sourceHeight * (double)width / sourceWidth);
        }

        static int Even(int value) => Math.Max(Contracts.ScreenShareLimits.MinSize, value & ~1);
        return (Even(width), Even(height));
    }

    /// <summary>Pega o quadro mais recente, se houver um novo, e o converte. Retorna verdadeiro se havia quadro novo.</summary>
    public bool Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Direct3D11CaptureFrame? latest = null;
        while (_framePool.TryGetNextFrame() is { } frame)
        {
            latest?.Dispose();
            latest = frame;
        }

        if (latest is null)
        {
            return false;
        }

        using (latest)
        {
            var contentSize = latest.ContentSize;
            using var texture = GetTexture(latest.Surface);
            var description = texture.Description;
            Convert(texture, (uint)Math.Min(contentSize.Width, (int)description.Width), (uint)Math.Min(contentSize.Height, (int)description.Height));

            // A janela mudou de tamanho: os próximos quadros vêm no tamanho novo.
            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                _poolSize = contentSize;
                _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
            }
        }

        _hasFrame = true;
        return true;
    }

    /// <summary>
    /// Copia o último quadro convertido para <paramref name="destination"/> em NV12 contíguo (plano Y com
    /// <see cref="Width"/> bytes por linha, seguido do plano UV intercalado). Retorna falso antes do primeiro quadro.
    /// </summary>
    public unsafe bool ReadNv12(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasFrame)
        {
            return false;
        }

        var size = Width * Height * 3 / 2;
        if (destination.Length < size)
        {
            throw new ArgumentException("Buffer pequeno demais para o quadro.", nameof(destination));
        }

        _context.CopyResource(_staging, _output);
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)mapped.DataPointer;
            var pitch = (int)mapped.RowPitch;
            for (var row = 0; row < Height; row++)
            {
                new ReadOnlySpan<byte>(source + (long)row * pitch, Width).CopyTo(destination.Slice(row * Width, Width));
            }

            // Em texturas NV12 mapeadas, o plano UV começa logo depois das Height linhas do plano Y.
            var uv = source + (long)pitch * Height;
            var uvOffset = Width * Height;
            for (var row = 0; row < Height / 2; row++)
            {
                new ReadOnlySpan<byte>(uv + (long)row * pitch, Width).CopyTo(destination.Slice(uvOffset + row * Width, Width));
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
        _framePool.Dispose();
        _outputView?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _staging.Dispose();
        _output.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private void Convert(ID3D11Texture2D texture, uint sourceWidth, uint sourceHeight)
    {
        var description = texture.Description;
        if (_processor is null || _processorInput != (description.Width, description.Height))
        {
            CreateProcessor(description.Width, description.Height);
        }

        using var inputView = _videoDevice.CreateVideoProcessorInputView(texture, _enumerator!, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

        // Encaixa a origem no quadro de saída mantendo a proporção; o resto fica preto.
        var scale = Math.Min(Width / (double)sourceWidth, Height / (double)sourceHeight);
        var destWidth = Math.Min(Width, (int)Math.Round(sourceWidth * scale) & ~1);
        var destHeight = Math.Min(Height, (int)Math.Round(sourceHeight * scale) & ~1);
        var left = (Width - destWidth) / 2 & ~1;
        var top = (Height - destHeight) / 2 & ~1;

        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new Vortice.RawRect(0, 0, (int)sourceWidth, (int)sourceHeight));
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new Vortice.RawRect(left, top, left + destWidth, top + destHeight));
        _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
    }

    private void CreateProcessor(uint inputWidth, uint inputHeight)
    {
        _outputView?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_frameRate, 1),
            InputWidth = inputWidth,
            InputHeight = inputHeight,
            OutputFrameRate = new Rational((uint)_frameRate, 1),
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            Usage = VideoUsage.OptimalSpeed,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });

        // Entrada: RGB de 0 a 255. Saída: YCbCr BT.709 de 16 a 235, o mesmo que o decodificador espera.
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        _processorInput = (inputWidth, inputHeight);
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        // Windows 11: tira a borda amarela em volta do que está sendo capturado.
        if (!ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            return;
        }

        try
        {
            _ = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
            session.IsBorderRequired = false;
        }
        catch (Exception)
        {
            // Sem permissão para tirar a borda: segue com ela.
        }
    }

    private static GraphicsCaptureItem CreateItem(CaptureSource source)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopIid, out var interop));
        try
        {
            var itemPointer = IntPtr.Zero;
            int hr;
            unsafe
            {
                // IGraphicsCaptureItemInterop: [3] CreateForWindow, [4] CreateForMonitor.
                var vtable = *(IntPtr**)interop;
                var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[source.IsScreen ? 4 : 3];
                hr = create(interop, source.Handle, &itemIid, &itemPointer);
            }

            Marshal.ThrowExceptionForHR(hr);
            try
            {
                return GraphicsCaptureItem.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pointer));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var accessIid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        var textureIid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        var surfacePointer = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, in accessIid, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess: [3] GetInterface.
                var vtable = *(IntPtr**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
                IntPtr texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfacePointer);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
