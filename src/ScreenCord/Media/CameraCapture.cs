using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ScreenCord.Media;

/// <summary>Um modo que a câmera sabe entregar (resolução, fps e formato).</summary>
public readonly record struct CameraMode(int Index, int Width, int Height, double FrameRate, bool IsCompressed);

/// <summary>
/// Captura de uma câmera (webcam) com o leitor de fontes do Media Foundation, que entrega NV12 mesmo quando a
/// câmera só fala MJPEG ou YUY2. Uma thread própria lê os quadros e guarda o mais recente: <see cref="Update"/> e
/// <see cref="ReadNv12"/> só olham para ele, como nas outras capturas. Câmeras não ampliam a imagem: o vídeo sai no
/// maior modo da câmera que cabe na altura pedida (se todos forem maiores, no menor, reduzido).
/// </summary>
public sealed class CameraCapture : IScreenCapture
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Quantos modos tentar antes de desistir da câmera (algumas listam dezenas).</summary>
    private const int MaxModeAttempts = 8;

    private readonly IMFMediaSource _source;
    private readonly IMFSourceReader _reader;
    private readonly Thread _thread;
    private readonly Lock _lock = new();
    private readonly byte[] _latest;
    private readonly int _nativeWidth;
    private readonly int _nativeHeight;
    private readonly int _stride;
    private readonly ILogger? _logger;
    private volatile bool _stopping;
    private bool _hasFrame;
    private bool _fresh;
    private bool _disposed;
    private int _closed;

    public CameraCapture(string symbolicLink, int targetHeight, int frameRate, ILogger? logger = null)
    {
        _logger = logger;
        MediaFoundation.EnsureStarted();

        using (var attributes = MediaFactory.MFCreateAttributes(2))
        {
            attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            attributes.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, symbolicLink);
            _source = MediaFactory.MFCreateDeviceSource(attributes);
        }

        try
        {
            using var readerAttributes = MediaFactory.MFCreateAttributes(2);
            // O processamento avançado usa o Video Processor MFT, que converte YUY2 e MJPEG para NV12 (o simples só chega a RGB).
            readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
            _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(_source, readerAttributes);
        }
        catch
        {
            ReleaseQuietly(null, _source);
            throw;
        }

        try
        {
            var modes = ListModes(_reader);
            if (modes.Count == 0)
            {
                throw new InvalidOperationException("A câmera não informou nenhum modo de vídeo.");
            }

            // O melhor modo primeiro; câmeras virtuais e alguns drivers recusam o formato e aceitam outro.
            var mode = default(CameraMode);
            var chosen = false;
            SharpGenException? failure = null;
            foreach (var candidate in Candidates(modes, targetHeight, frameRate).Take(MaxModeAttempts))
            {
                try
                {
                    SelectMode(_reader, candidate);
                    mode = candidate;
                    chosen = true;
                    break;
                }
                catch (SharpGenException ex)
                {
                    failure = ex;
                    _logger?.LogDebug("Modo {Width}x{Height} a {Fps:0} fps da câmera recusado ({Message}).", candidate.Width, candidate.Height, candidate.FrameRate, ex.Message);
                }
            }

            if (!chosen)
            {
                throw new InvalidOperationException("Nenhum modo de vídeo da câmera pôde ser convertido para NV12.", failure);
            }

            Name = "Câmera (Media Foundation)";
            _nativeWidth = mode.Width;
            _nativeHeight = mode.Height;
            _stride = ReadStride(_reader, mode.Width);
            (Width, Height) = ScreenCapture.OutputSize(mode.Width, mode.Height, targetHeight);
            _latest = new byte[Width * Height * 3 / 2];
            _reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
        }
        catch
        {
            ReleaseQuietly(_reader, _source);
            throw;
        }

        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "Câmera" };
        _thread.Start();
    }

    public event EventHandler? Closed;

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Nome e identificador (link simbólico) das câmeras conectadas, pelo Media Foundation.</summary>
    public static IReadOnlyList<(string Name, string SymbolicLink)> Enumerate()
    {
        MediaFoundation.EnsureStarted();
        var cameras = new List<(string, string)>();
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);

        Marshal.ThrowExceptionForHR(MFEnumDeviceSources(attributes.NativePointer, out var array, out var count));
        try
        {
            for (var i = 0; i < count; i++)
            {
                using var activate = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                Marshal.AddRef(activate.NativePointer); // a matriz é liberada abaixo; cada item guarda a sua referência
                try
                {
                    var link = activate.GetString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink);
                    var name = activate.GetString(CaptureDeviceAttributeKeys.FriendlyName);
                    cameras.Add((name, link));
                }
                catch (SharpGenException)
                {
                    // Câmera que não informa nome ou identificador não dá para escolher.
                }
            }
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                Marshal.Release(Marshal.ReadIntPtr(array, i * IntPtr.Size));
            }

            Marshal.FreeCoTaskMem(array);
        }

        return cameras;
    }

    /// <summary>
    /// Escolhe o modo: o de maior altura que não passa de <paramref name="targetHeight"/> (ou o menor, se todos
    /// passarem). Entre os dessa altura, o que entrega ao menos <paramref name="frameRate"/> fps com menos
    /// sobra (senão o mais rápido), preferindo o que não é comprimido.
    /// </summary>
    public static CameraMode? ChooseMode(IReadOnlyList<CameraMode> modes, int targetHeight, int frameRate)
    {
        if (modes.Count == 0)
        {
            return null;
        }

        var fits = modes.Where(m => m.Height <= targetHeight).ToList();
        var height = fits.Count > 0 ? fits.Max(m => m.Height) : modes.Min(m => m.Height);
        var sameHeight = modes.Where(m => m.Height == height).ToList();
        var fastEnough = sameHeight.Where(m => m.FrameRate >= frameRate - 1).ToList();
        return fastEnough.Count > 0
            ? fastEnough.OrderBy(m => m.FrameRate).ThenBy(m => m.IsCompressed).ThenByDescending(m => m.Width).First()
            : sameHeight.OrderByDescending(m => m.FrameRate).ThenBy(m => m.IsCompressed).ThenByDescending(m => m.Width).First();
    }

    /// <summary>Os modos em ordem de preferência: o de <see cref="ChooseMode"/> e depois os outros, dos que cabem na altura (maiores primeiro) aos que passam dela.</summary>
    public static IEnumerable<CameraMode> Candidates(IReadOnlyList<CameraMode> modes, int targetHeight, int frameRate)
    {
        if (ChooseMode(modes, targetHeight, frameRate) is not { } best)
        {
            yield break;
        }

        yield return best;
        foreach (var mode in modes
                     .Where(m => m != best)
                     .OrderBy(m => m.Height > targetHeight)
                     .ThenBy(m => m.Height > targetHeight ? m.Height : -m.Height)
                     .ThenBy(m => m.FrameRate < frameRate - 1)
                     .ThenBy(m => m.IsCompressed))
        {
            yield return mode;
        }
    }

    /// <summary>Põe a câmera no modo e pede NV12 no mesmo tamanho; o leitor põe o decodificador ou o conversor de cor no meio.</summary>
    private static void SelectMode(IMFSourceReader reader, CameraMode mode)
    {
        const int stream = (int)SourceReaderIndex.FirstVideoStream;
        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        output.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)mode.Width, (uint)mode.Height));
        try
        {
            using (var native = reader.GetNativeMediaType(stream, mode.Index))
            {
                reader.SetCurrentMediaType(stream, native);
            }

            reader.SetCurrentMediaType(stream, output);
        }
        catch (SharpGenException)
        {
            // Alguns drivers só aceitam o pedido direto em NV12, deixando o leitor escolher o modo da câmera pelo tamanho.
            reader.SetCurrentMediaType(stream, output);
        }
    }

    public bool Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            var fresh = _fresh;
            _fresh = false;
            return fresh;
        }
    }

    public bool ReadNv12(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            if (!_hasFrame)
            {
                return false;
            }

            _latest.CopyTo(destination);
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        try
        {
            _reader.Flush((int)SourceReaderIndex.AllStreams);
        }
        catch (SharpGenException)
        {
        }

        // Um ReadSample preso (câmera usada por outro programa) não pode travar quem fecha: a thread é de fundo.
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            ReleaseQuietly(_reader, _source);
        }
    }

    /// <summary>Solta o leitor e a fonte sem esconder o erro que levou até aqui (o leitor já desliga a fonte ao ser solto).</summary>
    private static void ReleaseQuietly(IMFSourceReader? reader, IMFMediaSource source)
    {
        try
        {
            reader?.Dispose();
            source.Shutdown();
        }
        catch (SharpGenException)
        {
        }

        source.Dispose();
    }

    private static List<CameraMode> ListModes(IMFSourceReader reader)
    {
        var modes = new List<CameraMode>();
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = reader.GetNativeMediaType((int)SourceReaderIndex.FirstVideoStream, index);
            }
            catch (SharpGenException)
            {
                break; // MF_E_NO_MORE_TYPES
            }

            using (type)
            {
                try
                {
                    var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    var rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
                    var subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
                    var width = (int)(size >> 32);
                    var height = (int)(size & 0xFFFFFFFF);
                    var denominator = (uint)(rate & 0xFFFFFFFF);
                    if (width >= 2 && height >= 2 && denominator != 0)
                    {
                        var compressed = !IsRawYuv(subtype);
                        modes.Add(new CameraMode(index, width, height, (double)(rate >> 32) / denominator, compressed));
                    }
                }
                catch (SharpGenException)
                {
                    // Modo sem tamanho ou taxa: não serve.
                }
            }
        }

        return modes;
    }

    /// <summary>
    /// Formatos sem compressão em YUV (NV12, YUY2, UYVY, I420, YV12): o subtipo do Media Foundation é o FourCC no
    /// começo do GUID. Os outros (MJPEG, H.264, RGB) dão mais trabalho ao converter.
    /// </summary>
    private static bool IsRawYuv(Guid subtype)
    {
        var fourCc = BitConverter.ToUInt32(subtype.ToByteArray(), 0);
        return fourCc is 0x3231564E or 0x32595559 or 0x59565955 or 0x30323449 or 0x32315659;
    }

    [DllImport("mf.dll")]
    private static extern int MFEnumDeviceSources(IntPtr attributes, out IntPtr devices, out uint count);

    private static int ReadStride(IMFSourceReader reader, int width)
    {
        try
        {
            using var current = reader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream);
            var stride = (int)current.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
            return stride >= width ? stride : width;
        }
        catch (SharpGenException)
        {
            return width;
        }
    }

    private void ReadLoop()
    {
        var frame = new byte[_nativeWidth * _nativeHeight * 3 / 2];
        var lastFrame = Environment.TickCount64;
        try
        {
            while (!_stopping)
            {
                using var sample = _reader.ReadSample(
                    (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
                if ((flags & (SourceReaderFlag.Error | SourceReaderFlag.EndOfStream)) != 0)
                {
                    RaiseClosed();
                    return;
                }

                if (sample is null)
                {
                    // Sem quadro agora (câmera ocupada ou tela de bloqueio); desiste se passar tempo demais.
                    if (Environment.TickCount64 - lastFrame > StallTimeout.TotalMilliseconds)
                    {
                        RaiseClosed();
                        return;
                    }

                    continue;
                }

                lastFrame = Environment.TickCount64;
                CopyFrame(sample, frame);
                lock (_lock)
                {
                    if (_stopping)
                    {
                        return;
                    }

                    Resize(frame, _latest);
                    _hasFrame = true;
                    _fresh = true;
                }
            }
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_stopping)
            {
                _logger?.LogWarning("A câmera parou de entregar quadros ({ExceptionType}: {Message}).", ex.GetType().Name, ex.Message);
                RaiseClosed();
            }
        }
    }

    /// <summary>Copia o quadro do leitor para <paramref name="destination"/> em NV12 contíguo no tamanho do modo (sem sobra de linha).</summary>
    private unsafe void CopyFrame(IMFSample sample, byte[] destination)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            var source = (byte*)pointer;
            var needed = _stride * _nativeHeight * 3 / 2;
            if (length < needed)
            {
                throw new InvalidOperationException($"Quadro da câmera menor que o esperado ({length} < {needed} bytes).");
            }

            for (var row = 0; row < _nativeHeight; row++)
            {
                new ReadOnlySpan<byte>(source + (long)row * _stride, _nativeWidth).CopyTo(destination.AsSpan(row * _nativeWidth, _nativeWidth));
            }

            var uv = source + (long)_stride * _nativeHeight;
            var uvOffset = _nativeWidth * _nativeHeight;
            for (var row = 0; row < _nativeHeight / 2; row++)
            {
                new ReadOnlySpan<byte>(uv + (long)row * _stride, _nativeWidth).CopyTo(destination.AsSpan(uvOffset + row * _nativeWidth, _nativeWidth));
            }
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>Do modo da câmera para o tamanho de saída: cópia direta quando é o mesmo, senão uma redução simples.</summary>
    private void Resize(byte[] source, byte[] destination)
    {
        if (Width == _nativeWidth && Height == _nativeHeight)
        {
            source.CopyTo(destination, 0);
            return;
        }

        ScaleNv12(source, _nativeWidth, _nativeHeight, destination, Width, Height);
    }

    /// <summary>Redução por vizinho mais próximo; só entra quando a menor resolução da câmera passa da altura pedida.</summary>
    public static void ScaleNv12(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, Span<byte> destination, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var sourceRow = Math.Min(sourceHeight - 1, y * sourceHeight / height) * sourceWidth;
            for (var x = 0; x < width; x++)
            {
                destination[y * width + x] = source[sourceRow + Math.Min(sourceWidth - 1, x * sourceWidth / width)];
            }
        }

        var sourceUv = sourceWidth * sourceHeight;
        var destinationUv = width * height;
        for (var y = 0; y < height / 2; y++)
        {
            var sourceRow = sourceUv + Math.Min(sourceHeight / 2 - 1, y * sourceHeight / height) * sourceWidth;
            for (var x = 0; x < width / 2; x++)
            {
                // Cada par U/V anda junto.
                var sx = Math.Min(sourceWidth / 2 - 1, x * sourceWidth / width) * 2;
                destination[destinationUv + y * width + x * 2] = source[sourceRow + sx];
                destination[destinationUv + y * width + x * 2 + 1] = source[sourceRow + sx + 1];
            }
        }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0 && !_stopping)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
