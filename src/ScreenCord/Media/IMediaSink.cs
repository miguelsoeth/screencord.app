namespace ScreenCord.Media;

/// <summary>
/// Para onde vai a mídia de quem compartilha: o canal com a API (<see cref="MediaSocket"/>) ou, nas salas locais,
/// o próprio computador repassando a cada espectador (<see cref="Local.LocalMediaHost"/>).
/// </summary>
public interface IMediaSink : IAsyncDisposable
{
    /// <summary>Alguém começou a assistir, perdeu pacotes ou a fila descartou vídeo: é preciso um quadro-chave.</summary>
    event Action? KeyframeRequested;

    /// <summary>O canal terminou; o argumento é o motivo quando não foi um encerramento normal.</summary>
    event Action<string?>? Closed;

    /// <summary>Bytes ainda não entregues (para o bitrate adaptativo).</summary>
    long QueuedBytes { get; }

    /// <summary>Quantas vezes a fila encheu e o vídeo passou a ser descartado até o próximo quadro-chave.</summary>
    long VideoOverflows { get; }

    /// <summary>Começa a enviar. Chamado depois de assinar os eventos.</summary>
    void Start();

    void SendVideo(EncodedFrame frame, long timestampMicroseconds);

    void SendAudio(byte[] opus, long timestampMicroseconds);
}
