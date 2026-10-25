using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.Local;

/// <summary>
/// Compartilhamento de tela de uma sala local: quem compartilha faz o papel do relay da API e envia os pacotes
/// direto a cada espectador. Cada espectador tem a própria fila (<see cref="MediaQueue"/>, limitada por tempo): quem
/// tem conexão lenta perde quadros (e volta no próximo quadro-chave) sem atrasar os outros. Sobrevive à troca de configurações (<see cref="Acquire"/>): quem
/// assiste continua conectado e recomeça do próximo quadro-chave.
/// </summary>
public sealed class LocalMediaHost
{
    public const string KeyHeader = "X-ScreenCord-Key";
    private static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _lock = new();
    private readonly List<Viewer> _viewers = [];
    private Lease? _current;
    private long _lastKeyframeRequestTicks;
    private readonly int _bitrateKbps;
    private long _overflows;
    private bool _stopped;

    public LocalMediaHost(int bitrateKbps)
    {
        _bitrateKbps = bitrateKbps;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Chave que quem assiste apresenta em <see cref="KeyHeader"/>; vai para a sala junto com o compartilhamento.</summary>
    public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public int ViewerCount
    {
        get
        {
            lock (_lock)
            {
                return _viewers.Count;
            }
        }
    }

    /// <summary>Canal de envio do encoder atual. Um novo (troca de configurações) substitui o anterior.</summary>
    public IMediaSink Acquire()
    {
        var lease = new Lease(this);
        lock (_lock)
        {
            _current = lease;
            foreach (var viewer in _viewers)
            {
                viewer.ResetToKeyframe();
            }
        }

        return lease;
    }

    public bool AcceptsKey(string? key) =>
        key is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(key), System.Text.Encoding.ASCII.GetBytes(Key));

    /// <summary>Atende um espectador até ele sair ou o compartilhamento acabar.</summary>
    public async Task WatchAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var viewer = new Viewer(socket, new MediaQueue(_bitrateKbps, waitForKeyframe: true), RequestKeyframe);
        lock (_lock)
        {
            if (_stopped)
            {
                viewer.Close();
            }
            else
            {
                _viewers.Add(viewer);
            }
        }

        RequestKeyframe();
        try
        {
            await viewer.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _viewers.Remove(viewer);
            }
        }
    }

    /// <summary>Encerra o compartilhamento: quem assiste é desconectado.</summary>
    public void Stop()
    {
        List<Viewer> viewers;
        lock (_lock)
        {
            _stopped = true;
            viewers = [.. _viewers];
            _viewers.Clear();
        }

        foreach (var viewer in viewers)
        {
            viewer.Close();
        }
    }

    private void Broadcast(Packet packet)
    {
        Viewer[] viewers;
        lock (_lock)
        {
            viewers = [.. _viewers];
        }

        var needsKeyframe = false;
        foreach (var viewer in viewers)
        {
            var overflows = viewer.Overflows;
            needsKeyframe |= !viewer.Offer(packet);
            if (viewer.Overflows != overflows)
            {
                Interlocked.Increment(ref _overflows);
            }
        }

        if (needsKeyframe)
        {
            RequestKeyframe();
        }
    }

    private void RequestKeyframe()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastKeyframeRequestTicks);
        if (now - last < KeyframeRequestInterval.TotalMilliseconds ||
            Interlocked.CompareExchange(ref _lastKeyframeRequestTicks, now, last) != last)
        {
            return;
        }

        Lease? current;
        lock (_lock)
        {
            current = _current;
        }

        current?.RaiseKeyframeRequested();
    }

    private long MaxQueuedBytes()
    {
        lock (_lock)
        {
            return _viewers.Count == 0 ? 0 : _viewers.Max(v => v.QueuedBytes);
        }
    }

    private sealed record Packet(byte[] Data, byte Type, bool IsKeyframe);

    /// <summary>O lado do encoder: a mesma interface do canal de mídia com a API.</summary>
    private sealed class Lease(LocalMediaHost host) : IMediaSink
    {
        public event Action? KeyframeRequested;

        // A mídia não depende de uma conexão com outro computador: só para quando o compartilhamento termina.
        public event Action<string?>? Closed
        {
            add { }
            remove { }
        }

        /// <summary>O maior atraso entre os espectadores: é o upload de quem compartilha que limita.</summary>
        public long QueuedBytes => host.MaxQueuedBytes();

        public long VideoOverflows => Interlocked.Read(ref host._overflows);

        public void Start()
        {
        }

        public void SendVideo(EncodedFrame frame, long timestampMicroseconds) =>
            Send(MediaContract.Video, frame.IsKeyframe ? MediaContract.KeyframeFlag : (byte)0, timestampMicroseconds, frame.Data, frame.IsKeyframe);

        public void SendAudio(byte[] opus, long timestampMicroseconds) =>
            Send(MediaContract.Audio, 0, timestampMicroseconds, opus, false);

        public void RaiseKeyframeRequested() => KeyframeRequested?.Invoke();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Send(byte type, byte flags, long timestamp, byte[] payload, bool keyframe)
        {
            lock (host._lock)
            {
                // Só o encoder mais recente envia (o anterior está terminando durante a troca de configurações).
                if (host._current != this)
                {
                    return;
                }
            }

            var packet = new byte[MediaContract.HeaderSize + payload.Length];
            MediaContract.WriteHeader(packet, type, flags, timestamp);
            payload.CopyTo(packet, MediaContract.HeaderSize);
            host.Broadcast(new Packet(packet, type, keyframe));
        }
    }

    private sealed class Viewer(WebSocket socket, MediaQueue queue, Action requestKeyframe)
    {
        private readonly CancellationTokenSource _closed = new();
        private volatile bool _videoPaused;

        public long QueuedBytes => queue.QueuedBytes;

        public long Overflows => queue.Overflows;

        /// <summary>Enfileira o pacote. Retorna falso se o espectador precisa de um quadro-chave para continuar.</summary>
        public bool Offer(Packet packet)
        {
            if (packet.Type != MediaContract.Video)
            {
                queue.OfferAudio(packet.Data);
                return true;
            }

            // Não está olhando: economiza a banda do vídeo; o áudio continua.
            return _videoPaused || queue.OfferVideo(packet.Data, packet.IsKeyframe) != MediaOffer.NeedsKeyframe;
        }

        public void ResetToKeyframe() => queue.ResetToKeyframe();

        public void Close()
        {
            try
            {
                _closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            var receive = DrainIncomingAsync(linked);
            try
            {
                await foreach (var packet in queue.ReadAllAsync(linked.Token).ConfigureAwait(false))
                {
                    await socket.SendAsync(packet, WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
            }

            await LocalMessage.CloseAsync(socket);
            linked.Cancel();
            await receive.ConfigureAwait(false);
        }

        /// <summary>Quem assiste só envia pedidos de quadro-chave e de pausa do vídeo; a leitura também detecta o fechamento.</summary>
        private async Task DrainIncomingAsync(CancellationTokenSource linked)
        {
            var buffer = new byte[1024];
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (!result.EndOfMessage || result.Count != MediaContract.HeaderSize)
                    {
                        continue;
                    }

                    switch (buffer[0])
                    {
                        case MediaContract.PauseVideo:
                            _videoPaused = true;
                            break;
                        case MediaContract.ResumeVideo when _videoPaused:
                            queue.ResetToKeyframe();
                            _videoPaused = false;
                            requestKeyframe();
                            break;
                        case MediaContract.KeyframeRequest when !_videoPaused:
                            queue.ResetToKeyframe();
                            requestKeyframe();
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
            }

            try
            {
                linked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
