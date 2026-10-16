using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.Tests;

public class MediaTests
{
    [Theory]
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(1920, 1080, 540, 960, 540)]
    [InlineData(1920, 1080, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(3440, 1440, 1080, 2580, 1080)] // ultrawide: largura acompanha a proporção
    [InlineData(1280, 720, 1080, 1280, 720)]   // origem menor não é ampliada
    [InlineData(801, 601, 720, 800, 600)]      // lados ímpares viram pares
    [InlineData(1000, 400, 540, 1000, 400)]
    public void TamanhoDeSaida_SegueAAlturaEscolhidaEAProporcao(int sourceWidth, int sourceHeight, int target, int width, int height)
    {
        Assert.Equal((width, height), ScreenCapture.OutputSize(sourceWidth, sourceHeight, target));
    }

    [Fact]
    public void TamanhoDeSaida_SempreAceitoPelaApi()
    {
        var available = from resolution in Enum.GetValues<ShareResolution>()
                        from frameRate in Enum.GetValues<ShareFrameRate>()
                        where ShareSettings.IsAvailable(resolution, frameRate)
                        select new ShareSettings(resolution, frameRate);
        foreach (var settings in available)
        {
            foreach (var (w, h) in new[] { (7680, 1080), (100, 3000), (5, 5), (1366, 768) })
            {
                var (width, height) = ScreenCapture.OutputSize(w, h, settings.Height);
                Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(width, height, settings.Fps, settings.BitrateKbps, false)),
                    $"{w}x{h}@{settings.Summary} → {width}x{height}");
            }
        }
    }

    [Fact]
    public unsafe void Nv12ParaBgra_UsaBt709FaixaLimitada()
    {
        // 2×2: preto, branco, cinza e vermelho puro (Y, U, V em BT.709 16–235).
        byte[] y = [16, 235, 126, 63];
        byte[] uv = [102, 240];
        var bgra = new byte[16];
        fixed (byte* yp = y)
        fixed (byte* uvp = uv)
        {
            Nv12.ToBgra(yp, uvp, 2, 2, 2, bgra);
        }

        // Os quatro pixels compartilham o mesmo U/V (amostragem 4:2:0), então só a luminância muda entre eles.
        Assert.True(bgra[2] > bgra[0], "componente vermelho domina");
        Assert.True(bgra[4 + 2] >= bgra[2], "pixel mais claro tem mais vermelho");
        Assert.All(new[] { 3, 7, 11, 15 }, i => Assert.Equal(255, bgra[i]));
    }

    [Fact]
    public void Pacote_CabecalhoIdaEVolta()
    {
        var packet = new byte[MediaContract.HeaderSize + 3];
        MediaContract.WriteHeader(packet, MediaContract.Video, MediaContract.KeyframeFlag, 123_456_789);

        Assert.True(MediaContract.TryReadHeader(packet, out var type, out var flags, out var timestamp));
        Assert.Equal(MediaContract.Video, type);
        Assert.Equal(MediaContract.KeyframeFlag, flags);
        Assert.Equal(123_456_789, timestamp);
        Assert.False(MediaContract.TryReadHeader(new byte[3], out _, out _, out _));
    }

    [Fact]
    public void EnderecoDeMidia_UsaWebSocketSeguroComHttps()
    {
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();

        Assert.Equal($"wss://api.exemplo.com/media/{group}/{user}", MediaContract.BuildUri(new Uri("https://api.exemplo.com/"), group, user).AbsoluteUri);
        Assert.Equal($"ws://localhost:5080/media/{group}/{user}", MediaContract.BuildUri(new Uri("http://localhost:5080/"), group, user).AbsoluteUri);
    }

    /// <summary>
    /// Codifica quadros sintéticos com o encoder disponível (hardware ou o do Windows), decodifica e confere
    /// tamanho, cor e o quadro-chave pedido no meio do fluxo.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Codec_IdaEVolta(bool allowHardware)
    {
        const int width = 1280;
        const int height = 720;
        const int fps = 30;
        using var encoder = H264Encoder.Create(width, height, fps, 4000, allowHardware);
        using var decoder = new H264Decoder();
        var frames = new List<EncodedFrame>();
        encoder.FrameEncoded += frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
            }
        };

        var nv12 = new byte[width * height * 3 / 2];
        nv12.AsSpan(0, width * height).Fill(126);       // cinza médio
        nv12.AsSpan(width * height).Fill(128);          // sem cor
        for (var i = 0; i < 40; i++)
        {
            if (i == 20)
            {
                encoder.RequestKeyframe();
            }

            nv12[i] = (byte)(16 + i); // muda um pouco a cada quadro
            encoder.Encode(nv12, i * 10_000_000L / fps);
            Thread.Sleep(10);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (frames)
            {
                if (frames.Count + encoder.DroppedFrames >= 38)
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

        Assert.True(encoded[0].IsKeyframe);
        Assert.True(H264.ContainsSps(encoded[0].Data));
        Assert.Contains(encoded.Skip(1), f => f.IsKeyframe);

        var decoded = 0;
        byte[]? center = null;
        foreach (var frame in encoded)
        {
            decoder.Decode(frame.Data, frame.Timestamp, (bgra, w, h, _) =>
            {
                decoded++;
                Assert.Equal((width, height), (w, h));
                var offset = (h / 2 * w + w / 2) * 4;
                center = bgra[offset..(offset + 4)];
            });
        }

        Assert.True(decoded >= encoded.Count - 2, $"decodificados {decoded} de {encoded.Count}");
        Assert.InRange(center![0], 110, 145);
        Assert.InRange(center[1], 110, 145);
        Assert.InRange(center[2], 110, 145);
    }
}
