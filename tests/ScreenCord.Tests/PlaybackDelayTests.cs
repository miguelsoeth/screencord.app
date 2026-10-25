using ScreenCord.Media;

namespace ScreenCord.Tests;

public class PlaybackDelayTests
{
    /// <summary>Um quadro a 60 fps, em microssegundos.</summary>
    private const long Frame = 16_667;

    /// <summary>O relógio local começa bem longe do de quem compartilha: só a diferença relativa importa.</summary>
    private const long Clock = 5_000_000_000;

    [Fact]
    public void NoRitmo_DecodificaTudo()
    {
        var delay = new PlaybackDelay();
        for (var i = 0; i < 600; i++)
        {
            Assert.Equal(PlaybackDecision.Decode, delay.OnFrame(i * Frame, i % 240 == 0, Clock + i * Frame));
        }

        Assert.Equal(0, delay.Skips);
    }

    [Fact]
    public void VariacaoDaRedeAbaixoDoLimite_NaoDescarta()
    {
        var delay = new PlaybackDelay();
        for (var i = 0; i < 600; i++)
        {
            var jitter = i % 7 * 100_000; // até 600 ms
            Assert.Equal(PlaybackDecision.Decode, delay.OnFrame(i * Frame, i == 0, Clock + i * Frame + jitter));
        }
    }

    [Fact]
    public void DecodificacaoLenta_DescartaAteOQuadroChaveEVoltaAoTempoReal()
    {
        var delay = new PlaybackDelay();
        var now = Clock;
        var i = 0;
        PlaybackDecision decision;

        // Cada quadro leva 2 quadros para ser decodificado: o atraso cresce até passar de 1 s.
        do
        {
            decision = delay.OnFrame(i * Frame, i == 0, now);
            now += 2 * Frame;
            i++;
        }
        while (decision == PlaybackDecision.Decode && i < 1000);

        Assert.Equal(PlaybackDecision.SkipAndRequestKeyframe, decision);
        Assert.True(delay.Current > TimeSpan.FromSeconds(1));

        // Descartar é rápido: o que estava atrasado passa sem decodificar e o quadro-chave novo chega no tempo.
        Assert.Equal(PlaybackDecision.Skip, delay.OnFrame(i * Frame, false, now));
        now = Clock + (i + 30) * Frame;
        Assert.Equal(PlaybackDecision.Decode, delay.OnFrame((i + 30) * Frame, true, now));
        Assert.Equal(PlaybackDecision.Decode, delay.OnFrame((i + 31) * Frame, false, now + Frame));
        Assert.True(delay.Current < TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, delay.Skips);
    }

    [Fact]
    public void QuadroChaveAtrasadoTambem_ViraAReferenciaSemDescartarDeNovo()
    {
        var delay = new PlaybackDelay();
        delay.OnFrame(0, true, Clock);
        Assert.Equal(PlaybackDecision.SkipAndRequestKeyframe, delay.OnFrame(Frame, false, Clock + Frame + 1_500_000));

        // O atraso vem de antes (rede): o quadro-chave chega tão atrasado quanto, e descartar aqui não adianta.
        Assert.Equal(PlaybackDecision.Decode, delay.OnFrame(2 * Frame, true, Clock + 2 * Frame + 1_500_000));
        for (var i = 3; i < 100; i++)
        {
            Assert.Equal(PlaybackDecision.Decode, delay.OnFrame(i * Frame, false, Clock + i * Frame + 1_500_000));
        }

        Assert.Equal(1, delay.Skips);
    }

    [Fact]
    public void EncoderRecomecou_RecalculaAReferencia()
    {
        var delay = new PlaybackDelay();
        delay.OnFrame(0, true, Clock);
        delay.OnFrame(60_000_000, false, Clock + 60_000_000);

        // Troca de configurações: os carimbos voltam a 0, mas não é atraso.
        Assert.Equal(PlaybackDecision.Decode, delay.OnFrame(0, true, Clock + 61_000_000));
        Assert.Equal(PlaybackDecision.Decode, delay.OnFrame(Frame, false, Clock + 61_000_000 + Frame));
        Assert.Equal(TimeSpan.Zero, delay.Current);
    }
}
