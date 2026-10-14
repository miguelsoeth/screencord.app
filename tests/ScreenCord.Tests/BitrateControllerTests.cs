using ScreenCord.Media;

namespace ScreenCord.Tests;

public class BitrateControllerTests
{
    private static TimeSpan Second(int n) => TimeSpan.FromSeconds(n);

    /// <summary>4000 kbps = 500 KB/s: 250 KB parados na fila são 0,5 s, acima do limite de congestionamento.</summary>
    private const long CongestedBytes = 250_000;

    [Fact]
    public void RedeFolgada_MantemATaxaEscolhida()
    {
        var controller = new BitrateController(4000, TimeSpan.Zero);
        for (var s = 1; s <= 20; s++)
        {
            Assert.Null(controller.Update(Second(s), 0, 0));
        }

        Assert.Equal(4000, controller.CurrentKbps);
    }

    [Fact]
    public void FilaParada_BaixaATaxa()
    {
        var controller = new BitrateController(4000, TimeSpan.Zero);
        Assert.Equal(2800, controller.Update(Second(1), CongestedBytes, 0));
    }

    [Fact]
    public void PicoDeQuadroChaveQueEsvazia_NaoContaComoCongestionamento()
    {
        var controller = new BitrateController(4000, TimeSpan.Zero);
        controller.Update(TimeSpan.FromMilliseconds(100), 400_000, 0);
        controller.Update(TimeSpan.FromMilliseconds(500), 0, 0);
        Assert.Null(controller.Update(Second(1), 400_000, 0));
    }

    [Fact]
    public void FilaEstourada_BaixaATaxaMesmoSemFilaParada()
    {
        var controller = new BitrateController(4000, TimeSpan.Zero);
        Assert.Equal(2800, controller.Update(Second(1), 0, overflows: 1));
    }

    [Fact]
    public void CongestionamentoContinuo_DesceAteOMinimoSemPassarDele()
    {
        var controller = new BitrateController(4000, TimeSpan.Zero);
        for (var s = 1; s <= 60; s++)
        {
            controller.Update(Second(s), 10_000_000, 0);
        }

        Assert.Equal(800, controller.MinKbps);
        Assert.Equal(800, controller.CurrentKbps);
    }

    [Fact]
    public void DepoisDeBaixar_EsperaAntesDeBaixarDeNovo()
    {
        var controller = new BitrateController(4000, TimeSpan.Zero);
        Assert.Equal(2800, controller.Update(Second(1), CongestedBytes, 0));
        Assert.Null(controller.Update(Second(2), CongestedBytes, 0));
        Assert.Equal(1960, controller.Update(Second(3), CongestedBytes, 0));
    }

    [Fact]
    public void RedeVoltaAFolgar_SobeAosPoucosAteATaxaEscolhida()
    {
        var controller = new BitrateController(4000, TimeSpan.Zero);
        controller.Update(Second(1), CongestedBytes, 0);
        Assert.Equal(2800, controller.CurrentKbps);

        // Espera algumas janelas depois da redução, depois sobe 400 kbps a cada 2 s com folga.
        for (var s = 2; s <= 5; s++)
        {
            Assert.Null(controller.Update(Second(s), 0, 0));
        }

        Assert.Equal(3200, controller.Update(Second(6), 0, 0));
        Assert.Null(controller.Update(Second(7), 0, 0));
        Assert.Equal(3600, controller.Update(Second(8), 0, 0));

        for (var s = 9; s <= 30; s++)
        {
            controller.Update(Second(s), 0, 0);
        }

        Assert.Equal(4000, controller.CurrentKbps);
    }

    [Theory]
    [InlineData(2000, 400)]
    [InlineData(5000, 1000)]
    [InlineData(1000, 300)]
    [InlineData(200, 200)]
    public void Minimo_EUmQuintoDaTaxaEscolhida(int target, int min)
    {
        Assert.Equal(min, new BitrateController(target, TimeSpan.Zero).MinKbps);
    }
}
