using ScreenCord.Media;

namespace ScreenCord.Tests;

public class FramePacerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1.0 / 30);

    /// <summary>Simula <paramref name="seconds"/> de transmissão a 30 fps e conta os quadros codificados.</summary>
    private static int Run(FramePacer pacer, ref TimeSpan now, double seconds, Func<int, bool> changed, Func<int, bool>? keyframe = null)
    {
        var encoded = 0;
        var frames = (int)Math.Round(seconds * 30);
        for (var i = 0; i < frames; i++)
        {
            if (pacer.ShouldEncode(now, changed(i), keyframe?.Invoke(i) ?? false))
            {
                encoded++;
            }

            now += Frame;
        }

        return encoded;
    }

    [Fact]
    public void TelaMudando_CodificaTodosOsQuadros()
    {
        var now = TimeSpan.Zero;
        Assert.Equal(90, Run(new FramePacer(), ref now, 3, _ => true));
    }

    [Fact]
    public void TelaParada_CaiParaDoisQuadrosPorSegundo()
    {
        var pacer = new FramePacer();
        var now = TimeSpan.Zero;

        // Um quadro novo e depois nada: ritmo cheio durante 1 s, depois 2 por segundo.
        Assert.Equal(30, Run(pacer, ref now, 1, i => i == 0));
        Assert.InRange(Run(pacer, ref now, 10, _ => false), 19, 21);
    }

    [Fact]
    public void TelaVoltaAMudar_VoltaAoRitmoCheio()
    {
        var pacer = new FramePacer();
        var now = TimeSpan.Zero;
        Run(pacer, ref now, 5, i => i == 0);
        Assert.Equal(30, Run(pacer, ref now, 1, i => i == 0));
    }

    [Fact]
    public void QuadroChavePedido_SaiNaHoraMesmoComATelaParada()
    {
        var pacer = new FramePacer();
        var now = TimeSpan.Zero;
        Run(pacer, ref now, 5, i => i == 0);

        // Logo depois de um quadro ocioso, o próximo só sairia em 500 ms; o pedido de quadro-chave fura a fila.
        while (!pacer.ShouldEncode(now, false, false))
        {
            now += Frame;
        }

        Assert.False(pacer.ShouldEncode(now + Frame, false, false));
        Assert.True(pacer.ShouldEncode(now + Frame, false, keyframePending: true));
    }

    [Fact]
    public void PrimeiroQuadro_SempreSai()
    {
        Assert.True(new FramePacer().ShouldEncode(TimeSpan.Zero, false, false));
    }
}
