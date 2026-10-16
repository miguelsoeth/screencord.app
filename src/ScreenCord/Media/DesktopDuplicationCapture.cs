using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenCord.Media;

/// <summary>
/// Captura uma tela com o Desktop Duplication do DXGI, que, ao contrário do Windows Graphics Capture no
/// Windows 10, não desenha borda amarela. O cursor não vem na imagem: é desenhado por cima com GDI.
/// Telas giradas não são suportadas (a imagem vem na orientação física do monitor).
/// Não é thread-safe: <see cref="Update"/> e <see cref="ReadNv12"/> devem ser chamados pela mesma thread.
/// </summary>
public sealed class DesktopDuplicationCapture : IScreenCapture
{
    // Tempo entre tentativas de retomar a captura depois de perdê-la (ex.: tela do UAC, mudança de resolução).
    private const long RetryMilliseconds = 250;

    private readonly IntPtr _monitor;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutput1 _output;
    private readonly Vortice.RawRect _bounds;
    private readonly Nv12Converter _converter;

    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _desktop;
    private ID3D11Texture2D? _composed;
    private long _nextRetry;
    private Cursor _cursor;
    private (IntPtr Handle, int X, int Y) _hotspot;
    private bool _closed;
    private bool _disposed;

    public DesktopDuplicationCapture(IntPtr monitor, int targetHeight, int frameRate)
    {
        _monitor = monitor;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var (adapter, output) = FindOutput(factory, monitor);
        try
        {
            var description = output.Description;
            if (description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            {
                throw new NotSupportedException("A tela está girada.");
            }

            _bounds = description.DesktopCoordinates;
            _output = output.QueryInterface<IDXGIOutput1>();

            // A duplicação exige um dispositivo na mesma placa de vídeo que a tela.
            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out var device).CheckError();
            _device = device!;
        }
        finally
        {
            output.Dispose();
            adapter.Dispose();
        }

        _context = _device.ImmediateContext;
        try
        {
            var (width, height) = ScreenCapture.OutputSize(_bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top, targetHeight);
            _converter = new Nv12Converter(_device, width, height, frameRate);
            _duplication = _output.DuplicateOutput(_device);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public event EventHandler? Closed;

    public string Name => "Desktop Duplication";

    public int Width => _converter.Width;

    public int Height => _converter.Height;

    public bool Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closed)
        {
            return false;
        }

        var imageChanged = AcquireFrame();
        var cursorChanged = UpdateCursor();
        if (_desktop is null || _composed is null || !imageChanged && !cursorChanged)
        {
            return false;
        }

        _context.CopyResource(_composed, _desktop);
        DrawCursor(_composed);
        var description = _composed.Description;
        _converter.Convert(_composed, description.Width, description.Height);
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
        _duplication?.Dispose();
        _composed?.Dispose();
        _desktop?.Dispose();
        _converter?.Dispose();
        _context?.Dispose();
        _output?.Dispose();
        _device?.Dispose();
    }

    /// <summary>Copia a imagem nova da tela, se houver, para <see cref="_desktop"/>.</summary>
    private bool AcquireFrame()
    {
        if (_duplication is null && !TryResume())
        {
            return false;
        }

        var result = _duplication!.AcquireNextFrame(0, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return false;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost || result == Result.AccessDenied)
        {
            // Mudou a resolução, entrou um jogo em tela cheia ou a área de trabalho segura (UAC): recria a duplicação.
            _duplication.Dispose();
            _duplication = null;
            return false;
        }

        result.CheckError();
        try
        {
            // Sem imagem nova: só o mouse mexeu (o cursor é tratado à parte).
            if (info.LastPresentTime == 0)
            {
                return false;
            }

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureTextures(texture.Description);
            _context.CopyResource(_desktop!, texture);
            return true;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    private bool TryResume()
    {
        var now = Environment.TickCount64;
        if (now < _nextRetry)
        {
            return false;
        }

        _nextRetry = now + RetryMilliseconds;
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(_monitor, ref info))
        {
            _closed = true;
            Closed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        try
        {
            _duplication = _output.DuplicateOutput(_device);
            return true;
        }
        catch (SharpGenException)
        {
            // Ainda na área de trabalho segura ou no meio da troca de modo de vídeo: tenta de novo depois.
            return false;
        }
    }

    private void EnsureTextures(Texture2DDescription source)
    {
        if (_desktop is { } current && current.Description.Width == source.Width && current.Description.Height == source.Height)
        {
            return;
        }

        _desktop?.Dispose();
        _composed?.Dispose();
        _desktop = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, source.Width, source.Height, 1, 1));

        // Compatível com GDI para desenhar o cursor com DrawIconEx (que trata cursores invertidos, como o de texto).
        _composed = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, source.Width, source.Height, 1, 1, BindFlags.RenderTarget, miscFlags: ResourceOptionFlags.GdiCompatible));
    }

    /// <summary>Lê o cursor; retorna verdadeiro se ele mudou de forma, posição ou visibilidade sobre esta tela.</summary>
    private bool UpdateCursor()
    {
        var info = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };

        // Em coordenadas físicas, as mesmas do DXGI, independentemente da escala de cada tela.
        var previous = Native.SetThreadDpiAwarenessContext(Native.DpiAwarenessPerMonitorV2);
        bool read;
        try
        {
            read = Native.GetCursorInfo(ref info);
        }
        finally
        {
            Native.SetThreadDpiAwarenessContext(previous);
        }

        var visible = read
            && (info.Flags & Native.CursorShowing) != 0
            && info.X >= _bounds.Left && info.X < _bounds.Right && info.Y >= _bounds.Top && info.Y < _bounds.Bottom;
        var cursor = visible ? new Cursor(info.Handle, info.X - _bounds.Left, info.Y - _bounds.Top) : default;
        if (cursor == _cursor)
        {
            return false;
        }

        _cursor = cursor;
        return true;
    }

    private void DrawCursor(ID3D11Texture2D target)
    {
        if (_cursor.Handle == IntPtr.Zero)
        {
            return;
        }

        if (_hotspot.Handle != _cursor.Handle)
        {
            if (!Native.GetIconInfo(_cursor.Handle, out var icon))
            {
                return;
            }

            Native.DeleteObject(icon.Mask);
            Native.DeleteObject(icon.Color);
            _hotspot = (_cursor.Handle, icon.HotspotX, icon.HotspotY);
        }

        using var surface = target.QueryInterface<IDXGISurface1>();
        var dc = surface.GetDC(false);
        try
        {
            Native.DrawIconEx(dc, _cursor.X - _hotspot.X, _cursor.Y - _hotspot.Y, _cursor.Handle, 0, 0, 0, IntPtr.Zero, Native.DiNormal);
        }
        finally
        {
            surface.ReleaseDC(null);
        }
    }

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output) FindOutput(IDXGIFactory1 factory, IntPtr monitor)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (output.Description.Monitor == monitor)
                {
                    return (adapter, output);
                }

                output.Dispose();
            }

            adapter.Dispose();
        }

        throw new NotSupportedException("A tela não foi encontrada no DXGI.");
    }

    private readonly record struct Cursor(IntPtr Handle, int X, int Y);

    private static class Native
    {
        public const int CursorShowing = 1;
        public const int DiNormal = 3;
        public static readonly IntPtr DpiAwarenessPerMonitorV2 = -4;

        [StructLayout(LayoutKind.Sequential)]
        public struct CursorInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Handle;
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IconInfo
        {
            public bool IsIcon;
            public int HotspotX;
            public int HotspotY;
            public IntPtr Mask;
            public IntPtr Color;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public Vortice.RawRect Monitor;
            public Vortice.RawRect Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(ref CursorInfo info);

        [DllImport("user32.dll")]
        public static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);
    }
}
