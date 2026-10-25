using ScreenCord.Localization;
using System.Collections.Concurrent;
using System.Net;
using ScreenCord.Contracts;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Services;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Local;

/// <summary>Um endereço para entrar na sala pelo IP, com a rede dele ("Hamachi", "Rede local"...).</summary>
public sealed record LocalAddress(string Address, string Network);

/// <summary>Como a sala local aparece para quem está nela.</summary>
/// <param name="Addresses">
/// Por onde entrar na sala pelo IP: para quem hospeda, todos os endereços de rede local e VPN deste computador;
/// para os outros, o endereço em que a sala foi encontrada.
/// </param>
public sealed record LocalRoomInfo(bool IsHost, IReadOnlyList<LocalAddress> Addresses);

/// <summary>
/// Salas locais: funcionam sem a API (e sem conta do Discord). Uma sala é hospedada no computador de quem a cria;
/// os outros a encontram pelos anúncios na rede ou entram pelo IP. A mídia vai direto de quem compartilha para quem
/// assiste, até <see cref="StreamLimits.Direct"/>.
/// </summary>
public interface ILocalRooms
{
    /// <summary>Quem este computador é nas salas locais.</summary>
    LocalMember Me { get; }

    /// <summary>Conta local, sem Discord nem API: só as salas locais funcionam.</summary>
    bool IsLocalOnly { get; set; }

    Guid? CurrentRoomId { get; }

    /// <summary>Salas visíveis agora: as anunciadas na rede, a hospedada aqui e a em que você está.</summary>
    IReadOnlyList<GroupDto> Rooms { get; }

    /// <summary>Sala nova ou alterada (vem de threads de fundo).</summary>
    event Action<GroupDto>? RoomChanged;

    event Action<Guid>? RoomRemoved;

    /// <summary>Presença da sala em que você está.</summary>
    event Action<GroupPresenceDto>? PresenceChanged;

    /// <summary>A sala em que você estava acabou (quem hospedava saiu, a conexão caiu); o texto é o motivo.</summary>
    event Action<Guid, string>? RoomEnded;

    void SetIdentity(string name, string? discordId = null, string? avatarHash = null);

    bool IsLocal(Guid groupId);

    LocalRoomInfo? Info(Guid roomId);

    void Start();

    Task StopAsync();

    /// <summary>Cria e hospeda uma sala neste computador. Ainda é preciso entrar nela (<see cref="JoinAsync"/>).</summary>
    Task<GroupDto> CreateRoomAsync(string name);

    /// <summary>Encontra a sala hospedada em <paramref name="address"/> (ex.: "25.1.2.3") e a deixa pronta para entrar.</summary>
    Task<GroupDto> FindAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Entra na sala (saindo da sala local anterior; se ela era hospedada aqui, ela acaba).</summary>
    Task<GroupPresenceDto> JoinAsync(Guid roomId, CancellationToken cancellationToken = default);

    Task LeaveAsync();

    GroupDetails Details(Guid roomId);

    /// <summary>
    /// Muda o que a sala hospedada aqui aceita transmitir (só quem hospeda). O que a sala deixa de aceitar termina na
    /// hora, inclusive o compartilhamento deste computador.
    /// </summary>
    Task<GroupSettings> UpdateSettingsAsync(Guid roomId, GroupSettings settings);

    /// <summary>Anuncia a transmissão (tela ou câmera) na sala atual, com os endereços deste computador.</summary>
    Task<ScreenShareDto> StartShareAsync(ScreenShareStart start, CancellationToken cancellationToken = default);

    /// <summary>Encerra a transmissão desse tipo (a outra, tela ou câmera, continua).</summary>
    Task StopShareAsync(ShareKind kind);

    /// <summary>Para onde o encoder envia a mídia da transmissão anunciada desse tipo.</summary>
    IMediaSink OpenPublisher(ShareKind kind);

    /// <summary>Conecta direto ao computador de quem transmite (a tela ou a câmera) na sala atual.</summary>
    Task<MediaSocket> WatchAsync(Guid roomId, Guid sharerId, ShareKind kind, CancellationToken cancellationToken = default);
}

public sealed class LocalRooms : ILocalRooms, IAsyncDisposable, IDisposable
{
    private readonly ILocalIdentityStore _identity;
    private readonly ILogger<LocalRooms> _logger;
    private readonly RoomDiscovery _discovery;
    private readonly int _port;
    private readonly Func<int, IReadOnlyList<string>> _endpoints;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private LocalMember _me;
    private LocalServer? _server;
    private LocalCertificate? _certificate;
    private LocalRoomHost? _hosted;
    private LocalRoomClient? _client;
    private LocalRoomClient? _pending;
    private Guid? _currentRoomId;
    // Um canal de mídia por tipo de transmissão (tela e câmera); a rota de mídia lê de threads de fundo.
    private readonly ConcurrentDictionary<ShareKind, LocalMediaHost> _media = new();
    private bool _started;

    /// <param name="port">Porta TCP das salas; 0 escolhe uma livre (testes).</param>
    /// <param name="endpoints">Endereços anunciados para quem assiste, pela porta (padrão: os de rede local deste computador).</param>
    public LocalRooms(
        ILocalIdentityStore identity,
        ILogger<LocalRooms> logger,
        RoomDiscovery? discovery = null,
        int port = LocalNetwork.RoomPort,
        Func<int, IReadOnlyList<string>>? endpoints = null)
    {
        _identity = identity;
        _logger = logger;
        _discovery = discovery ?? new RoomDiscovery(logger);
        _port = port;
        _endpoints = endpoints ?? (p => LocalNetwork.Endpoints(p));
        var profile = identity.Load();
        _me = new LocalMember(profile.Id, profile.Name ?? "Você");
        _discovery.RoomSeen += room =>
        {
            if (room.Beacon.RoomId != _hosted?.Id && room.Beacon.RoomId != _currentRoomId)
            {
                RoomChanged?.Invoke(ToGroup(room.Beacon));
            }
        };
        _discovery.RoomLost += id =>
        {
            if (id != _currentRoomId && id != _hosted?.Id && id != _pending?.State.Id)
            {
                RoomRemoved?.Invoke(id);
            }
        };
    }

    public LocalMember Me => _me;

    public bool IsLocalOnly { get; set; }

    public Guid? CurrentRoomId => _currentRoomId;

    /// <summary>Porta em que as salas e a mídia deste computador escutam (depois de hospedar ou compartilhar).</summary>
    public int? ListeningPort => _server?.Port;

    /// <summary>Impressão digital do certificado das salas e da mídia deste computador (enquanto o servidor está aberto).</summary>
    public string? Fingerprint => _certificate?.Fingerprint;

    public event Action<GroupDto>? RoomChanged;

    public event Action<Guid>? RoomRemoved;

    public event Action<GroupPresenceDto>? PresenceChanged;

    public event Action<Guid, string>? RoomEnded;

    public IReadOnlyList<GroupDto> Rooms
    {
        get
        {
            var rooms = new Dictionary<Guid, GroupDto>();
            foreach (var room in _discovery.Rooms)
            {
                rooms[room.Beacon.RoomId] = ToGroup(room.Beacon);
            }

            if (_pending is { } pending)
            {
                rooms[pending.State.Id] = pending.State.ToGroup();
            }

            if (_client is { } client)
            {
                rooms[client.State.Id] = client.State.ToGroup();
            }

            if (_hosted is { } hosted)
            {
                rooms[hosted.Id] = hosted.Snapshot().ToGroup();
            }

            return [.. rooms.Values];
        }
    }

    public void SetIdentity(string name, string? discordId = null, string? avatarHash = null)
    {
        var member = new LocalMember(_me.Id, name, discordId, avatarHash).Sanitize();
        if (member is not null)
        {
            _me = member;
        }
    }

    public bool IsLocal(Guid groupId) =>
        groupId == _hosted?.Id || groupId == _client?.State.Id || groupId == _pending?.State.Id ||
        _discovery.Rooms.Any(r => r.Beacon.RoomId == groupId);

    public LocalRoomInfo? Info(Guid roomId)
    {
        if (_hosted is { } hosted && hosted.Id == roomId)
        {
            var port = _server?.Port ?? _port;
            var addresses = LocalNetwork.Interfaces()
                .Select(i => i.Address)
                .Distinct()
                .Select(a => new LocalAddress(LocalNetwork.FormatAddress(a.ToString(), port), LocalNetwork.NetworkName(a)))
                .ToList();
            return new LocalRoomInfo(true, addresses);
        }

        var client = _client?.State.Id == roomId ? _client : _pending?.State.Id == roomId ? _pending : null;
        if (client is not null)
        {
            return new LocalRoomInfo(false, [Describe(client.Host, client.Port)]);
        }

        return _discovery.Rooms.FirstOrDefault(r => r.Beacon.RoomId == roomId) is { } found
            ? new LocalRoomInfo(false, [Describe(found.Host, found.Beacon.Port)])
            : null;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _discovery.Start();
    }

    public async Task StopAsync()
    {
        await LeaveAsync();
        if (_pending is { } pending)
        {
            _pending = null;
            await pending.DisposeAsync();
        }

        _discovery.Stop();
        _started = false;
        if (_server is { } server)
        {
            _server = null;
            await server.DisposeAsync();
        }

        _certificate?.Dispose();
        _certificate = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// Ao fechar o app (o contêiner descarta de forma síncrona): para de anunciar e de escutar sem esperar. Quem
    /// estava na sala hospedada aqui percebe a conexão fechando e vê que a sala acabou.
    /// </summary>
    public void Dispose()
    {
        _discovery.Stop();
        foreach (var media in _media.Values)
        {
            media.Stop();
        }

        _ = _hosted?.CloseAsync(Strings.HostLeftRoomClosed);
        _ = _client?.DisposeAsync().AsTask();
        _ = _pending?.DisposeAsync().AsTask();
        _ = _server?.DisposeAsync().AsTask().ContinueWith(_ => _certificate?.Dispose(), TaskScheduler.Default);
    }

    public async Task<GroupDto> CreateRoomAsync(string name)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureServer();
            if (_hosted is not null)
            {
                throw new LocalNetworkException(Strings.AlreadyHosting);
            }

            var host = new LocalRoomHost(Guid.NewGuid(), name, _me, _logger);
            host.Changed += state =>
            {
                if (_currentRoomId == state.Id)
                {
                    PresenceChanged?.Invoke(state.ToPresence());
                }

                RoomChanged?.Invoke(state.ToGroup());
            };
            _hosted = host;
            _discovery.Announce(() => _hosted is { } h ? ToBeacon(h.Snapshot()) : null);
            _logger.LogInformation("Sala local {Room} criada na porta {Port}.", host.Id, _server!.Port);
            return host.Snapshot().ToGroup();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GroupDto> FindAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!LocalNetwork.TryParseAddress(address, out var host, out var port))
        {
            throw new LocalNetworkException(Strings.InvalidAddress);
        }

        var client = await LocalRoomClient.ConnectAsync(host, port, _me, fingerprint: null, cancellationToken);
        if (client.State.Id == _hosted?.Id)
        {
            await client.DisposeAsync();
            throw new LocalNetworkException(Strings.OwnRoomAddress);
        }

        var previous = Interlocked.Exchange(ref _pending, client);
        if (previous is not null)
        {
            await previous.DisposeAsync();
        }

        Attach(client);
        return client.State.ToGroup();
    }

    public async Task<GroupPresenceDto> JoinAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_currentRoomId == roomId)
            {
                return CurrentPresence() ?? throw new LocalNetworkException(Strings.RoomNoLongerAvailable);
            }

            if (_hosted is { } hosted && hosted.Id == roomId)
            {
                await LeaveLockedAsync();
                _currentRoomId = roomId;
                return hosted.Snapshot().ToPresence();
            }

            LocalRoomClient client;
            if (_pending is { } pending && pending.State.Id == roomId)
            {
                _pending = null;
                client = pending;
            }
            else if (_discovery.Rooms.FirstOrDefault(r => r.Beacon.RoomId == roomId) is { } found)
            {
                client = await LocalRoomClient.ConnectAsync(found.Host, found.Beacon.Port, _me, found.Beacon.Fingerprint, cancellationToken);
                Attach(client);
            }
            else
            {
                throw new LocalNetworkException(Strings.RoomNoLongerOnNetwork);
            }

            await LeaveLockedAsync();
            _client = client;
            _currentRoomId = client.State.Id;
            return client.State.ToPresence();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LeaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await LeaveLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public GroupDetails Details(Guid roomId)
    {
        var state = _hosted?.Id == roomId ? _hosted.Snapshot()
            : _client?.State.Id == roomId ? _client.State
            : _pending?.State.Id == roomId ? _pending.State
            : _discovery.Rooms.FirstOrDefault(r => r.Beacon.RoomId == roomId) is { } found
                ? new LocalRoomState(roomId, found.Beacon.Name, Guid.Empty, found.Beacon.Version, found.Beacon.Members, [])
                : throw new LocalNetworkException(Strings.RoomNoLongerOnNetwork);

        var connected = _currentRoomId == roomId;
        var members = state.Members
            .Select(m => m.ToPresence())
            .Select(p => CentralApiClient.ToPerson(p.Id, p.DiscordId, p.Username, p.GlobalName, p.Discriminator, p.AvatarHash, RelationshipStatus.None))
            .OfType<Person>()
            .Select(p => new GroupMember(p, IsConnected: true, IsOwner: p.UserId == state.HostId))
            .ToList();
        var isHost = _hosted?.Id == roomId && connected;
        return new GroupDetails(state.Id, state.Name, members, state.Version, CanDelete: isHost, Settings: state.Settings ?? GroupSettings.Default, CanEdit: isHost);
    }

    public async Task<GroupSettings> UpdateSettingsAsync(Guid roomId, GroupSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (_hosted is not { } hosted || hosted.Id != roomId)
            {
                throw new LocalNetworkException(Strings.OnlyHostChangesRoom);
            }

            // As transmissões deste computador de um tipo que a sala deixou de aceitar terminam.
            foreach (var kind in hosted.UpdateSettings(settings))
            {
                if (_hosted?.Id == _currentRoomId)
                {
                    StopShareLocked(kind, NotAcceptedReason(kind));
                }
            }

            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScreenShareDto> StartShareAsync(ScreenShareStart start, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_currentRoomId is null)
            {
                throw new LocalNetworkException(Strings.JoinLocalRoomToShare);
            }

            if (!(CurrentState()?.Settings ?? GroupSettings.Default).Allows(start.Kind))
            {
                throw new LocalNetworkException(start.Kind == ShareKind.Camera ? Strings.GroupNoCamera : Strings.GroupNoScreenShare);
            }

            EnsureServer();
            var endpoints = _endpoints(_server!.Port);
            if (endpoints.Count == 0)
            {
                throw new LocalNetworkException(Strings.NoLocalNetwork);
            }

            // Trocar as configurações mantém o mesmo canal: quem assiste continua conectado. Tela e câmera têm um canal cada.
            if (!_media.TryGetValue(start.Kind, out var media))
            {
                media = new LocalMediaHost(start.BitrateKbps);
            }

            var share = new LocalShare(_me.Id, start.Width, start.Height, start.FrameRate, start.BitrateKbps, start.HasAudio, media.Id, endpoints, media.Key, _certificate!.Fingerprint, start.Kind);
            if (!share.IsValid)
            {
                throw new LocalNetworkException(Strings.ShareSettingsOutOfLimits);
            }

            _media[start.Kind] = media;

            if (_hosted is { } hosted && hosted.Id == _currentRoomId)
            {
                hosted.SetHostShare(share);
            }
            else
            {
                _client!.Share(share);
            }

            return share.ToDto();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopShareAsync(ShareKind kind)
    {
        await _gate.WaitAsync();
        try
        {
            StopShareLocked(kind);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IMediaSink OpenPublisher(ShareKind kind) =>
        _media.TryGetValue(kind, out var media) ? media.Acquire() : throw new LocalNetworkException(Strings.ShareNotAnnounced);

    public async Task<MediaSocket> WatchAsync(Guid roomId, Guid sharerId, ShareKind kind, CancellationToken cancellationToken = default)
    {
        var state = _currentRoomId == roomId ? CurrentState() : null;
        if (state?.Shares.FirstOrDefault(s => s.UserId == sharerId && s.Kind == kind) is not { } share)
        {
            throw new LocalNetworkException(Strings.ShareAlreadyEnded);
        }

        var name = state.Members.FirstOrDefault(m => m.Id == sharerId)?.Name ?? Strings.SomeoneSharing;
        return await MediaSocket.ConnectDirectAsync(share.Endpoints, share.MediaId, share.Key, share.Fingerprint, share.BitrateKbps, name, cancellationToken);
    }

    private LocalRoomState? CurrentState() =>
        _hosted?.Id == _currentRoomId ? _hosted?.Snapshot() : _client?.State;

    private GroupPresenceDto? CurrentPresence() => CurrentState()?.ToPresence();

    private void EnsureServer()
    {
        if (_server is not null)
        {
            return;
        }

        var certificate = LocalCertificate.Create();
        var server = new LocalServer(Route, certificate.Certificate, _logger);
        try
        {
            server.Start(_port);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }

        _certificate = certificate;
        _server = server;
    }

    private LocalRoute Route(LocalRequest request)
    {
        if (request.Path == "/room")
        {
            return _hosted is { } hosted ? LocalRoute.Accept(hosted.HandleAsync) : LocalRoute.NotFound;
        }

        const string media = "/media/";
        if (request.Path.StartsWith(media, StringComparison.Ordinal) && Guid.TryParse(request.Path[media.Length..], out var mediaId))
        {
            if (_media.Values.FirstOrDefault(m => m.Id == mediaId) is not { } host)
            {
                return LocalRoute.NotFound;
            }

            return host.AcceptsKey(request.Header(LocalMediaHost.KeyHeader)) ? LocalRoute.Accept(host.WatchAsync) : LocalRoute.Forbidden;
        }

        return LocalRoute.NotFound;
    }

    private void Attach(LocalRoomClient client)
    {
        client.Changed += state =>
        {
            if (_client == client)
            {
                // O anfitrião passou a não aceitar o que este computador transmite: o anúncio já saiu da sala.
                var settings = state.Settings ?? GroupSettings.Default;
                foreach (var kind in _media.Keys.Where(k => !settings.Allows(k)).ToList())
                {
                    _ = StopNotAcceptedShareAsync(kind);
                }

                PresenceChanged?.Invoke(state.ToPresence());
            }

            RoomChanged?.Invoke(state.ToGroup());
        };
        client.Closed += reason => _ = OnClientClosedAsync(client, reason);
    }

    private async Task OnClientClosedAsync(LocalRoomClient client, string reason)
    {
        var roomId = client.State.Id;
        var wasCurrent = false;
        await _gate.WaitAsync();
        try
        {
            if (_client == client)
            {
                StopAllSharesLocked();
                _client = null;
                _currentRoomId = null;
                wasCurrent = true;
            }
            else if (_pending == client)
            {
                _pending = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        _discovery.Forget(roomId);
        await client.DisposeAsync();
        RoomRemoved?.Invoke(roomId);
        if (wasCurrent)
        {
            RoomEnded?.Invoke(roomId, reason);
        }
    }

    /// <summary>Sai da sala atual; se ela era hospedada aqui, ela acaba para todos.</summary>
    private async Task LeaveLockedAsync()
    {
        StopAllSharesLocked();
        if (_client is { } client)
        {
            _client = null;
            await client.DisposeAsync();
        }

        if (_hosted is { } hosted && _currentRoomId == hosted.Id)
        {
            _hosted = null;
            _discovery.Announce(null);
            await hosted.CloseAsync(Strings.HostLeftRoomClosed);
            RoomRemoved?.Invoke(hosted.Id);
        }

        _currentRoomId = null;
    }

    private static string NotAcceptedReason(ShareKind kind) => kind == ShareKind.Camera ? Strings.GroupNoCamera : Strings.GroupNoScreenShare;

    /// <summary>A sala deixou de aceitar o que este computador transmite: para a captura, com o motivo.</summary>
    private async Task StopNotAcceptedShareAsync(ShareKind kind)
    {
        await _gate.WaitAsync();
        try
        {
            StopShareLocked(kind, NotAcceptedReason(kind));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StopAllSharesLocked()
    {
        foreach (var kind in _media.Keys.ToList())
        {
            StopShareLocked(kind);
        }
    }

    /// <param name="reason">Quando a sala (e não quem compartilha) encerrou, o motivo; o encoder avisa o aplicativo.</param>
    private void StopShareLocked(ShareKind kind, string? reason = null)
    {
        if (!_media.TryRemove(kind, out var media))
        {
            return;
        }

        media.Stop(reason);
        if (_hosted is { } hosted && hosted.Id == _currentRoomId)
        {
            hosted.RemoveHostShare(kind);
        }
        else
        {
            _client?.Unshare(kind);
        }
    }

    private static LocalAddress Describe(string host, int port) =>
        new(LocalNetwork.FormatAddress(host, port), IPAddress.TryParse(host, out var ip) ? LocalNetwork.NetworkName(ip) : Strings.ComputerName);

    private static GroupDto ToGroup(RoomBeacon beacon) =>
        new(beacon.RoomId, beacon.Name, beacon.Members.Count, beacon.Members.Select(m => m.ToPresence()).ToList(), beacon.Version, []);

    private RoomBeacon ToBeacon(LocalRoomState state) =>
        new(state.Id, state.Name, _server?.Port ?? _port, state.Version, state.Members, _certificate!.Fingerprint);
}
