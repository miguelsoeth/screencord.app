using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;

namespace ScreenCord.Local;

/// <summary>Conexão com uma sala local hospedada em outro computador.</summary>
public sealed class LocalRoomClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly ClientWebSocket _socket;
    private readonly Channel<LocalMessage> _outgoing = Channel.CreateUnbounded<LocalMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;
    private int _closedRaised;
    private volatile bool _disposing;

    private LocalRoomClient(ClientWebSocket socket, string host, int port, LocalRoomState state)
    {
        _socket = socket;
        Host = host;
        Port = port;
        State = state;
    }

    public string Host { get; }

    public int Port { get; }

    public LocalRoomState State { get; private set; }

    /// <summary>Estado novo enviado pelo anfitrião, na thread de leitura.</summary>
    public event Action<LocalRoomState>? Changed;

    /// <summary>A sala acabou ou a conexão caiu; o argumento é o motivo para exibir.</summary>
    public event Action<string>? Closed;

    /// <summary>Entra na sala de <paramref name="host"/>:<paramref name="port"/> e espera o primeiro estado.</summary>
    /// <param name="fingerprint">Certificado esperado do anfitrião (do anúncio da sala); null aceita qualquer um (entrada pelo IP).</param>
    public static async Task<LocalRoomClient> ConnectAsync(string host, int port, LocalMember me, string? fingerprint, CancellationToken cancellationToken)
    {
        var rejected = false;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.SetRequestHeader(ClientVersionContract.Header, AppVersion.Current);
        socket.Options.RemoteCertificateValidationCallback = LocalCertificate.Validator(fingerprint, () => rejected = true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new Uri($"wss://{FormatHost(host)}:{port}/room"), timeout.Token).ConfigureAwait(false);
            await LocalMessage.SendAsync(socket, new LocalMessage(LocalMessage.Hello, Member: me), timeout.Token).ConfigureAwait(false);
            var first = await LocalMessage.ReceiveAsync(socket, timeout.Token).ConfigureAwait(false);
            if (first is { Type: LocalMessage.Closed })
            {
                throw new LocalNetworkException(first.Reason ?? "A sala recusou a entrada.");
            }

            if (first is not { Type: LocalMessage.State, Room: { } room } || Sanitize(room) is not { } state)
            {
                throw new LocalNetworkException("O outro computador não respondeu como uma sala do ScreenCord.");
            }

            var client = new LocalRoomClient(socket, host, port, state);
            client._sendLoop = Task.Run(client.SendLoopAsync);
            client._receiveLoop = Task.Run(client.ReceiveLoopAsync);
            return client;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (LocalNetworkException)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception ex) when (rejected && ex is WebSocketException or OperationCanceledException or IOException)
        {
            socket.Dispose();
            throw new LocalNetworkException(
                $"O computador em {host}:{port} não é o que anunciou a sala (o certificado não confere). " +
                "Peça o endereço para quem hospeda e entre pelo IP.", ex);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException or IOException)
        {
            socket.Dispose();
            throw new LocalNetworkException(
                $"Não foi possível entrar na sala em {host}:{port}. Confira o endereço, se vocês estão na mesma rede (LAN, Hamachi, Radmin ou Tailscale) " +
                "e se o Firewall do Windows de quem hospeda liberou o ScreenCord.", ex);
        }
    }

    public void Share(LocalShare share) => _outgoing.Writer.TryWrite(new LocalMessage(LocalMessage.ShareStart, Share: share));

    public void Unshare() => _outgoing.Writer.TryWrite(new LocalMessage(LocalMessage.ShareStop));

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        _outgoing.Writer.TryComplete();
        try
        {
            await _sendLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await LocalMessage.CloseAsync(_socket);
        _lifetime.Cancel();
        try
        {
            await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _socket.Dispose();
        _lifetime.Dispose();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await LocalMessage.SendAsync(_socket, message, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
        }
    }

    private async Task ReceiveLoopAsync()
    {
        string reason = "A conexão com a sala caiu.";
        try
        {
            while (await LocalMessage.ReceiveAsync(_socket, _lifetime.Token).ConfigureAwait(false) is { } message)
            {
                if (message.Type == LocalMessage.Closed)
                {
                    reason = message.Reason ?? "A sala foi encerrada.";
                    break;
                }

                if (message is { Type: LocalMessage.State, Room: { } room } && Sanitize(room) is { } state && state.Version > State.Version)
                {
                    State = state;
                    Changed?.Invoke(state);
                }
            }

            if (reason == "A conexão com a sala caiu.")
            {
                reason = "Quem hospedava saiu, e a sala foi encerrada.";
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidDataException or IOException)
        {
        }

        if (!_disposing && Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke(reason);
        }
    }

    /// <summary>O estado vem de outro computador: integrantes e compartilhamentos inválidos ficam de fora.</summary>
    public static LocalRoomState? Sanitize(LocalRoomState room)
    {
        var name = string.Join(' ', (room.Name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (room.Id == Guid.Empty || name.Length == 0)
        {
            return null;
        }

        var members = (room.Members ?? [])
            .Select(m => m?.Sanitize())
            .OfType<LocalMember>()
            .DistinctBy(m => m.Id)
            .Take(LocalRoomHost.MaxMembers)
            .ToList();
        var ids = members.Select(m => m.Id).ToHashSet();
        var shares = (room.Shares ?? []).Where(s => s is not null && s.IsValid && ids.Contains(s.UserId)).DistinctBy(s => s.UserId).ToList();
        return room with
        {
            Name = name.Length > Contracts.GroupsHubContract.MaxNameLength ? name[..Contracts.GroupsHubContract.MaxNameLength] : name,
            Members = members,
            Shares = shares,
        };
    }

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;
}
