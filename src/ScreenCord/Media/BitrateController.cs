namespace ScreenCord.Media;

/// <summary>
/// Bitrate adaptativo de quem compartilha: acompanha a fila de envio do <see cref="MediaSocket"/>, baixa a taxa
/// do encoder quando a rede não dá conta e sobe de volta aos poucos quando ela folga, até a taxa escolhida.
/// A fila é medida pelo menor nível de cada janela de 1 s: um quadro-chave grande, que logo sai, não conta
/// como congestionamento; uma fila que nunca esvazia, sim.
/// </summary>
public sealed class BitrateController
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>Fila parada acima disso (em segundos de vídeo na taxa atual): a rede não está acompanhando.</summary>
    private const double CongestedQueueSeconds = 0.25;

    /// <summary>Fila abaixo disso: há folga para subir.</summary>
    private const double ClearQueueSeconds = 0.05;

    /// <summary>Cada redução deixa 70% da taxa atual.</summary>
    private const int DecreasePercent = 70;

    /// <summary>Janelas entre duas reduções, para a fila esvaziar antes de medir de novo.</summary>
    private const int WindowsBetweenDecreases = 2;

    /// <summary>Janelas depois de uma redução antes de tentar subir.</summary>
    private const int WindowsBeforeIncrease = 5;

    /// <summary>Janelas seguidas com folga antes de cada aumento.</summary>
    private const int ClearWindowsPerIncrease = 2;

    private TimeSpan _windowStart;
    private long _minQueuedBytes = long.MaxValue;
    private long _lastOverflows;
    private int _windowsSinceDecrease = int.MaxValue / 2;
    private int _clearWindows;

    public BitrateController(int targetKbps, TimeSpan start)
    {
        TargetKbps = targetKbps;
        MinKbps = Math.Min(targetKbps, Math.Max(300, targetKbps / 5));
        CurrentKbps = targetKbps;
        _windowStart = start;
    }

    /// <summary>A taxa escolhida pelo usuário, que nunca é ultrapassada.</summary>
    public int TargetKbps { get; }

    public int MinKbps { get; }

    public int CurrentKbps { get; private set; }

    /// <summary>
    /// Registra o estado da fila (chamado a cada quadro) e, ao fim de cada janela, decide a taxa. Retorna a nova
    /// taxa quando ela muda. <paramref name="overflows"/> é o contador acumulado de <see cref="MediaSocket.VideoOverflows"/>.
    /// </summary>
    public int? Update(TimeSpan now, long queuedBytes, long overflows)
    {
        _minQueuedBytes = Math.Min(_minQueuedBytes, queuedBytes);
        if (now - _windowStart < Window)
        {
            return null;
        }

        var queueSeconds = _minQueuedBytes * 8 / (CurrentKbps * 1000.0);
        var overflowed = overflows != _lastOverflows;
        _windowStart = now;
        _minQueuedBytes = long.MaxValue;
        _lastOverflows = overflows;
        _windowsSinceDecrease++;

        if (overflowed || queueSeconds > CongestedQueueSeconds)
        {
            _clearWindows = 0;
            if (_windowsSinceDecrease < WindowsBetweenDecreases)
            {
                return null;
            }

            _windowsSinceDecrease = 0;
            return Change(Math.Max(MinKbps, CurrentKbps * DecreasePercent / 100));
        }

        _clearWindows = queueSeconds < ClearQueueSeconds ? _clearWindows + 1 : 0;
        if (_windowsSinceDecrease < WindowsBeforeIncrease || _clearWindows < ClearWindowsPerIncrease)
        {
            return null;
        }

        // Sobe devagar (10% da taxa escolhida por vez) e desce rápido (30%), como o TCP.
        _clearWindows = 0;
        return Change(Math.Min(TargetKbps, CurrentKbps + Math.Max(100, TargetKbps / 10)));
    }

    private int? Change(int kbps)
    {
        if (kbps == CurrentKbps)
        {
            return null;
        }

        CurrentKbps = kbps;
        return kbps;
    }
}
