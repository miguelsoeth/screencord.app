namespace ScreenCord.Media;

/// <summary>O que fazer com um quadro recebido, segundo o <see cref="PlaybackDelay"/>.</summary>
public enum PlaybackDecision
{
    Decode,

    /// <summary>Atrasado: descarta (até o próximo quadro-chave).</summary>
    Skip,

    /// <summary>Atrasou agora: descarta e pede um quadro-chave.</summary>
    SkipAndRequestKeyframe,
}

/// <summary>
/// Atraso de quem assiste em relação a quem compartilha, pelos carimbos de tempo dos quadros. A diferença entre o
/// relógio local e o carimbo tem uma parte fixa (relógios diferentes, trajeto da rede), estimada pela menor diferença
/// vista; o que passar disso é atraso acumulado. Acima de <see cref="MaxDelay"/> (ex.: o computador não dá conta de
/// decodificar), os quadros são descartados até o próximo quadro-chave, em vez de a imagem ficar em câmera lenta.
/// </summary>
/// <remarks>
/// Se o quadro-chave que encerra o descarte também chega atrasado, o atraso vem de antes (rede, servidor) e descartar
/// aqui não o reduz: ele passa a ser a nova referência, até a diferença voltar a diminuir.
/// </remarks>
public sealed class PlaybackDelay(TimeSpan maxDelay)
{
    /// <summary>
    /// Folgado de propósito: um quadro-chave grande atrasa os quadros seguintes por um instante, e as filas de quem
    /// compartilha e do servidor já cortam o atraso da rede (<see cref="Contracts.MediaQueue"/>).
    /// </summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(1);

    /// <summary>Carimbo que volta mais que isso: o encoder recomeçou (troca de configurações).</summary>
    private const long RestartMicroseconds = 1_000_000;

    private readonly long _maxDelayMicroseconds = (long)(maxDelay.TotalMilliseconds * 1000);
    private long? _baseline;
    private long _lastTimestamp;
    private bool _skipping;

    public PlaybackDelay()
        : this(DefaultMaxDelay)
    {
    }

    public TimeSpan MaxDelay { get; } = maxDelay;

    /// <summary>Atraso acumulado no último quadro.</summary>
    public TimeSpan Current { get; private set; }

    /// <summary>Quantas vezes passou a descartar.</summary>
    public long Skips { get; private set; }

    /// <param name="timestampMicroseconds">Carimbo do quadro, no relógio de quem compartilha.</param>
    /// <param name="nowMicroseconds">Relógio local (qualquer origem, sempre crescente).</param>
    public PlaybackDecision OnFrame(long timestampMicroseconds, bool keyframe, long nowMicroseconds)
    {
        var offset = nowMicroseconds - timestampMicroseconds;
        if (_baseline is not { } baseline || offset < baseline || timestampMicroseconds < _lastTimestamp - RestartMicroseconds)
        {
            _baseline = baseline = offset;
        }

        _lastTimestamp = timestampMicroseconds;
        var delay = offset - baseline;
        Current = TimeSpan.FromTicks(delay * 10);

        if (_skipping)
        {
            if (!keyframe)
            {
                return PlaybackDecision.Skip;
            }

            _skipping = false;
            if (delay > _maxDelayMicroseconds)
            {
                _baseline = offset;
                Current = TimeSpan.Zero;
            }

            return PlaybackDecision.Decode;
        }

        if (!keyframe && delay > _maxDelayMicroseconds)
        {
            _skipping = true;
            Skips++;
            return PlaybackDecision.SkipAndRequestKeyframe;
        }

        return PlaybackDecision.Decode;
    }
}
