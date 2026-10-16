using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenCord.Media;

/// <summary>
/// Converte texturas BGRA, na GPU, para NV12 no tamanho de saída (com faixas pretas se a proporção mudar,
/// ex.: a janela foi redimensionada) e as copia para a memória. Não é thread-safe.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11Texture2D _output;
    private readonly ID3D11Texture2D _staging;
    private readonly int _frameRate;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorOutputView? _outputView;
    private (uint Width, uint Height) _processorInput;
    private bool _hasFrame;

    public Nv12Converter(ID3D11Device device, int width, int height, int frameRate)
    {
        Width = width;
        Height = height;
        _frameRate = frameRate;
        _context = device.ImmediateContext;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        _output = device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
        _staging = device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Converte a área (0, 0, <paramref name="sourceWidth"/>, <paramref name="sourceHeight"/>) de <paramref name="texture"/>.</summary>
    public void Convert(ID3D11Texture2D texture, uint sourceWidth, uint sourceHeight)
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
        _hasFrame = true;
    }

    /// <summary>
    /// Copia o último quadro convertido para <paramref name="destination"/> em NV12 contíguo (plano Y com
    /// <see cref="Width"/> bytes por linha, seguido do plano UV intercalado). Retorna falso antes do primeiro quadro.
    /// </summary>
    public unsafe bool ReadNv12(Span<byte> destination)
    {
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
        _outputView?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _staging.Dispose();
        _output.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _context.Dispose();
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
}
