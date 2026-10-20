using ScreenCord.Contracts;
using ScreenCord.Services;

namespace ScreenCord.Local;

/// <summary>A conexão em tempo real com a API (o hub, ou a do mockup), separada da que junta as salas locais.</summary>
public sealed record ApiRealtime(IGroupsRealtime Inner);

/// <summary>
/// Junta os grupos da API e as salas locais numa conexão só para a tela de grupos: entrar, sair e anunciar um
/// compartilhamento vão para a API ou para a sala local, conforme o grupo. Numa sala local o estado é sempre
/// "conectado", mesmo com a API fora do ar; na conta local, a API nem é usada.
/// </summary>
public sealed class RoutedGroupsRealtime : IGroupsRealtime
{
    private readonly IGroupsRealtime _api;
    private readonly ILocalRooms _local;
    private readonly Lock _lock = new();
    private RealtimeState _state;
    private bool _apiStarted;

    public RoutedGroupsRealtime(ApiRealtime api, ILocalRooms local)
    {
        _api = api.Inner;
        _local = local;
        _state = Effective();
        _api.StateChanged += (_, _) => RaiseStateIfChanged();
        _api.PresenceChanged += (_, presence) => PresenceChanged?.Invoke(this, presence);
        _api.GroupChanged += (_, group) => GroupChanged?.Invoke(this, group);
        _api.GroupRemoved += (_, groupId) => GroupRemoved?.Invoke(this, groupId);
        _api.FriendsChanged += (_, _) => FriendsChanged?.Invoke(this, EventArgs.Empty);
        _local.PresenceChanged += presence => PresenceChanged?.Invoke(this, presence);
        _local.RoomChanged += room => GroupChanged?.Invoke(this, room);
        _local.RoomRemoved += roomId =>
        {
            GroupRemoved?.Invoke(this, roomId);
            RaiseStateIfChanged();
        };
    }

    public RealtimeState State => _state;

    public event EventHandler<RealtimeState>? StateChanged;

    public event EventHandler<GroupPresenceDto>? PresenceChanged;

    public event EventHandler<GroupDto>? GroupChanged;

    public event EventHandler<Guid>? GroupRemoved;

    public event EventHandler? FriendsChanged;

    private bool InLocalRoom => _local.CurrentRoomId is not null;

    public void Start()
    {
        _local.Start();
        if (!_local.IsLocalOnly)
        {
            _apiStarted = true;
            _api.Start();
        }

        RaiseStateIfChanged();
    }

    public async Task<GroupPresenceDto> JoinAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (_local.IsLocal(groupId))
        {
            var presence = await _local.JoinAsync(groupId, cancellationToken);
            if (_apiStarted && _api.State == RealtimeState.Connected)
            {
                // Uma sala de cada vez: sai da sala do grupo da API (melhor esforço).
                try
                {
                    await _api.LeaveAsync(cancellationToken);
                }
                catch (Exception)
                {
                }
            }

            RaiseStateIfChanged();
            return presence;
        }

        if (_local.IsLocalOnly)
        {
            throw new LocalNetworkException("Sem conta do Discord, só as salas locais estão disponíveis.");
        }

        var joined = await _api.JoinAsync(groupId, cancellationToken);
        if (InLocalRoom)
        {
            await _local.LeaveAsync();
        }

        RaiseStateIfChanged();
        return joined;
    }

    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        if (InLocalRoom)
        {
            await _local.LeaveAsync();
            RaiseStateIfChanged();
            return;
        }

        if (_apiStarted)
        {
            await _api.LeaveAsync(cancellationToken);
        }
    }

    public Task<ScreenShareDto> StartScreenShareAsync(ScreenShareStart start, CancellationToken cancellationToken = default) =>
        InLocalRoom ? _local.StartShareAsync(start, cancellationToken) : _api.StartScreenShareAsync(start, cancellationToken);

    public Task StopScreenShareAsync(CancellationToken cancellationToken = default) =>
        InLocalRoom ? _local.StopShareAsync() : _api.StopScreenShareAsync(cancellationToken);

    public async Task StopAsync()
    {
        await _local.StopAsync();
        if (_apiStarted)
        {
            _apiStarted = false;
            await _api.StopAsync();
        }

        RaiseStateIfChanged();
    }

    private RealtimeState Effective() =>
        InLocalRoom || _local.IsLocalOnly ? RealtimeState.Connected : _api.State;

    private void RaiseStateIfChanged()
    {
        RealtimeState state;
        lock (_lock)
        {
            state = Effective();
            if (state == _state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
