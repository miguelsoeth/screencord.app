using ScreenCord.Contracts;
using ScreenCord.Media;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;
using ScreenCord.Views;

namespace ScreenCord.Tests;

/// <summary>
/// Organização das telas na janela principal com monitores e janelas de várias proporções (vertical, ultrawide,
/// 5:4, janela pequena) e vídeos de várias proporções (quem compartilha um monitor vertical manda um vídeo em pé).
/// </summary>
public class StageLayoutTests
{
    private const double Gap = 2;
    private const double Landscape = 16.0 / 9;
    private const double Portrait = 608.0 / 1080; // tela de monitor vertical em 1080p

    // Espaço que sobra para o bloco das telas (janela menos margens, cabeçalho e lateral de grupos) em cada caso.
    public static TheoryData<string, double, double> Espacos => new()
    {
        { "janela padrão", 876, 500 },
        { "full HD maximizada", 1616, 916 },
        { "16:10 maximizada", 1616, 1076 },
        { "5:4 maximizada", 976, 852 },
        { "notebook 1366×768 a 125%", 789, 470 },
        { "monitor vertical 1080×1920", 776, 1716 },
        { "monitor vertical a 150% (largura mínima)", 336, 1136 },
        { "ultrawide 21:9", 3136, 1284 },
        { "super ultrawide 32:9", 4816, 1284 },
        { "janela mínima", 336, 324 },
        { "janela baixa e larga", 1400, 330 },
    };

    [Theory]
    [InlineData(876, 500, 876, 492)]     // proporção comum: o maior 16:9
    [InlineData(1616, 916, 1616, 909)]
    [InlineData(1700, 916, 1628, 916)]   // um pouco mais larga que 16:9: limitada pela altura
    [InlineData(700, 525, 700, 393)]     // 4:3 em ponto ainda é janela comum
    [InlineData(776, 1716, 776, 1379)]   // monitor vertical: cresce para baixo até 9:16
    [InlineData(336, 1136, 336, 597)]
    [InlineData(800, 700, 800, 700)]     // mais alta que 4:3: usa a altura toda
    [InlineData(3136, 1284, 2996, 1284)] // ultrawide: cresce para os lados até 21:9
    [InlineData(4816, 1284, 2996, 1284)]
    [InlineData(2400, 1200, 2133, 1200)] // 2:1 em ponto ainda é 16:9
    public void Bloco_AcompanhaAProporcaoDaJanela(double width, double height, double stageWidth, double stageHeight)
    {
        Assert.Equal((stageWidth, stageHeight), StageLayout.Stage(width, height));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(-10, 500)]
    [InlineData(500, 0)]
    [InlineData(double.NaN, 500)]
    [InlineData(500, double.PositiveInfinity)]
    public void Bloco_SemEspaco_FicaVazio(double width, double height)
    {
        Assert.Equal((0.0, 0.0), StageLayout.Stage(width, height));
    }

    [Theory]
    [MemberData(nameof(Espacos))]
    public void Bloco_CabeNoEspacoComProporcaoEntre9por16E21por9(string caso, double width, double height)
    {
        var (stageWidth, stageHeight) = StageLayout.Stage(width, height);

        Assert.True(stageWidth > 0 && stageHeight > 0, caso);
        Assert.True(stageWidth <= width && stageHeight <= height, $"{caso}: {stageWidth}x{stageHeight} em {width}x{height}");
        Assert.InRange(stageWidth / stageHeight, StageLayout.MinAspect - 0.01, StageLayout.MaxAspect + 0.01);
        Assert.Equal(Math.Floor(stageWidth), stageWidth);
        Assert.Equal(Math.Floor(stageHeight), stageHeight);
    }

    [Fact]
    public void Bloco_RedimensionarAJanelaDevagar_NaoPula()
    {
        // Fora dos dois limiares (4:3 e 2:1), um pixel a mais na janela muda o bloco em no máximo dois.
        var previous = StageLayout.Stage(1200, 700);
        for (var width = 1201.0; width < 1400; width++)
        {
            var current = StageLayout.Stage(width, 700);
            Assert.InRange(current.Width - previous.Width, 0, 2);
            Assert.InRange(Math.Abs(current.Height - previous.Height), 0, 2);
            previous = current;
        }
    }

    /// <summary>Invariantes em todos os espaços, com 1 a 4 telas de proporções variadas, na grade e com uma fixada.</summary>
    [Theory]
    [MemberData(nameof(Espacos))]
    public void Telas_FicamDentroDoBlocoSemSobreporENaProporcaoDoVideo(string caso, double width, double height)
    {
        var (stageWidth, stageHeight) = StageLayout.Stage(width, height);
        double[][] mixes =
        [
            [Landscape],
            [Portrait],
            [Landscape, Landscape],
            [Portrait, Portrait],
            [Landscape, Portrait, Landscape],
            [Landscape, Landscape, Landscape, Landscape],
            [Portrait, Landscape, 21.0 / 9, 4.0 / 3],
        ];

        foreach (var aspects in mixes)
        {
            var label = $"{caso}, {aspects.Length} tela(s) [{string.Join(", ", aspects.Select(a => a.ToString("0.00")))}]";
            var grid = StageLayout.Grid(stageWidth, stageHeight, aspects, Gap);
            AssertValid(label + ", grade", grid, aspects, stageWidth, stageHeight);

            if (aspects.Length > 1)
            {
                var (main, thumbnails) = StageLayout.Pinned(stageWidth, stageHeight, aspects[0], aspects[1..], Gap);
                AssertValid(label + ", fixada", [main, .. thumbnails], aspects, stageWidth, stageHeight);
                Assert.All(thumbnails, t => Assert.True(t.Area < main.Area, $"{label}: miniatura maior que a fixada"));
            }
        }
    }

    [Fact]
    public void Grade_UmaTela_OcupaOBlocoInteiro()
    {
        var tile = Assert.Single(StageLayout.Grid(1616, 909, [Landscape], Gap));

        Assert.Equal(new LayoutRect(0, 0, 1616, 909), tile);
    }

    [Fact]
    public void Grade_DuasTelas16por9_NumaJanelaComum_FicamLadoALado()
    {
        var tiles = StageLayout.Grid(1616, 909, [Landscape, Landscape], Gap);

        Assert.Equal(tiles[0].Y, tiles[1].Y);
        Assert.True(tiles[1].X >= tiles[0].X + tiles[0].Width + Gap);
    }

    [Fact]
    public void Grade_DuasTelas16por9_NumMonitorVertical_FicamEmpilhadasNaLarguraToda()
    {
        var (width, height) = StageLayout.Stage(776, 1716);
        var tiles = StageLayout.Grid(width, height, [Landscape, Landscape], Gap);

        Assert.Equal(tiles[0].X, tiles[1].X);
        Assert.True(tiles[1].Y >= tiles[0].Y + tiles[0].Height + Gap);
        Assert.All(tiles, t => Assert.Equal(width, t.Width));
    }

    [Fact]
    public void Grade_MonitorVertical_TelasFicamMaioresQueNoBloco16por9()
    {
        // Antes o bloco era sempre 16:9: num monitor vertical as telas ficavam espremidas numa faixa no meio.
        var (width, height) = StageLayout.Stage(776, 1716);
        var old = StageLayout.Grid(776, 436, [Landscape, Landscape, Landscape], Gap);
        var now = StageLayout.Grid(width, height, [Landscape, Landscape, Landscape], Gap);

        Assert.True(now.Sum(t => t.Area) > old.Sum(t => t.Area) * 3, $"{now.Sum(t => t.Area)} contra {old.Sum(t => t.Area)}");
    }

    [Fact]
    public void Grade_DuasTelasEmPe_NumaJanelaComum_FicamLadoALadoNaAlturaToda()
    {
        // Com a grade pensada só em 16:9, duas telas de monitor vertical empatavam e ficavam empilhadas, minúsculas.
        var tiles = StageLayout.Grid(1616, 909, [Portrait, Portrait], Gap);

        Assert.Equal(tiles[0].Y, tiles[1].Y);
        Assert.All(tiles, t => Assert.Equal(909, t.Height));
    }

    [Fact]
    public void Grade_QuatroTelas_NumaJanelaComum_FicamEm2x2()
    {
        var tiles = StageLayout.Grid(1616, 909, [Landscape, Landscape, Landscape, Landscape], Gap);

        Assert.Equal(2, tiles.Select(t => t.X).Distinct().Count());
        Assert.Equal(2, tiles.Select(t => t.Y).Distinct().Count());
    }

    [Fact]
    public void Grade_TresTelas_ATerceiraFicaCentralizadaEmbaixo()
    {
        var tiles = StageLayout.Grid(1616, 909, [Landscape, Landscape, Landscape], Gap);

        Assert.Equal(tiles[0].Y, tiles[1].Y);
        Assert.True(tiles[2].Y > tiles[0].Y);
        Assert.InRange(tiles[2].X + tiles[2].Width / 2, 1616 / 2.0 - 1, 1616 / 2.0 + 1);
    }

    [Fact]
    public void Grade_Ultrawide_DuasTelasLadoALadoMaioresQueNoBloco16por9()
    {
        var (width, height) = StageLayout.Stage(3136, 1284);
        var ultrawide = StageLayout.Grid(width, height, [Landscape, Landscape], Gap);
        var old = StageLayout.Grid(2282, 1284, [Landscape, Landscape], Gap);

        Assert.Equal(ultrawide[0].Y, ultrawide[1].Y);
        Assert.True(ultrawide[0].Area > old[0].Area * 1.5);
    }

    [Fact]
    public void Grade_TelaQueAindaNaoTemTamanho_Vale16por9()
    {
        var tile = Assert.Single(StageLayout.Grid(1600, 1600, [double.NaN], Gap));

        Assert.Equal((1600.0, 900.0), (tile.Width, tile.Height));
        Assert.Equal(350, tile.Y);
    }

    [Fact]
    public void Fixada_NumaJanelaComum_MiniaturasNaColunaDaDireita()
    {
        var (main, thumbnails) = StageLayout.Pinned(1616, 909, Landscape, [Landscape, Landscape, Landscape], Gap);

        Assert.All(thumbnails, t => Assert.True(t.X >= main.X + main.Width + Gap));
        Assert.Single(thumbnails.Select(t => t.X).Distinct());
    }

    [Fact]
    public void Fixada_NumMonitorVertical_MiniaturasNaLinhaDeBaixo()
    {
        var (width, height) = StageLayout.Stage(776, 1716);
        var (main, thumbnails) = StageLayout.Pinned(width, height, Landscape, [Landscape, Landscape], Gap);

        Assert.All(thumbnails, t => Assert.True(t.Y >= main.Y + main.Height + Gap));
        Assert.Single(thumbnails.Select(t => t.Y).Distinct());
        Assert.Equal(width, main.Width);
    }

    [Fact]
    public void Fixada_TelaEmPeNumaJanelaComum_UsaAAlturaToda()
    {
        var (main, _) = StageLayout.Pinned(1616, 909, Portrait, [Landscape], Gap);

        Assert.Equal(909, main.Height);
    }

    [Fact]
    public void Fixada_MaisMiniaturasDoQueCabem_AsDemaisFicamSemLugar()
    {
        var (_, thumbnails) = StageLayout.Pinned(1616, 909, Landscape, [Landscape, Landscape, Landscape, Landscape], Gap);

        Assert.Equal(4, thumbnails.Count);
        Assert.Equal(0, thumbnails[3].Area);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 0, 0, 1920, 1080)]  // mesma proporção: ocupa tudo
    [InlineData(1080, 1920, 1920, 1080, 656, 0, 608, 1080)] // tela girou para vertical no meio da transmissão
    [InlineData(1920, 1080, 608, 1080, 0, 368, 608, 342)]   // e o contrário
    [InlineData(1, 2000, 1280, 720, 638, 0, 2, 720)]        // janela fininha: pelo menos 2 de largura
    [InlineData(0, 0, 1280, 720, 0, 0, 1280, 720)]          // quadro vazio
    public void FaixasPretas_EncaixamAOrigemCentralizadaComLadosPares(
        int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, int left, int top, int width, int height)
    {
        Assert.Equal((left, top, width, height), ScreenCapture.Letterbox(sourceWidth, sourceHeight, outputWidth, outputHeight));
    }

    [Fact]
    public void FaixasPretas_SempreDentroDoQuadroEPares()
    {
        foreach (var (sw, sh) in new[] { (1, 1), (3, 7), (1080, 1920), (3440, 1440), (7680, 64), (64, 7680), (999, 333) })
        {
            foreach (var (ow, oh) in new[] { (64, 64), (608, 1080), (1920, 1080), (3840, 1080), (404, 720) })
            {
                var (left, top, width, height) = ScreenCapture.Letterbox(sw, sh, ow, oh);
                var label = $"{sw}x{sh} em {ow}x{oh}: {left},{top} {width}x{height}";
                Assert.True(left >= 0 && top >= 0 && left + width <= ow && top + height <= oh, label);
                Assert.True(width >= 2 && height >= 2, label);
                Assert.True(left % 2 == 0 && top % 2 == 0 && width % 2 == 0 && height % 2 == 0, label);
            }
        }
    }

    [Fact]
    public async Task Espectador_ProporcaoVemDoAnuncioEDepoisDosQuadros()
    {
        var service = new FakeScreenShareService();
        var share = new ScreenShareDto(Guid.NewGuid(), 1920, 1080, 30, 4000, false);
        var viewer = new StreamViewerViewModel(service, Guid.NewGuid(), "Sala", FakeGroupsService.Me, share);
        var changes = new List<string?>();
        viewer.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await viewer.StartAsync();

        Assert.Equal(16.0 / 9, viewer.VideoAspect, 3);

        // Quem compartilha passou para um monitor vertical: os quadros chegam em pé e a grade é avisada uma vez.
        service.Watching[0].RaiseFrame(608, 1080);
        service.Watching[0].RaiseFrame(608, 1080);

        Assert.Equal(608.0 / 1080, viewer.VideoAspect, 3);
        Assert.Equal(1, changes.Count(c => c == nameof(StreamViewerViewModel.VideoAspect)));
    }

    [Fact]
    public void Espectador_AnuncioSemTamanho_Vale16por9()
    {
        var viewer = new StreamViewerViewModel(new FakeScreenShareService(), Guid.NewGuid(), "Sala", FakeGroupsService.Me,
            new ScreenShareDto(Guid.NewGuid(), 0, 0, 30, 4000, false));

        Assert.Equal(16.0 / 9, viewer.VideoAspect, 3);
    }

    private static void AssertValid(string label, IReadOnlyList<LayoutRect> tiles, double[] aspects, double width, double height)
    {
        Assert.Equal(aspects.Length, tiles.Count);
        var placed = tiles.Where(t => t.Area > 0).ToList();
        Assert.True(placed.Count == Math.Min(tiles.Count, StageLayout.MaxThumbnails + 1), $"{label}: {placed.Count} telas com lugar");
        for (var i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (t.Area == 0)
            {
                continue;
            }

            Assert.True(t.X >= 0 && t.Y >= 0 && t.X + t.Width <= width && t.Y + t.Height <= height, $"{label}: tela {i} {t} fora de {width}x{height}");

            // A proporção do vídeo, a menos do arredondamento para pixels inteiros.
            Assert.True(Math.Abs(t.Width - t.Height * aspects[i]) <= Math.Max(1.5, aspects[i] * 1.5), $"{label}: tela {i} {t} fora da proporção {aspects[i]:0.00}");
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var o = tiles[j];
                if (o.Area == 0)
                {
                    continue;
                }

                var apart = t.X + t.Width + Gap <= o.X + 0.001 || o.X + o.Width + Gap <= t.X + 0.001 ||
                            t.Y + t.Height + Gap <= o.Y + 0.001 || o.Y + o.Height + Gap <= t.Y + 0.001;
                Assert.True(apart, $"{label}: telas {i} {t} e {j} {o} se sobrepõem ou estão sem a linha entre elas");
            }
        }
    }
}
