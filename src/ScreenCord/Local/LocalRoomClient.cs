using ScreenCord.Localization;
using System.IO;
using System.Net;
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
    /// <param name="fingerprint">Certificado esperado do anfitrião (do anúncio ou do convite); null aceita qualquer um (entrada pelo IP).</param>
    /// <param name="token">Token da sala (do convite), exigido de quem conecta de fora das redes locais.</param>
    /// <param name="roomId">Sala esperada (do convite): se o anfitrião não a hospeda mais, falha com <see cref="LocalFailure.RoomClosed"/>.</param>
    public static async Task<LocalRoomClient> ConnectAsync(
        string host, int port, LocalMember me, string? fingerprint, CancellationToken cancellationToken, string? token = null, Guid? roomId = null)
    {
        var socket = await OpenAsync(host, port, fingerprint, token, roomId, cancellationToken).ConfigureAwait(false);
        return await HelloAsync(socket, host, port, me, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Entra pelo primeiro dos <paramref name="endpoints"/> que responder (o convite traz o IP público e os da rede
    /// local e das VPNs, e de cada lugar só alguns funcionam). As conexões abrem em paralelo, mas só a vencedora
    /// envia o <c>hello</c>: o anfitrião vê uma entrada só. Se todas falham, vale o motivo mais útil para quem tenta.
    /// </summary>
    public static async Task<LocalRoomClient> ConnectAnyAsync(
        IReadOnlyList<(string Host, int Port)> endpoints, LocalMember me, string fingerprint, string token, Guid roomId, CancellationToken cancellationToken)
    {
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = endpoints.Select(e => OpenAttemptAsync(e.Host, e.Port, fingerprint, token, roomId, attempts.Token)).ToList();
        (ClientWebSocket Socket, string Host, int Port)? winner = null;
        var failures = new List<LocalNetworkException>();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            var attempt = await done.ConfigureAwait(false);
            if (attempt.Socket is not null)
            {
                winner = (attempt.Socket, attempt.Host, attempt.Port);
                break;
            }

            if (attempt.Failure is not null)
            {
                failures.Add(attempt.Failure);
            }
        }

        // As tentativas que ainda estão abrindo são canceladas e descartadas.
        attempts.Cancel();
        foreach (var other in pending)
        {
            _ = other.ContinueWith(t => t.Result.Socket?.Dispose(), TaskScheduler.Default);
        }

        if (winner is not { } connected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw MostUseful(failures, endpoints);
        }

        return await HelloAsync(connected.Socket, connected.Host, connected.Port, me, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(ClientWebSocket? Socket, string Host, int Port, LocalNetworkException? Failure)> OpenAttemptAsync(
        string host, int port, string fingerprint, string token, Guid roomId, CancellationToken cancellationToken)
    {
        try
        {
            return (await OpenAsync(host, port, fingerprint, token, roomId, cancellationToken).ConfigureAwait(false), host, port, null);
        }
        catch (LocalNetworkException ex)
        {
            return (null, host, port, ex);
        }
        catch (OperationCanceledException)
        {
            return (null, host, port, null);
        }
    }

    /// <summary>
    /// A recusa explícita da sala explica mais que o silêncio: token, sala encerrada e bloqueio vêm antes do
    /// certificado diferente (outro computador naquele endereço), que vem antes de "não respondeu".
    /// </summary>
    private static LocalNetworkException MostUseful(List<LocalNetworkException> failures, IReadOnlyList<(string Host, int Port)> endpoints)
    {
        LocalFailure[] order = [LocalFailure.TokenRejected, LocalFailure.RoomClosed, LocalFailure.Blocked, LocalFailure.CertificateMismatch];
        foreach (var failure in order)
        {
            if (failures.FirstOrDefault(f => f.Failure == failure) is { } found)
            {
                return found;
            }
        }

        var addresses = string.Join(", ", endpoints.Select(e => $"{e.Host}:{e.Port}"));
        return new LocalNetworkException(
            string.Format(Loc.Culture, Strings.InviteUnreachableFormat, addresses, LocalNetwork.RoomPort), failures.FirstOrDefault(), LocalFailure.Unreachable);
    }

    /// <summary>Abre o WebSocket da sala (TLS e o pedido de conexão), sem entrar ainda.</summary>
    private static async Task<ClientWebSocket> OpenAsync(string host, int port, string? fingerprint, string? token, Guid? roomId, CancellationToken cancellationToken)
    {
        var rejected = false;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader(ClientVersionContract.Header, AppVersion.Current);
        if (token is not null)
        {
            socket.Options.SetRequestHeader(LocalRoomHost.TokenHeader, token);
        }

        if (roomId is { } id)
        {
            socket.Options.SetRequestHeader(LocalRoomHost.RoomHeader, id.ToString("N"));
        }

        socket.Options.RemoteCertificateValidationCallback = LocalCertificate.Validator(fingerprint, () => rejected = true);
        var address = $"{host}:{port}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new Uri($"wss://{FormatHost(host)}:{port}/room"), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception ex) when (rejected && ex is WebSocketException or OperationCanceledException or IOException)
        {
            socket.Dispose();
            var format = roomId is null ? Strings.CertificateMismatchFormat : Strings.InviteCertificateMismatchFormat;
            throw new LocalNetworkException(string.Format(Loc.Culture, format, address), ex, LocalFailure.CertificateMismatch);
        }
        catch (WebSocketException ex) when (socket.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            // Sem token (entrada pelo IP) é quem está fora das redes locais de quem hospeda; com token, o convite foi trocado.
            socket.Dispose();
            throw new LocalNetworkException(roomId is null ? Strings.InviteRequired : Strings.InviteTokenRejected, ex, LocalFailure.TokenRejected);
        }
        catch (WebSocketException ex) when (roomId is not null && socket.HttpStatusCode == HttpStatusCode.NotFound)
        {
            socket.Dispose();
            throw new LocalNetworkException(Strings.InviteRoomClosed, ex, LocalFailure.RoomClosed);
        }
        catch (WebSocketException ex) when (socket.HttpStatusCode == HttpStatusCode.TooManyRequests)
        {
            socket.Dispose();
            throw new LocalNetworkException(Strings.InviteBlocked, ex, LocalFailure.Blocked);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            socket.Dispose();
            throw new LocalNetworkException(string.Format(Loc.Culture, Strings.JoinRoomFailedFormat, address), ex, LocalFailure.Unreachable);
        }
    }

    /// <summary>Envia o <c>hello</c> e espera o primeiro estado da sala.</summary>
    private static async Task<LocalRoomClient> HelloAsync(ClientWebSocket socket, string host, int port, LocalMember me, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await LocalMessage.SendAsync(socket, new LocalMessage(LocalMessage.Hello, Member: me), timeout.Token).ConfigureAwait(false);
            var first = await LocalMessage.ReceiveAsync(socket, timeout.Token).ConfigureAwait(false);
            if (first is { Type: LocalMessage.Closed })
            {
                var failure = first.Reason == Strings.HostOtherVersion ? LocalFailure.ProtocolMismatch : LocalFailure.Other;
                throw new LocalNetworkException(first.Reason ?? Strings.RoomRefusedEntry, failure: failure);
            }

            if (first is not { Type: LocalMessage.State, Room: { } room } || Sanitize(room) is not { } state)
            {
                throw new LocalNetworkException(Strings.NotAScreenCordRoom, failure: LocalFailure.Unreachable);
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
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException or IOException)
        {
            socket.Dispose();
            throw new LocalNetworkException(string.Format(Loc.Culture, Strings.JoinRoomFailedFormat, $"{host}:{port}"), ex, LocalFailure.Unreachable);
        }
    }

    public void Share(LocalShare share) => _outgoing.Writer.TryWrite(new LocalMessage(LocalMessage.ShareStart, Share: share));

    /// <summary>Encerra a transmissão desse tipo (a outra continua).</summary>
    public void Unshare(ShareKind kind) => _outgoing.Writer.TryWrite(new LocalMessage(LocalMessage.ShareStop, Kind: kind));

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
        string? reason = null;
        try
        {
            while (await LocalMessage.ReceiveAsync(_socket, _lifetime.Token).ConfigureAwait(false) is { } message)
            {
                if (message.Type == LocalMessage.Closed)
                {
                    reason = message.Reason ?? Strings.RoomClosed;
                    break;
                }

                if (message is { Type: LocalMessage.State, Room: { } room } && Sanitize(room) is { } state && state.Version > State.Version)
                {
                    State = state;
                    Changed?.Invoke(state);
                }
            }

            reason ??= Strings.HostLeftRoomClosed;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidDataException or IOException)
        {
        }

        if (!_disposing && Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke(reason ?? Strings.RoomConnectionDropped);
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
        var shares = (room.Shares ?? []).Where(s => s is not null && s.IsValid && ids.Contains(s.UserId)).DistinctBy(s => (s.UserId, s.Kind)).OrderBy(s => s.Kind).ToList();
        return room with
        {
            Name = name.Length > Contracts.GroupsHubContract.MaxNameLength ? name[..Contracts.GroupsHubContract.MaxNameLength] : name,
            Members = members,
            Shares = shares,
        };
    }

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;
}
