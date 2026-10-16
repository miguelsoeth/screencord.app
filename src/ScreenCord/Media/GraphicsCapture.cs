using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenCord.Media;

/// <summary>
/// Captura uma tela ou janela com o Windows Graphics Capture. No Windows 10 ele desenha uma borda amarela
/// em volta do que está sendo capturado, que só dá para tirar a partir do Windows 11 (<see cref="CanHideBorder"/>).
/// Não é thread-safe: <see cref="Update"/> e <see cref="ReadNv12"/> devem ser chamados pela mesma thread.
/// </summary>
public sealed class GraphicsCapture : IScreenCapture
{
    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly Nv12Converter _converter;

    private SizeInt32 _poolSize;
    private bool _disposed;

    public GraphicsCapture(CaptureSource source, int targetHeight, int frameRate)
    {
        _item = CreateItem(source);
        _item.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);

        _device = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0]);
        _winrtDevice = CreateWinRtDevice(_device);

        var (width, height) = ScreenCapture.OutputSize(_item.Size.Width, _item.Size.Height, targetHeight);
        _converter = new Nv12Converter(_device, width, height, frameRate);

        _poolSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        TryDisableBorder(_session);
        _session.StartCapture();
    }

    public event EventHandler? Closed;

    public string Name => "Windows Graphics Capture";

    public int Width => _converter.Width;

    public int Height => _converter.Height;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>O Windows permite tirar a borda amarela (Windows 11).</summary>
    public static bool CanHideBorder =>
        ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired));

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
            _converter.Convert(texture, (uint)Math.Min(contentSize.Width, (int)description.Width), (uint)Math.Min(contentSize.Height, (int)description.Height));

            // A janela mudou de tamanho: os próximos quadros vêm no tamanho novo.
            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                _poolSize = contentSize;
                _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
            }
        }

        return true;
    }

    public bool ReadNv12(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _converter.ReadNv12(destination);
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
        _converter.Dispose();
        _device.Dispose();
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        // Windows 11: tira a borda amarela em volta do que está sendo capturado.
        if (!CanHideBorder)
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
