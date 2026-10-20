using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Services;

namespace ScreenCord.Media;

/// <summary>
/// Canal de mídia com a API (WebSocket autenticado pelo cabeçalho <c>Authorization</c>, como o hub).
/// Quem compartilha envia por <see cref="SendVideo"/>/<see cref="SendAudio"/>; quem assiste recebe por
/// <see cref="PacketReceived"/>.
/// </summary>
public sealed class MediaSocket : IMediaSink
{
    private readonly ClientWebSocket _socket;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _queueLimitBytes;
    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;
    private long _queuedBytes;
    private long _videoOverflows;
    private volatile bool _waitingForKeyframe;
    private int _closedRaised;
    private volatile bool _disposing;
    private readonly string _peer;

    /// <param name="peer">Com quem é a conexão, para as mensagens ("com a API central", "com o computador de Ana").</param>
    private MediaSocket(ClientWebSocket socket, int bitrateKbps, string peer = "com a API central")
    {
        _socket = socket;
        _peer = peer;

        // Cerca de 1 s de vídeo na fila de saída: mais que isso, a rede não está acompanhando.
        _queueLimitBytes = Math.Max(512 * 1024, bitrateKbps * 1000 / 8);
    }

    /// <summary>A API pediu um quadro-chave (alguém começou a assistir) ou a fila descartou vídeo.</summary>
    public event Action? KeyframeRequested;

    /// <summary>Pacote recebido (tipo, flags, carimbo em microssegundos, conteúdo), na thread de leitura.</summary>
    public event Action<byte, byte, long, ReadOnlyMemory<byte>>? PacketReceived;

    /// <summary>A conexão terminou; o argumento é o motivo quando não foi um encerramento normal.</summary>
    public event Action<string?>? Closed;

    /// <summary>Bytes na fila de saída, ainda não entregues ao socket.</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>Quantas vezes a fila encheu e o vídeo passou a ser descartado até o próximo quadro-chave.</summary>
    public long VideoOverflows => Interlocked.Read(ref _videoOverflows);

    public static async Task<MediaSocket> ConnectAsync(CentralApiClient api, Guid groupId, Guid sharerId, int bitrateKbps, CancellationToken cancellationToken)
    {
        var uri = MediaContract.BuildUri(api.BaseUri, groupId, sharerId);
        for (var attempt = 0; ; attempt++)
        {
            var token = await api.GetApiTokenAsync(cancellationToken).ConfigureAwait(false);
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            socket.Options.SetRequestHeader(ClientVersionContract.Header, AppVersion.Current);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                return new MediaSocket(socket, bitrateKbps);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var status = socket.HttpStatusCode;
                socket.Dispose();
                if (status == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    api.InvalidateToken(token);
                    continue;
                }

                if (status == HttpStatusCode.UpgradeRequired)
                {
                    throw api.ReportOutdated(inner: ex);
                }

                throw status switch
                {
                    HttpStatusCode.NotFound => CentralApiClient.Error(DiscordErrorKind.Rejected, "Este compartilhamento de tela já terminou.", ex, status),
                    HttpStatusCode.Forbidden => CentralApiClient.Error(DiscordErrorKind.Rejected, "Entre na sala do grupo para assistir.", ex, status),
                    HttpStatusCode.Conflict => CentralApiClient.Error(DiscordErrorKind.Rejected, "O compartilhamento não foi iniciado no servidor.", ex, status),
                    HttpStatusCode.Unauthorized => CentralApiClient.Error(DiscordErrorKind.Unauthorized, "A API central recusou a sessão.", ex, status),
                    _ => CentralApiClient.Error(DiscordErrorKind.Network, "Não foi possível abrir o canal de vídeo com a API central.", ex, status),
                };
            }
        }
    }

    /// <summary>
    /// Conecta direto ao computador de quem compartilha numa sala local, tentando todos os endereços ao mesmo tempo;
    /// fica com o primeiro que responder.
    /// </summary>
    public static async Task<MediaSocket> ConnectDirectAsync(
        IReadOnlyList<string> endpoints, Guid mediaId, string key, int bitrateKbps, string sharerName, CancellationToken cancellationToken)
    {
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempts.CancelAfter(TimeSpan.FromSeconds(6));
        var pending = endpoints.Select(endpoint => TryConnectDirectAsync(endpoint, mediaId, key, attempts.Token)).ToList();
        ClientWebSocket? winner = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (await done.ConfigureAwait(false) is { } socket)
            {
                winner = socket;
                break;
            }
        }

        // As tentativas que ainda estão abrindo são canceladas e descartadas.
        attempts.Cancel();
        foreach (var other in pending)
        {
            _ = other.ContinueWith(t => t.Result?.Dispose(), TaskScheduler.Default);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return winner is not null
            ? new MediaSocket(winner, bitrateKbps, $"com o computador de {sharerName}")
            : throw new Local.LocalNetworkException(
                $"Não foi possível conectar direto ao computador de {sharerName}. Confira se vocês estão na mesma rede (LAN, Hamachi, Radmin ou Tailscale) " +
                "e se o Firewall do Windows de quem compartilha liberou o ScreenCord.");
    }

    private static async Task<ClientWebSocket?> TryConnectDirectAsync(string endpoint, Guid mediaId, string key, CancellationToken cancellationToken)
    {
        if (!Local.LocalNetwork.TryParseAddress(endpoint, out var host, out var port))
        {
            return null;
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(Local.LocalMediaHost.KeyHeader, key);
        socket.Options.SetRequestHeader(ClientVersionContract.Header, AppVersion.Current);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{host}:{port}/media/{mediaId}"), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>Começa a enviar e a receber. Chamado depois de assinar os eventos, para nenhum pacote se perder.</summary>
    public void Start()
    {
        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Quem assiste pede um quadro-chave (ex.: não conseguiu decodificar a imagem).</summary>
    public void RequestKeyframe() => SendControl(MediaContract.KeyframeRequest);

    /// <summary>Quem assiste para (ou volta a receber) o vídeo, mantendo o áudio, ex.: ao minimizar a janela.</summary>
    public void SetVideoPaused(bool paused) => SendControl(paused ? MediaContract.PauseVideo : MediaContract.ResumeVideo);

    private void SendControl(byte type)
    {
        var packet = new byte[MediaContract.HeaderSize];
        MediaContract.WriteHeader(packet, type, 0, 0);
        if (_outgoing.Writer.TryWrite(packet))
        {
            Interlocked.Add(ref _queuedBytes, packet.Length);
        }
    }

    /// <summary>Enfileira um quadro de vídeo. Se a rede não acompanha, descarta até o próximo quadro-chave e pede um.</summary>
    public void SendVideo(EncodedFrame frame, long timestampMicroseconds)
    {
        if (frame.IsKeyframe)
        {
            _waitingForKeyframe = false;
        }
        else if (_waitingForKeyframe)
        {
            return;
        }

        if (Interlocked.Read(ref _queuedBytes) + frame.Data.Length > _queueLimitBytes)
        {
            _waitingForKeyframe = true;
            Interlocked.Increment(ref _videoOverflows);
            KeyframeRequested?.Invoke();
            return;
        }

        Enqueue(MediaContract.Video, frame.IsKeyframe ? MediaContract.KeyframeFlag : (byte)0, timestampMicroseconds, frame.Data);
    }

    public void SendAudio(byte[] opus, long timestampMicroseconds)
    {
        if (Interlocked.Read(ref _queuedBytes) + opus.Length <= _queueLimitBytes * 2)
        {
            Enqueue(MediaContract.Audio, 0, timestampMicroseconds, opus);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        _outgoing.Writer.TryComplete();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(_sendLoop, _receiveLoop).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _socket.Dispose();
        _lifetime.Dispose();
    }

    private void Enqueue(byte type, byte flags, long timestamp, byte[] payload)
    {
        var packet = new byte[MediaContract.HeaderSize + payload.Length];
        MediaContract.WriteHeader(packet, type, flags, timestamp);
        payload.CopyTo(packet, MediaContract.HeaderSize);
        Interlocked.Add(ref _queuedBytes, packet.Length);
        if (!_outgoing.Writer.TryWrite(packet))
        {
            Interlocked.Add(ref _queuedBytes, -packet.Length);
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var packet in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes, -packet.Length);
                await _socket.SendAsync(packet, WebSocketMessageType.Binary, true, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (WebSocketException)
        {
            RaiseClosed($"A conexão de vídeo {_peer} caiu.");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[256 * 1024];
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        if (buffer.Length >= MediaContract.MaxPacketSize)
                        {
                            RaiseClosed($"A conexão de vídeo {_peer} enviou um pacote grande demais.");
                            return;
                        }

                        Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MediaContract.MaxPacketSize));
                    }

                    result = await _socket.ReceiveAsync(buffer.AsMemory(length), _lifetime.Token).ConfigureAwait(false);
                    length += result.Count;
                }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    RaiseClosed(null);
                    return;
                }

                if (!MediaContract.TryReadHeader(buffer.AsSpan(0, length), out var type, out var flags, out var timestamp))
                {
                    continue;
                }

                if (type == MediaContract.KeyframeRequest)
                {
                    KeyframeRequested?.Invoke();
                }
                else
                {
                    PacketReceived?.Invoke(type, flags, timestamp, buffer.AsMemory(MediaContract.HeaderSize, length - MediaContract.HeaderSize));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (WebSocketException)
        {
            RaiseClosed($"A conexão de vídeo {_peer} caiu.");
        }
    }

    private void RaiseClosed(string? reason)
    {
        if (!_disposing && Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke(reason);
        }
    }
}
