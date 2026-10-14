using System.Collections.ObjectModel;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Services;

namespace ScreenCord.ViewModels;

/// <summary>
/// Lista de grupos (salas de presença) na lateral. Mantém a conexão em tempo real aberta enquanto a conta
/// está conectada: entradas e saídas de qualquer integrante chegam pelo hub e atualizam a lista na hora.
/// </summary>
public sealed class GroupsViewModel : ObservableObject
{
    private readonly IGroupsService _groupsService;
    private readonly IGroupsRealtime _realtime;
    private readonly IFriendsService _friendsService;
    private readonly SynchronizationContext? _ui;

    private GroupItemViewModel? _currentGroup;
    private Guid? _desiredGroupId;
    private RealtimeState _realtimeState;
    private bool _isActive;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isCreating;
    private string _newGroupName = "";
    private string? _errorMessage;

    public GroupsViewModel(IGroupsService groupsService, IGroupsRealtime realtime, IFriendsService friendsService)
    {
        _groupsService = groupsService;
        _realtime = realtime;
        _friendsService = friendsService;

        // Criado na thread da interface; os eventos do hub chegam de outras threads e são trazidos para cá.
        _ui = SynchronizationContext.Current;
        _realtime.StateChanged += (_, state) => Post(() => OnRealtimeStateChangedAsync(state));
        _realtime.PresenceChanged += (_, presence) => Post(() => OnPresenceChanged(presence));
        _realtime.GroupChanged += (_, group) => Post(() => OnGroupChanged(group));
        _realtime.GroupRemoved += (_, groupId) => Post(() => RemoveGroup(groupId));

        JoinCommand = new AsyncRelayCommand<GroupItemViewModel>(JoinAsync);
        LeaveCurrentCommand = new AsyncRelayCommand(LeaveCurrentAsync, () => CurrentGroup is not null);
        StartCreateCommand = new RelayCommand(() => IsCreating = true);
        CancelCreateCommand = new RelayCommand(CancelCreate);
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync());
    }

    public ObservableCollection<GroupItemViewModel> Groups { get; } = [];

    /// <summary>Clicar no nome do grupo: conecta à sala.</summary>
    public AsyncRelayCommand<GroupItemViewModel> JoinCommand { get; }

    public AsyncRelayCommand LeaveCurrentCommand { get; }

    public RelayCommand StartCreateCommand { get; }

    public RelayCommand CancelCreateCommand { get; }

    public AsyncRelayCommand CreateCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>Grupo em que este aplicativo está conectado.</summary>
    public GroupItemViewModel? CurrentGroup
    {
        get => _currentGroup;
        private set
        {
            var previous = _currentGroup;
            if (SetProperty(ref _currentGroup, value))
            {
                if (previous is not null)
                {
                    previous.IsCurrent = false;
                }

                if (value is not null)
                {
                    value.IsCurrent = true;
                }

                OnPropertyChanged(nameof(IsInGroup));
                LeaveCurrentCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInGroup => CurrentGroup is not null;

    public RealtimeState RealtimeState
    {
        get => _realtimeState;
        private set
        {
            if (SetProperty(ref _realtimeState, value))
            {
                OnPropertyChanged(nameof(RealtimeStatusText));
                OnPropertyChanged(nameof(HasRealtimeStatus));
            }
        }
    }

    /// <summary>Aviso sobre a conexão em tempo real; nulo quando está tudo certo.</summary>
    public string? RealtimeStatusText => !_isActive ? null : RealtimeState switch
    {
        RealtimeState.Connecting => "Conectando ao servidor…",
        RealtimeState.Reconnecting => "Conexão perdida. Reconectando…",
        RealtimeState.Disconnected => "Sem conexão em tempo real.",
        _ => null,
    };

    public bool HasRealtimeStatus => RealtimeStatusText is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Lista carregada com sucesso e vazia.</summary>
    public bool ShowNoGroups => _hasLoaded && Groups.Count == 0 && !IsCreating;

    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetProperty(ref _isCreating, value))
            {
                OnPropertyChanged(nameof(ShowNoGroups));
            }
        }
    }

    public string NewGroupName
    {
        get => _newGroupName;
        set => SetProperty(ref _newGroupName, value ?? "");
    }

    public static int MaxNameLength => GroupsHubContract.MaxNameLength;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Abre a conexão em tempo real e carrega os grupos.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _isActive = true;
        _realtime.Start();
        RealtimeState = _realtime.State == RealtimeState.Disconnected ? RealtimeState.Connecting : _realtime.State;
        OnPropertyChanged(nameof(RealtimeStatusText));
        OnPropertyChanged(nameof(HasRealtimeStatus));

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await RefreshGroupsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Fecha a conexão em tempo real (saindo da sala) e limpa a lista.</summary>
    public async Task SignOutAsync()
    {
        Clear();
        try
        {
            await _realtime.StopAsync();
        }
        catch (Exception)
        {
            // Melhor esforço: o servidor tira a conexão da sala quando ela cai.
        }
    }

    public void Clear()
    {
        _isActive = false;
        _desiredGroupId = null;
        CurrentGroup = null;
        Groups.Clear();
        _hasLoaded = false;
        CancelCreate();
        ErrorMessage = null;
        OnPropertyChanged(nameof(ShowNoGroups));
        OnPropertyChanged(nameof(RealtimeStatusText));
        OnPropertyChanged(nameof(HasRealtimeStatus));
    }

    internal async Task LoadMembersAsync(GroupItemViewModel group)
    {
        group.IsLoadingMembers = true;
        group.MembersError = null;
        try
        {
            var details = await _groupsService.GetGroupAsync(group.Id);
            var friends = await _friendsService.GetFriendsAsync();

            var memberIds = details.Members.Select(m => m.Person.UserId).ToHashSet();
            group.Name = details.Name;
            group.MemberCount = details.Members.Count;
            group.SetMembers(details.Members, friends.Where(f => !memberIds.Contains(f.UserId)));

            // Vale a presença mais nova: a dos detalhes, ou a que já chegou pelo hub enquanto eles eram buscados.
            var connected = details.Members.Where(m => m.IsConnected).Select(m => m.Person).ToList();
            if (!group.ApplyPresence(connected, details.PresenceVersion))
            {
                group.ApplyPresence(group.ConnectedUsers.ToList(), group.PresenceVersion);
            }
        }
        catch (Exception ex)
        {
            group.MembersError = ErrorMessages.For(ex);
        }
        finally
        {
            group.IsLoadingMembers = false;
        }
    }

    internal async Task AddMemberAsync(GroupItemViewModel group, Person friend)
    {
        group.MembersError = null;
        try
        {
            await _groupsService.AddMemberAsync(group.Id, friend.UserId);
        }
        catch (Exception ex)
        {
            group.MembersError = ErrorMessages.For(ex);
            return;
        }

        await LoadMembersAsync(group);
    }

    internal async Task LeaveGroupAsync(GroupItemViewModel group)
    {
        group.MembersError = null;
        try
        {
            await _groupsService.LeaveGroupAsync(group.Id);
        }
        catch (Exception ex)
        {
            group.MembersError = ErrorMessages.For(ex);
            return;
        }

        group.IsMembersOpen = false;
        RemoveGroup(group.Id);
    }

    private async Task JoinAsync(GroupItemViewModel group)
    {
        if (group == CurrentGroup && RealtimeState == RealtimeState.Connected)
        {
            return;
        }

        ErrorMessage = null;
        group.IsJoining = true;
        try
        {
            var presence = await _realtime.JoinAsync(group.Id);
            _desiredGroupId = group.Id;
            CurrentGroup = Groups.Contains(group) ? group : null;
            group.ApplyPresence(CentralApiClient.ToPeople(presence.ConnectedUsers), presence.Version);
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            group.IsJoining = false;
        }
    }

    private async Task LeaveCurrentAsync()
    {
        _desiredGroupId = null;
        CurrentGroup = null;
        ErrorMessage = null;
        try
        {
            await _realtime.LeaveAsync();
        }
        catch (Exception ex)
        {
            // Sem conexão, o servidor já tirou esta conexão da sala.
            if (_realtime.State == RealtimeState.Connected)
            {
                ErrorMessage = ErrorMessages.For(ex);
            }
        }
    }

    private async Task CreateAsync()
    {
        var name = string.Join(' ', NewGroupName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
        {
            ErrorMessage = "Digite um nome para o grupo.";
            return;
        }

        ErrorMessage = null;
        try
        {
            var created = await _groupsService.CreateGroupAsync(name);
            var item = Upsert(created);
            CancelCreate();

            // Grupo novo: já abre os integrantes para adicionar amigos.
            item.IsMembersOpen = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
    }

    private void CancelCreate()
    {
        NewGroupName = "";
        IsCreating = false;
    }

    private async Task OnRealtimeStateChangedAsync(RealtimeState state)
    {
        if (!_isActive)
        {
            return;
        }

        RealtimeState = state;
        if (state != RealtimeState.Connected)
        {
            return;
        }

        // (Re)conectado: a presença pode ter mudado enquanto a conexão estava fora, e a sala precisa ser retomada.
        try
        {
            await RefreshGroupsAsync(CancellationToken.None);
            ErrorMessage = null;
            if (_desiredGroupId is { } groupId && Groups.FirstOrDefault(g => g.Id == groupId) is { } group)
            {
                var presence = await _realtime.JoinAsync(groupId);
                CurrentGroup = group;
                group.ApplyPresence(CentralApiClient.ToPeople(presence.ConnectedUsers), presence.Version);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
    }

    private void OnPresenceChanged(GroupPresenceDto presence)
    {
        if (_isActive && Groups.FirstOrDefault(g => g.Id == presence.GroupId) is { } group)
        {
            group.ApplyPresence(CentralApiClient.ToPeople(presence.ConnectedUsers), presence.Version);
        }
    }

    private void OnGroupChanged(GroupDto group)
    {
        if (!_isActive)
        {
            return;
        }

        var existing = Groups.FirstOrDefault(g => g.Id == group.Id);
        var countChanged = existing is not null && existing.MemberCount != group.MemberCount;
        var item = Upsert(group);

        // Alguém entrou no grupo enquanto o popup está aberto: recarrega a lista de integrantes.
        if (countChanged && item.IsMembersOpen && !item.IsLoadingMembers && item.Members.Count != group.MemberCount)
        {
            _ = LoadMembersAsync(item);
        }
    }

    private async Task RefreshGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await _groupsService.GetGroupsAsync(cancellationToken);
        if (!_isActive)
        {
            return;
        }

        foreach (var stale in Groups.Where(g => groups.All(x => x.Id != g.Id)).ToList())
        {
            RemoveGroup(stale.Id);
        }

        foreach (var group in groups)
        {
            Upsert(group);
        }

        _hasLoaded = true;
        OnPropertyChanged(nameof(ShowNoGroups));
    }

    /// <summary>Atualiza a linha existente (mantendo o popup aberto) ou insere na posição em ordem alfabética.</summary>
    private GroupItemViewModel Upsert(GroupDto group)
    {
        var item = Groups.FirstOrDefault(g => g.Id == group.Id);
        if (item is null)
        {
            item = new GroupItemViewModel(this, group);
            var index = 0;
            while (index < Groups.Count && string.Compare(Groups[index].Name, group.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
            {
                index++;
            }

            Groups.Insert(index, item);
            OnPropertyChanged(nameof(ShowNoGroups));
            return item;
        }

        item.Name = group.Name;
        item.MemberCount = group.MemberCount;
        item.ApplyPresence(CentralApiClient.ToPeople(group.ConnectedUsers), group.PresenceVersion);
        return item;
    }

    private void RemoveGroup(Guid groupId)
    {
        if (Groups.FirstOrDefault(g => g.Id == groupId) is not { } group)
        {
            return;
        }

        if (CurrentGroup == group)
        {
            CurrentGroup = null;
            _desiredGroupId = null;
        }

        group.IsMembersOpen = false;
        Groups.Remove(group);
        OnPropertyChanged(nameof(ShowNoGroups));
    }

    private void Post(Action action) => Post(() =>
    {
        action();
        return Task.CompletedTask;
    });

    private void Post(Func<Task> action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            _ = action();
        }
        else
        {
            _ui.Post(_ => _ = action(), null);
        }
    }
}
