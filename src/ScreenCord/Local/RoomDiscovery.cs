using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Local;

/// <summary>Anúncio de uma sala local, enviado por broadcast UDP a cada poucos segundos.</summary>
/// <param name="Fingerprint">Impressão digital do certificado do anfitrião: quem entra pela lista só aceita esse.</param>
public sealed record RoomBeacon(
    Guid RoomId,
    string Name,
    int Port,
    long Version,
    IReadOnlyList<LocalMember> Members,
    string Fingerprint,
    string Kind = RoomBeacon.ScreenCordKind,
    int Protocol = LocalMessage.CurrentProtocol)
{
    public const string ScreenCordKind = "screencord-room";

    public static byte[] Serialize(RoomBeacon beacon) => JsonSerializer.SerializeToUtf8Bytes(beacon, LocalMessage.Json);

    /// <summary>Lê um anúncio; null para qualquer coisa que não seja um anúncio válido do ScreenCord.</summary>
    public static RoomBeacon? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            var beacon = JsonSerializer.Deserialize<RoomBeacon>(data, LocalMessage.Json);
            if (beacon is not { Kind: ScreenCordKind, Protocol: LocalMessage.CurrentProtocol } || beacon.Port is < 1 or > 65535 ||
                !LocalCertificate.IsValidFingerprint(beacon.Fingerprint))
            {
                return null;
            }

            var state = LocalRoomClient.Sanitize(new LocalRoomState(beacon.RoomId, beacon.Name, Guid.Empty, beacon.Version, beacon.Members ?? [], []));
            return state is null ? null : beacon with { Name = state.Name, Members = state.Members };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Sala encontrada na rede: o endereço é o de quem enviou o anúncio.</summary>
public sealed record DiscoveredRoom(RoomBeacon Beacon, string Host, DateTimeOffset LastSeen);

/// <summary>
/// Descobre as salas locais da rede e anuncia a sala hospedada aqui. O anúncio vai para o broadcast de cada
/// interface (LAN, Hamachi, Radmin): o broadcast geral (255.255.255.255) sai só pela interface principal do Windows.
/// O Tailscale não repassa broadcast; ali se entra pelo IP.
/// </summary>
public sealed class RoomDiscovery : IDisposable
{
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Expiration = TimeSpan.FromSeconds(7);

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly int _port;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, DiscoveredRoom> _rooms = [];
    private UdpClient? _listener;
    private UdpClient? _sender;
    private CancellationTokenSource? _lifetime;
    private ITimer? _timer;
    private Func<RoomBeacon?>? _announce;

    public RoomDiscovery(ILogger logger, TimeProvider? time = null, int port = LocalNetwork.DiscoveryPort)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _port = port;
    }

    /// <summary>Sala nova ou atualizada (vem de uma thread de fundo).</summary>
    public event Action<DiscoveredRoom>? RoomSeen;

    /// <summary>Sala que parou de se anunciar.</summary>
    public event Action<Guid>? RoomLost;

    public IReadOnlyList<DiscoveredRoom> Rooms
    {
        get
        {
            lock (_lock)
            {
                return [.. _rooms.Values];
            }
        }
    }

    /// <summary>Começa a ouvir os anúncios (e a anunciar, quando houver sala hospedada aqui).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_lifetime is not null)
            {
                return;
            }

            _lifetime = new CancellationTokenSource();
        }

        try
        {
            var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            listener.EnableBroadcast = true;
            _listener = listener;
            _ = Task.Run(() => ListenAsync(listener, _lifetime.Token));
        }
        catch (SocketException ex)
        {
            // Sem ouvir os anúncios, ainda dá para entrar numa sala pelo IP.
            _logger.LogWarning("Não foi possível ouvir os anúncios de salas na porta UDP {Port} ({Error}).", _port, ex.SocketErrorCode);
        }

        _sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _timer = _time.CreateTimer(_ => Tick(), null, AnnounceInterval, AnnounceInterval);
    }

    /// <summary>O que anunciar a cada intervalo (null: nada hospedado aqui).</summary>
    public void Announce(Func<RoomBeacon?>? beacon)
    {
        _announce = beacon;
        if (beacon is not null)
        {
            Tick();
        }
    }

    /// <summary>Esquece uma sala (ex.: ela foi encerrada), até o próximo anúncio dela.</summary>
    public void Forget(Guid roomId)
    {
        lock (_lock)
        {
            _rooms.Remove(roomId);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? lifetime;
        lock (_lock)
        {
            lifetime = _lifetime;
            _lifetime = null;
            _rooms.Clear();
        }

        lifetime?.Cancel();
        _timer?.Dispose();
        _timer = null;
        _listener?.Dispose();
        _listener = null;
        _sender?.Dispose();
        _sender = null;
        _announce = null;
        lifetime?.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Registra um anúncio recebido (separado da rede para os testes).</summary>
    public void Receive(ReadOnlySpan<byte> data, IPAddress sender)
    {
        if (RoomBeacon.Parse(data) is not { } beacon)
        {
            return;
        }

        if (_announce?.Invoke() is { } own && own.RoomId == beacon.RoomId)
        {
            return;
        }

        var room = new DiscoveredRoom(beacon, (sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender).ToString(), _time.GetUtcNow());
        lock (_lock)
        {
            // Uma sala pode chegar por mais de uma interface; fica o endereço em que ela foi vista primeiro.
            if (_rooms.TryGetValue(beacon.RoomId, out var known) && known.Beacon.Version > beacon.Version)
            {
                return;
            }

            _rooms[beacon.RoomId] = known is null ? room : room with { Host = known.Host };
        }

        RoomSeen?.Invoke(room);
    }

    /// <summary>Remove as salas que pararam de se anunciar (separado para os testes).</summary>
    public void Expire()
    {
        var now = _time.GetUtcNow();
        List<Guid> lost;
        lock (_lock)
        {
            lost = _rooms.Values.Where(r => now - r.LastSeen > Expiration).Select(r => r.Beacon.RoomId).ToList();
            foreach (var id in lost)
            {
                _rooms.Remove(id);
            }
        }

        foreach (var id in lost)
        {
            RoomLost?.Invoke(id);
        }
    }

    private void Tick()
    {
        Expire();
        if (_announce?.Invoke() is not { } beacon || _sender is not { } sender)
        {
            return;
        }

        var data = RoomBeacon.Serialize(beacon);
        foreach (var (_, broadcast) in LocalNetwork.Interfaces())
        {
            try
            {
                sender.Send(data, data.Length, new IPEndPoint(broadcast, _port));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.LogDebug("Falha ao anunciar a sala em {Broadcast}.", broadcast);
            }
        }
    }

    private async Task ListenAsync(UdpClient listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await listener.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length <= LocalMessage.MaxBytes && LocalNetwork.IsLocal(result.RemoteEndPoint.Address))
                {
                    Receive(result.Buffer, result.RemoteEndPoint.Address);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Ex.: ICMP "porta inacessível" de um anúncio anterior; segue ouvindo.
            }
        }
    }
}
