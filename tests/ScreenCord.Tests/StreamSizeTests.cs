using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.Tests;

/// <summary>
/// Tamanhos de vídeo que saem de telas e janelas fora do comum (monitor vertical, ultrawide, 5:4, janelas de tamanho
/// qualquer): o encoder e o decodificador de verdade precisam aceitar a largura e a altura que não são múltiplas
/// de 16 sem distorcer nem deslocar a imagem.
/// </summary>
public class StreamSizeTests
{
    public static TheoryData<int, int, int> Telas => new()
    {
        { 1080, 1920, 1080 }, // monitor vertical 1080p
        { 1080, 1920, 720 },
        { 1080, 1920, 540 },
        { 1440, 2560, 1080 }, // monitor vertical 1440p
        { 3440, 1440, 1080 }, // ultrawide 21:9
        { 5120, 1440, 1080 }, // super ultrawide 32:9 (bate no limite de largura)
        { 1280, 1024, 720 },  // 5:4
        { 1920, 1200, 1080 }, // 16:10
        { 1366, 768, 720 },   // notebook comum
        { 1001, 777, 720 },   // janela de tamanho qualquer
        { 300, 1900, 1080 },  // janela estreita e alta
    };

    /// <summary>Codifica e decodifica nos tamanhos de saída dessas telas, com o encoder de hardware e com o do Windows.</summary>
    [Theory]
    [MemberData(nameof(Telas))]
    public void Codec_TamanhoDeTelaForaDoComum_DecodificaNoMesmoTamanhoSemDeslocar(int sourceWidth, int sourceHeight, int target)
    {
        var (width, height) = ScreenCapture.OutputSize(sourceWidth, sourceHeight, target);
        Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(width, height, 30, 2500, false), StreamLimits.Direct));

        foreach (var allowHardware in new[] { true, false })
        {
            var decoded = RoundTrip(width, height, allowHardware, out var encoderName);
            Assert.True(decoded is not null, $"{encoderName} em {width}x{height}: nenhum quadro decodificado");
            var (bgra, w, h) = decoded.Value;
            Assert.True((width, height) == (w, h), $"{encoderName}: codificado {width}x{height}, decodificado {w}x{h}");

            // Metade esquerda escura e direita clara, em cima e embaixo: um passo de linha errado entorta as faixas.
            foreach (var y in new[] { 8, h / 2, h - 8 })
            {
                var dark = Luma(bgra, w, w / 4, y);
                var bright = Luma(bgra, w, w * 3 / 4, y);
                Assert.True(dark < 90 && bright > 160, $"{encoderName} em {width}x{height}, linha {y}: esquerda {dark}, direita {bright}");
            }
        }
    }

    /// <summary>
    /// A origem muda de proporção no meio da transmissão (a tela girou para vertical, a janela foi redimensionada) e
    /// o quadro de saída, que o encoder já usa, não muda: a imagem fica centralizada com faixas pretas. Um quadro
    /// vazio (janela sendo minimizada) não derruba a conversão. Na GPU de verdade (ou no WARP, sem placa de vídeo).
    /// </summary>
    [Fact]
    public unsafe void Conversor_OrigemMudaDeProporcao_EncaixaComFaixasPretasNoMesmoQuadro()
    {
        using var device = CreateVideoDevice();
        if (device is null)
        {
            return; // sem processamento de vídeo do Direct3D nesta máquina
        }

        const int width = 1280;
        const int height = 720;
        using var converter = new Nv12Converter(device, width, height, 30);
        var nv12 = new byte[width * height * 3 / 2];

        // Tela vertical num quadro deitado: faixas pretas dos lados, imagem branca no meio.
        using (var portrait = WhiteTexture(device, 1080, 1920))
        {
            converter.Convert(portrait, 1080, 1920);
        }

        Assert.True(converter.ReadNv12(nv12));
        var (left, _, contentWidth, _) = ScreenCapture.Letterbox(1080, 1920, width, height);
        // Faixas no preto da faixa limitada (Y 16, sem cor), a mesma do resto da imagem; branco em 235.
        Assert.InRange(nv12[height / 2 * width + left / 2], 12, 20);                         // faixa da esquerda
        Assert.InRange(nv12[height / 2 * width + width / 2], 228, 240);                     // imagem
        Assert.InRange(nv12[height / 2 * width + left + contentWidth + left / 2], 12, 20);  // faixa da direita
        Assert.InRange(nv12[width * height + height / 4 * width + 2], 124, 132);            // croma da faixa

        // Quadro vazio: nada muda e nada quebra.
        using (var empty = WhiteTexture(device, 64, 64))
        {
            converter.Convert(empty, 0, 0);
        }

        // De volta à proporção do quadro: ocupa tudo.
        using (var landscape = WhiteTexture(device, 1920, 1080))
        {
            converter.Convert(landscape, 1920, 1080);
        }

        Assert.True(converter.ReadNv12(nv12));
        Assert.InRange(nv12[height / 2 * width + 4], 228, 240);
        Assert.InRange(nv12[(height - 2) * width + width - 4], 228, 240);
    }

    private static Vortice.Direct3D11.ID3D11Device? CreateVideoDevice()
    {
        foreach (var driver in new[] { Vortice.Direct3D.DriverType.Hardware, Vortice.Direct3D.DriverType.Warp })
        {
            try
            {
                var device = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    driver,
                    Vortice.Direct3D11.DeviceCreationFlags.BgraSupport | Vortice.Direct3D11.DeviceCreationFlags.VideoSupport,
                    [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0]);
                if (device.QueryInterfaceOrNull<Vortice.Direct3D11.ID3D11VideoDevice>() is { } video)
                {
                    video.Dispose();
                    return device;
                }

                device.Dispose();
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
            }
        }

        return null;
    }

    private static unsafe Vortice.Direct3D11.ID3D11Texture2D WhiteTexture(Vortice.Direct3D11.ID3D11Device device, int width, int height)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, 0xFFFFFFFF);
        fixed (uint* data = pixels)
        {
            return device.CreateTexture2D(
                new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
                    Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource),
                [new Vortice.Direct3D11.SubresourceData((IntPtr)data, (uint)width * 4)]);
        }
    }

    private static int Luma(byte[] bgra, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (bgra[offset] + bgra[offset + 1] + bgra[offset + 2]) / 3;
    }

    /// <summary>
    /// Quem compartilha troca no meio da transmissão de uma tela deitada para uma vertical (e volta): o encoder é
    /// outro, de outro tamanho, e o decodificador de quem assiste é o mesmo; ele precisa acompanhar sem travar.
    /// </summary>
    [Fact]
    public void Codec_TrocaDeTelaDeitadaParaVerticalNoMeio_ODecodificadorAcompanha()
    {
        using var decoder = new H264Decoder();
        var sizes = new[] { (1280, 720), (608, 1080), (2580, 1080), (1280, 720) };
        foreach (var (width, height) in sizes)
        {
            var decoded = RoundTrip(width, height, allowHardware: true, out var encoderName, decoder);
            Assert.True(decoded is not null, $"{encoderName}: nada decodificado depois de trocar para {width}x{height}");
            Assert.Equal((width, height), (decoded.Value.Width, decoded.Value.Height));
            Assert.InRange(Luma(decoded.Value.Bgra, width, width * 3 / 4, height - 8), 161, 255);
        }
    }

    private static (byte[] Bgra, int Width, int Height)? RoundTrip(
        int width, int height, bool allowHardware, out string encoderName, H264Decoder? sharedDecoder = null)
    {
        const int fps = 30;
        using var encoder = H264Encoder.Create(width, height, fps, 2500, allowHardware);
        using var ownDecoder = sharedDecoder is null ? new H264Decoder() : null;
        var decoder = sharedDecoder ?? ownDecoder!;
        encoderName = encoder.Name;
        var frames = new List<EncodedFrame>();
        encoder.FrameEncoded += frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
            }
        };

        var nv12 = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
        {
            nv12.AsSpan(y * width, width / 2).Fill(40);
            nv12.AsSpan(y * width + width / 2, width - width / 2).Fill(200);
        }

        nv12.AsSpan(width * height).Fill(128);
        for (var i = 0; i < 12; i++)
        {
            encoder.Encode(nv12, i * 10_000_000L / fps);
            Thread.Sleep(15);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (frames)
            {
                if (frames.Count + encoder.DroppedFrames >= 10)
                {
                    break;
                }
            }

            Thread.Sleep(20);
        }

        List<EncodedFrame> encoded;
        lock (frames)
        {
            encoded = [.. frames];
        }

        (byte[], int, int)? last = null;
        foreach (var frame in encoded)
        {
            decoder.Decode(frame.Data, frame.Timestamp, (bgra, w, h, _) => last = ([.. bgra], w, h));
        }

        return last;
    }
}
