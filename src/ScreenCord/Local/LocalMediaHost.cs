using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.Local;

/// <summary>
/// Compartilhamento de tela de uma sala local: quem compartilha faz o papel do relay da API e envia os pacotes
/// direto a cada espectador. Cada espectador tem a própria fila: quem tem conexão lenta perde quadros (e volta no
/// próximo quadro-chave) sem atrasar os outros. Sobrevive à troca de configurações (<see cref="Acquire"/>): quem
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
    private long _overflows;
    private bool _stopped;

    public LocalMediaHost(int bitrateKbps)
    {
        QueueLimitBytes = Math.Max(512 * 1024, bitrateKbps * 1000 / 8);
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Chave que quem assiste apresenta em <see cref="KeyHeader"/>; vai para a sala junto com o compartilhamento.</summary>
    public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>Cerca de 1 s de vídeo (no mínimo 512 KB, para caber um quadro-chave).</summary>
    public int QueueLimitBytes { get; }

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
        var viewer = new Viewer(socket, QueueLimitBytes, RequestKeyframe);
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
            needsKeyframe |= !viewer.Offer(packet, ref _overflows);
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

    private sealed class Viewer(WebSocket socket, int queueLimitBytes, Action requestKeyframe)
    {
        private readonly Channel<Packet> _queue = Channel.CreateUnbounded<Packet>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _closed = new();
        private long _queuedBytes;
        private volatile bool _waitingForKeyframe = true;
        private volatile bool _videoPaused;

        public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

        /// <summary>Enfileira o pacote. Retorna falso se o espectador precisa de um quadro-chave para continuar.</summary>
        public bool Offer(Packet packet, ref long overflows)
        {
            if (packet.Type == MediaContract.Video)
            {
                if (_videoPaused)
                {
                    return true;
                }

                if (_waitingForKeyframe && !packet.IsKeyframe)
                {
                    return false;
                }

                if (Interlocked.Read(ref _queuedBytes) + packet.Data.Length > queueLimitBytes)
                {
                    // Atrasado demais: descarta até o próximo quadro-chave em vez de acumular atraso.
                    _waitingForKeyframe = true;
                    Interlocked.Increment(ref overflows);
                    return false;
                }

                _waitingForKeyframe = false;
            }
            else if (Interlocked.Read(ref _queuedBytes) + packet.Data.Length > queueLimitBytes * 2)
            {
                return true;
            }

            Interlocked.Add(ref _queuedBytes, packet.Data.Length);
            _queue.Writer.TryWrite(packet);
            return true;
        }

        public void ResetToKeyframe() => _waitingForKeyframe = true;

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
                await foreach (var packet in _queue.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                {
                    Interlocked.Add(ref _queuedBytes, -packet.Data.Length);
                    await socket.SendAsync(packet.Data, WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false);
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
                            _waitingForKeyframe = true;
                            _videoPaused = false;
                            requestKeyframe();
                            break;
                        case MediaContract.KeyframeRequest when !_videoPaused:
                            _waitingForKeyframe = true;
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
