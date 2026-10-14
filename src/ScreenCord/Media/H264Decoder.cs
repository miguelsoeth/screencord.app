using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ScreenCord.Media;

/// <summary>
/// Decodificador H.264 do Windows (Media Foundation, síncrono) com saída em BGRA para exibição. O tamanho
/// do vídeo vem do próprio fluxo, então muda sozinho se quem compartilha trocar de configuração.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private const int MftEnumFlagSyncMft = 0x1;
    private const int MftEnumFlagSortAndFilter = 0x40;
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private int _frameWidth;
    private int _frameHeight;
    private int _displayWidth;
    private int _displayHeight;
    private bool _hasOutputType;
    private bool _disposed;

    public H264Decoder()
    {
        MediaFoundation.EnsureStarted();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, MftEnumFlagSyncMft | MftEnumFlagSortAndFilter, input, null);
        _activate = MediaFoundation.Keep(activates.FirstOrDefault() ?? throw new InvalidOperationException("Decodificador H.264 do Windows não encontrado."));
        _transform = _activate.ActivateObject<IMFTransform>();

        try
        {
            using var attributes = _transform.Attributes;
            attributes.Set(LowLatency, 1u);
        }
        catch (SharpGenException)
        {
        }

        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            _transform.SetInputType(0, type, 0);
        }

        SetOutputType();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>Envia um quadro e entrega cada quadro decodificado (BGRA, largura × altura × 4) a <paramref name="onFrame"/>.</summary>
    /// <remarks>O buffer passado a <paramref name="onFrame"/> só vale durante a chamada.</remarks>
    public void Decode(ReadOnlySpan<byte> h264, long timestamp, DecodedFrameHandler onFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (var sample = MediaFoundation.CreateSample(h264, timestamp, 0))
        {
            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
            {
                // Saída pendente: drena e tenta de novo.
                Drain(onFrame);
                _transform.ProcessInput(0, sample, 0);
            }
        }

        Drain(onFrame);
    }

    /// <summary>Descarta o que estiver no decodificador (ex.: pacotes foram perdidos; o próximo quadro-chave recomeça).</summary>
    public void Flush() => _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            _activate.ShutdownObject();
        }
        catch (SharpGenException)
        {
        }

        _transform.Dispose();
        _activate.Dispose();
    }

    private byte[] _bgra = [];

    private void Drain(DecodedFrameHandler onFrame)
    {
        while (true)
        {
            var info = _transform.GetOutputStreamInfo(0);
            var providesSamples = (info.Flags & 0x300) != 0;
            var buffer = new OutputDataBuffer { StreamID = 0 };
            if (!providesSamples)
            {
                buffer.Sample = MediaFoundation.CreateEmptySample(Math.Max(info.Size, 1), info.Alignment);
            }

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            try
            {
                if (result == ResultCode.TransformNeedMoreInput)
                {
                    return;
                }

                if (result == ResultCode.TransformStreamChange || !_hasOutputType)
                {
                    SetOutputType();
                    continue;
                }

                result.CheckError();
                if (buffer.Sample is { } sample && _frameWidth > 0)
                {
                    Convert(sample);
                    onFrame(_bgra, _displayWidth, _displayHeight, sample.SampleTime);
                }
            }
            finally
            {
                buffer.Sample?.Dispose();
                buffer.Events?.Dispose();
            }
        }
    }

    private void SetOutputType()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException)
            {
                // Antes do primeiro SPS o decodificador ainda não sabe o tamanho; tenta de novo depois.
                _hasOutputType = false;
                return;
            }

            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                {
                    continue;
                }

                _transform.SetOutputType(0, type, 0);
                MediaFactory.UnpackSize(type.GetUInt64(MediaTypeAttributeKeys.FrameSize), out var width, out var height);
                _frameWidth = (int)width;
                _frameHeight = (int)height;
                _displayWidth = _frameWidth;
                _displayHeight = _frameHeight;

                // 1080p é codificado com 1088 linhas; a área visível vem na abertura mínima.
                try
                {
                    var aperture = type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
                    if (aperture.Length >= 16)
                    {
                        var cx = BitConverter.ToInt32(aperture, 8);
                        var cy = BitConverter.ToInt32(aperture, 12);
                        if (cx > 0 && cy > 0 && cx <= _frameWidth && cy <= _frameHeight)
                        {
                            _displayWidth = cx;
                            _displayHeight = cy;
                        }
                    }
                }
                catch (SharpGenException)
                {
                }

                _hasOutputType = true;
                return;
            }
        }
    }

    private unsafe void Convert(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        var size = _displayWidth * _displayHeight * 4;
        if (_bgra.Length != size)
        {
            _bgra = new byte[size];
        }

        IMF2DBuffer? buffer2d = null;
        try
        {
            buffer2d = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        }
        catch (SharpGenException)
        {
        }

        if (buffer2d is not null)
        {
            using (buffer2d)
            {
                buffer2d.Lock2D(out var scanline0, out var pitch);
                try
                {
                    var y = (byte*)scanline0;
                    Nv12.ToBgra(y, y + (long)pitch * _frameHeight, pitch, _displayWidth, _displayHeight, _bgra);
                }
                finally
                {
                    buffer2d.Unlock2D();
                }
            }

            return;
        }

        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            var stride = _frameWidth;
            if (length < stride * _frameHeight * 3 / 2)
            {
                return;
            }

            var y = (byte*)pointer;
            Nv12.ToBgra(y, y + (long)stride * _frameHeight, stride, _displayWidth, _displayHeight, _bgra);
        }
        finally
        {
            buffer.Unlock();
        }
    }
}

public delegate void DecodedFrameHandler(byte[] bgra, int width, int height, long timestamp);

/// <summary>Conversão NV12 (BT.709, 16–235) → BGRA, a mesma faixa de cor usada na captura.</summary>
public static class Nv12
{
    public static unsafe void ToBgra(byte* yPlane, byte* uvPlane, int pitch, int width, int height, byte[] destination)
    {
        fixed (byte* output = destination)
        {
            var dst = output;
            var ySource = yPlane;
            var uvSource = uvPlane;
            Parallel.For(0, height / 2, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, pair =>
            {
                for (var half = 0; half < 2; half++)
                {
                    var row = pair * 2 + half;
                    var yRow = ySource + (long)row * pitch;
                    var uvRow = uvSource + (long)pair * pitch;
                    var outRow = dst + (long)row * width * 4;
                    ConvertRow(yRow, uvRow, outRow, width);
                }
            });

            if (height % 2 == 1)
            {
                var row = height - 1;
                ConvertRow(ySource + (long)row * pitch, uvSource + (long)(row / 2) * pitch, dst + (long)row * width * 4, width);
            }
        }
    }

    private static unsafe void ConvertRow(byte* yRow, byte* uvRow, byte* outRow, int width)
    {
        // Coeficientes BT.709 em ponto fixo (× 1024).
        for (var x = 0; x < width; x += 2)
        {
            var u = uvRow[x] - 128;
            var v = uvRow[x + 1] - 128;
            var rAdd = 1836 * v;
            var gAdd = -218 * u - 546 * v;
            var bAdd = 2163 * u;

            for (var i = 0; i < 2 && x + i < width; i++)
            {
                var c = (yRow[x + i] - 16) * 1192;
                var o = outRow + (x + i) * 4;
                o[0] = Clamp((c + bAdd + 512) >> 10);
                o[1] = Clamp((c + gAdd + 512) >> 10);
                o[2] = Clamp((c + rAdd + 512) >> 10);
                o[3] = 255;
            }
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
