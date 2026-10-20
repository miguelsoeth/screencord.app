using System.Collections.ObjectModel;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Local;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Services;

namespace ScreenCord.ViewModels;

/// <summary>
/// Lista de grupos (salas de presença) na lateral. Mantém a conexão em tempo real aberta enquanto a conta
/// está conectada: entradas e saídas de qualquer integrante chegam pelo hub e atualizam a lista na hora.
/// Também controla o compartilhamento de tela na sala atual e as janelas de quem você está assistindo.
/// As salas locais (<see cref="ILocalRooms"/>) aparecem na mesma lista: funcionam sem a API, e nelas a tela vai
/// direto entre os computadores, com mais qualidade.
/// </summary>
public sealed class GroupsViewModel : ObservableObject
{
    private readonly IGroupsService _groupsService;
    private readonly IGroupsRealtime _realtime;
    private readonly IScreenShareService _screenShare;
    private readonly IScreenShareUi _shareUi;
    private readonly SynchronizationContext? _ui;
    private readonly List<StreamViewerViewModel> _viewers = [];
    private readonly ILocalRooms? _localRooms;
    private readonly IServerLimits? _serverLimits;
    private readonly IFirewallRule? _firewall;
    private bool _newGroupIsLocal;
    private string _joinAddress = "";
    private bool _isJoiningByAddress;
    private bool _showFirewallWarning;
    private bool _isConfiguringFirewall;

    private GroupItemViewModel? _currentGroup;
    private GroupItemViewModel? _confirmingGroup;
    private Guid? _desiredGroupId;
    private RealtimeState _realtimeState;
    private bool _isActive;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isCreating;
    private string _newGroupName = "";
    private string? _errorMessage;
    private string? _createError;
    private IActiveScreenShare? _activeShare;
    private Guid? _shareGroupId;
    private bool _isStartingShare;
    private string? _shareMessage;
    private ShareSettings _lastShareSettings = ShareSettings.Default;

    public GroupsViewModel(
        IGroupsService groupsService,
        IGroupsRealtime realtime,
        IScreenShareService screenShare,
        IScreenShareUi shareUi,
        ILocalRooms? localRooms = null,
        IServerLimits? serverLimits = null,
        IFirewallRule? firewall = null)
    {
        _groupsService = groupsService;
        _realtime = realtime;
        _screenShare = screenShare;
        _shareUi = shareUi;
        _localRooms = localRooms;
        _serverLimits = serverLimits;
        _firewall = firewall;

        // Criado na thread da interface; os eventos do hub chegam de outras threads e são trazidos para cá.
        _ui = SynchronizationContext.Current;
        _realtime.StateChanged += (_, state) => Post(() => OnRealtimeStateChangedAsync(state));
        _realtime.PresenceChanged += (_, presence) => Post(() => OnPresenceChanged(presence));
        _realtime.GroupChanged += (_, group) => Post(() => OnGroupChanged(group));
        _realtime.GroupRemoved += (_, groupId) => Post(() => RemoveGroup(groupId));
        if (_localRooms is not null)
        {
            _localRooms.RoomEnded += (_, reason) => Post(() => ErrorMessage = reason);
        }

        JoinCommand = new AsyncRelayCommand<GroupItemViewModel>(JoinAsync);
        LeaveCurrentCommand = new AsyncRelayCommand(LeaveCurrentAsync, () => CurrentGroup is not null);
        StartCreateCommand = new RelayCommand(() => IsCreating = true);
        CancelCreateCommand = new RelayCommand(CancelCreate);
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync());
        StartShareCommand = new AsyncRelayCommand(StartShareAsync, () => CanStartShare);
        StopShareCommand = new AsyncRelayCommand(StopShareAsync, () => IsSharing);
        ChangeShareCommand = new AsyncRelayCommand(ChangeShareAsync, () => CanChangeShare);
        JoinByAddressCommand = new AsyncRelayCommand(JoinByAddressAsync);
        ConfigureFirewallCommand = new AsyncRelayCommand(ConfigureFirewallAsync);
        DismissFirewallWarningCommand = new RelayCommand(() => ShowFirewallWarning = false);
    }

    /// <summary>Entra numa sala local digitando o IP de quem hospeda (ex.: no Tailscale, que não repassa os anúncios).</summary>
    public AsyncRelayCommand JoinByAddressCommand { get; }

    /// <summary>Cria a regra do Firewall do Windows (pede a permissão do Windows).</summary>
    public AsyncRelayCommand ConfigureFirewallCommand { get; }

    public RelayCommand DismissFirewallWarningCommand { get; }

    /// <summary>Conta local, sem Discord: só as salas locais.</summary>
    public bool IsLocalOnly => _localRooms?.IsLocalOnly == true;

    /// <summary>Criar grupo da API (com amigos) está disponível; sem conta, só salas locais.</summary>
    public bool CanCreateGroup => !IsLocalOnly;

    public bool HasLocalRooms => _localRooms is not null;

    /// <summary>Texto da lista vazia.</summary>
    public string EmptyText => IsLocalOnly
        ? "Nenhuma sala local na rede. Clique em + para criar uma ou entrar numa pelo IP."
        : "Nenhum grupo ou sala ainda. Clique em + para criar um grupo com amigos, criar uma sala local ou entrar numa pelo IP.";

    /// <summary>Na janela de criar: sala local (verdadeiro) ou grupo com amigos.</summary>
    public bool NewGroupIsLocal
    {
        get => _newGroupIsLocal || IsLocalOnly;
        set
        {
            if (SetProperty(ref _newGroupIsLocal, value))
            {
                OnPropertyChanged(nameof(NewGroupIsGroup));
                OnPropertyChanged(nameof(CreateTitle));
                OnPropertyChanged(nameof(CreateHint));
            }
        }
    }

    public bool NewGroupIsGroup
    {
        get => !NewGroupIsLocal;
        set => NewGroupIsLocal = !value;
    }

    public string CreateTitle => NewGroupIsLocal ? "Criar sala local" : "Criar grupo";

    public string CreateHint => NewGroupIsLocal
        ? "Para quem está na mesma rede (LAN, Hamachi, Radmin ou Tailscale). A tela vai direto entre os computadores, até 1080p a 60 fps, e a sala funciona mesmo sem o servidor. Ela fica aberta enquanto você estiver nela."
        : "Com seus amigos do Discord. A tela passa pelo servidor, em 540p a 30 fps. Depois de criar, você já pode adicionar amigos.";

    /// <summary>Endereço digitado para entrar numa sala local (ex.: 25.1.2.3).</summary>
    public string JoinAddress
    {
        get => _joinAddress;
        set => SetProperty(ref _joinAddress, value ?? "");
    }

    public bool IsJoiningByAddress
    {
        get => _isJoiningByAddress;
        private set => SetProperty(ref _isJoiningByAddress, value);
    }

    /// <summary>A regra do firewall não existe: os outros computadores podem não conseguir entrar na sala ou assistir.</summary>
    public bool ShowFirewallWarning
    {
        get => _showFirewallWarning;
        private set => SetProperty(ref _showFirewallWarning, value);
    }

    public bool IsConfiguringFirewall
    {
        get => _isConfiguringFirewall;
        private set => SetProperty(ref _isConfiguringFirewall, value);
    }

    /// <summary>Limites do compartilhamento no grupo: direto nas salas locais, os do servidor nos grupos.</summary>
    public StreamLimits LimitsFor(Guid groupId) =>
        _localRooms?.IsLocal(groupId) == true ? StreamLimits.Direct : _serverLimits?.Relay ?? StreamLimits.Relay;

    public ObservableCollection<GroupItemViewModel> Groups { get; } = [];

    /// <summary>Clicar no nome do grupo: conecta à sala.</summary>
    public AsyncRelayCommand<GroupItemViewModel> JoinCommand { get; }

    public AsyncRelayCommand LeaveCurrentCommand { get; }

    public RelayCommand StartCreateCommand { get; }

    public RelayCommand CancelCreateCommand { get; }

    public AsyncRelayCommand CreateCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>Abre a escolha de tela ou janela e começa a compartilhar na sala atual.</summary>
    public AsyncRelayCommand StartShareCommand { get; }

    public AsyncRelayCommand StopShareCommand { get; }

    /// <summary>Troca resolução, taxa de quadros, qualidade, áudio (ou a origem) sem encerrar para quem assiste.</summary>
    public AsyncRelayCommand ChangeShareCommand { get; }

    public bool CanChangeShare => IsSharing && !IsStartingShare && RealtimeState == RealtimeState.Connected;

    /// <summary>Aviso enquanto o compartilhamento começa ou recomeça com as novas configurações.</summary>
    public string ShareProgressText => IsSharing ? "Aplicando as novas configurações…" : "Iniciando o compartilhamento…";

    /// <summary>Você está compartilhando a tela na sala atual.</summary>
    public bool IsSharing => _activeShare is not null;

    public bool IsStartingShare
    {
        get => _isStartingShare;
        private set
        {
            if (SetProperty(ref _isStartingShare, value))
            {
                OnShareStateChanged();
            }
        }
    }

    public bool CanStartShare => IsInGroup && !IsSharing && !IsStartingShare && RealtimeState == RealtimeState.Connected;

    /// <summary>O que está sendo compartilhado: "Tela 1 (principal)" ou o título da janela.</summary>
    public string? ShareSourceText => _activeShare?.Request.Source.Title;

    /// <summary>"1280 × 720 · 30 fps · Médio (4000 kbps) · com áudio".</summary>
    public string? ShareDetailText => _activeShare is { } active
        ? $"{active.Share.Width} × {active.Share.Height} · {active.Share.FrameRate} fps · {active.Share.BitrateKbps} kbps" +
          (active.Share.HasAudio ? " · com áudio" : "")
        : null;

    /// <summary>Aviso sobre o compartilhamento (ex.: a janela compartilhada foi fechada).</summary>
    public string? ShareMessage
    {
        get => _shareMessage;
        private set
        {
            if (SetProperty(ref _shareMessage, value))
            {
                OnPropertyChanged(nameof(HasShareMessage));
            }
        }
    }

    public bool HasShareMessage => !string.IsNullOrEmpty(ShareMessage);

    /// <summary>Janelas de compartilhamentos que você está assistindo.</summary>
    public IReadOnlyList<StreamViewerViewModel> Viewers => _viewers;

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

                // Saiu da sala (ou trocou de sala): o compartilhamento e as janelas de quem assistia terminam.
                if (previous is not null)
                {
                    LeftRoom(previous.Id);
                }

                OnPropertyChanged(nameof(IsInGroup));
                LeaveCurrentCommand.NotifyCanExecuteChanged();
                OnShareStateChanged();
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
                OnShareStateChanged();
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

    /// <summary>Erro ao criar um grupo, mostrado na própria janela de criação.</summary>
    public string? CreateError
    {
        get => _createError;
        private set
        {
            if (SetProperty(ref _createError, value))
            {
                OnPropertyChanged(nameof(HasCreateError));
            }
        }
    }

    public bool HasCreateError => !string.IsNullOrEmpty(CreateError);

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
        OnPropertyChanged(nameof(IsLocalOnly));
        OnPropertyChanged(nameof(CanCreateGroup));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(NewGroupIsLocal));
        OnPropertyChanged(nameof(NewGroupIsGroup));
        OnPropertyChanged(nameof(CreateTitle));
        OnPropertyChanged(nameof(CreateHint));
        if (!IsLocalOnly && _serverLimits is not null)
        {
            _ = _serverLimits.RefreshAsync(CancellationToken.None);
        }

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
        _ = EndShareAsync(null);
        CloseViewers(null);
        ShareMessage = null;
        _isActive = false;
        _desiredGroupId = null;
        CurrentGroup = null;
        ConfirmingGroup = null;
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
            var details = _localRooms?.IsLocal(group.Id) == true ? _localRooms.Details(group.Id) : await _groupsService.GetGroupAsync(group.Id);

            group.Name = details.Name;
            group.MemberCount = details.Members.Count;
            group.CanDelete = details.CanDelete;
            group.SetMembers(details.Members);

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
        if (group.IsLocal)
        {
            await LeaveLocalRoomAsync(group);
            return;
        }

        try
        {
            await _groupsService.LeaveGroupAsync(group.Id);
        }
        catch (Exception ex)
        {
            group.MembersError = ErrorMessages.For(ex);
            return;
        }

        RemoveGroup(group.Id);
    }

    internal async Task DeleteGroupAsync(GroupItemViewModel group)
    {
        group.MembersError = null;
        if (group.IsLocal)
        {
            // Encerrar a sala local é quem hospeda sair dela.
            await LeaveLocalRoomAsync(group);
            return;
        }

        try
        {
            await _groupsService.DeleteGroupAsync(group.Id);
        }
        catch (Exception ex)
        {
            group.MembersError = ErrorMessages.For(ex);
            return;
        }

        // Os outros integrantes (e as outras janelas desta conta) recebem GroupRemoved pelo hub.
        RemoveGroup(group.Id);
    }

    /// <summary>Grupo com a confirmação de sair ou de excluir aberta (janela central), ou nulo.</summary>
    public GroupItemViewModel? ConfirmingGroup
    {
        get => _confirmingGroup;
        private set
        {
            if (SetProperty(ref _confirmingGroup, value))
            {
                OnPropertyChanged(nameof(IsConfirming));
            }
        }
    }

    public bool IsConfirming => ConfirmingGroup is not null;

    /// <summary>Pedido de convidar amigos para um grupo; a janela de amigos abre para ele.</summary>
    public event Action<GroupItemViewModel>? InviteRequested;

    internal void RequestInvite(GroupItemViewModel group)
    {
        group.IsMembersOpen = false;
        InviteRequested?.Invoke(group);
    }

    /// <summary>Uma confirmação de cada vez: abrir a de um grupo fecha a de outro.</summary>
    internal void UpdateConfirmation(GroupItemViewModel group)
    {
        if (group.IsConfirmingLeave || group.IsConfirmingDelete)
        {
            var previous = ConfirmingGroup;
            ConfirmingGroup = group;
            if (previous is not null && previous != group)
            {
                previous.IsConfirmingLeave = previous.IsConfirmingDelete = false;
            }
        }
        else if (ConfirmingGroup == group)
        {
            ConfirmingGroup = null;
        }
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
            ApplyPresence(group, presence);
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
            CreateError = "Digite um nome para o grupo.";
            return;
        }

        CreateError = null;
        try
        {
            if (NewGroupIsLocal && _localRooms is not null)
            {
                var room = await _localRooms.CreateRoomAsync(name);
                var roomItem = Upsert(room);
                CancelCreate();
                await JoinAsync(roomItem);
                _ = CheckFirewallAsync();
                return;
            }

            var created = await _groupsService.CreateGroupAsync(name);
            var item = Upsert(created);
            CancelCreate();

            // Grupo novo: já abre os integrantes para adicionar amigos.
            item.IsMembersOpen = true;
        }
        catch (Exception ex)
        {
            CreateError = ErrorMessages.For(ex);
        }
    }

    private async Task JoinByAddressAsync()
    {
        if (_localRooms is null || IsJoiningByAddress)
        {
            return;
        }

        CreateError = null;
        IsJoiningByAddress = true;
        try
        {
            var room = await _localRooms.FindAsync(JoinAddress);
            var item = Upsert(room);
            CancelCreate();
            JoinAddress = "";
            await JoinAsync(item);
        }
        catch (Exception ex)
        {
            CreateError = ErrorMessages.For(ex);
        }
        finally
        {
            IsJoiningByAddress = false;
        }
    }

    private async Task LeaveLocalRoomAsync(GroupItemViewModel group)
    {
        group.IsMembersOpen = false;
        if (CurrentGroup == group)
        {
            _desiredGroupId = null;
            CurrentGroup = null;
        }

        try
        {
            await _realtime.LeaveAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
    }

    /// <summary>Sem a regra do firewall, os outros computadores podem não alcançar este: mostra o aviso com o botão.</summary>
    private async Task CheckFirewallAsync()
    {
        if (_firewall is not null && !await _firewall.IsConfiguredAsync())
        {
            ShowFirewallWarning = true;
        }
    }

    private async Task ConfigureFirewallAsync()
    {
        if (_firewall is null || IsConfiguringFirewall)
        {
            return;
        }

        IsConfiguringFirewall = true;
        try
        {
            if (await _firewall.ConfigureAsync())
            {
                ShowFirewallWarning = false;
            }
            else
            {
                ErrorMessage = "A regra do firewall não foi criada. Aceite o pedido de permissão do Windows para liberar as salas locais.";
            }
        }
        finally
        {
            IsConfiguringFirewall = false;
        }
    }

    private void CancelCreate()
    {
        NewGroupName = "";
        JoinAddress = "";
        CreateError = null;
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
            // A conexão caiu: o servidor já encerrou o compartilhamento junto com ela.
            if (IsSharing)
            {
                await EndShareAsync("O compartilhamento de tela parou porque a conexão com o servidor caiu.");
            }

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
                ApplyPresence(group, presence);
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
            ApplyPresence(group, presence);
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
        // As salas locais aparecem mesmo com a API fora do ar; o erro dela vem depois.
        IReadOnlyList<GroupDto> groups = [];
        Exception? apiError = null;
        if (!IsLocalOnly)
        {
            try
            {
                groups = await _groupsService.GetGroupsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _localRooms is not null)
            {
                apiError = ex;
            }
        }

        if (!_isActive)
        {
            return;
        }

        var rooms = _localRooms?.Rooms ?? [];
        foreach (var stale in Groups.Where(g => g.IsLocal ? rooms.All(r => r.Id != g.Id) : apiError is null && groups.All(x => x.Id != g.Id)).ToList())
        {
            RemoveGroup(stale.Id);
        }

        foreach (var group in groups.Concat(rooms))
        {
            Upsert(group);
        }

        _hasLoaded = true;
        OnPropertyChanged(nameof(ShowNoGroups));
        if (apiError is not null)
        {
            throw apiError;
        }
    }

    /// <summary>Atualiza a linha existente (mantendo o popup aberto) ou insere na posição em ordem alfabética.</summary>
    private GroupItemViewModel Upsert(GroupDto group)
    {
        var item = Groups.FirstOrDefault(g => g.Id == group.Id);
        if (item is null)
        {
            item = new GroupItemViewModel(this, group);
            if (_localRooms?.IsLocal(group.Id) == true)
            {
                var info = _localRooms.Info(group.Id);
                item.SetLocal(info?.IsHost == true, info?.Address);
            }

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
        if (item.ApplyPresence(CentralApiClient.ToPeople(group.ConnectedUsers), group.PresenceVersion, group.ScreenShares))
        {
            SyncViewers(item);
        }

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
        group.IsConfirmingLeave = group.IsConfirmingDelete = false;
        Groups.Remove(group);
        OnPropertyChanged(nameof(ShowNoGroups));
    }

    private void ApplyPresence(GroupItemViewModel group, GroupPresenceDto presence)
    {
        if (group.ApplyPresence(CentralApiClient.ToPeople(presence.ConnectedUsers), presence.Version, presence.ScreenShares))
        {
            SyncViewers(group);
        }
    }

    internal async Task WatchAsync(GroupItemViewModel group, Person sharer)
    {
        if (sharer.UserId == _screenShare.CurrentUserId || sharer.UserId == _localRooms?.Me.Id)
        {
            return;
        }

        if (_viewers.FirstOrDefault(v => v.GroupId == group.Id && v.SharerId == sharer.UserId && !v.IsEnded) is { } open)
        {
            _shareUi.ActivateViewer(open);
            return;
        }

        // Só quem está na sala pode assistir: clicar em "AO VIVO" de outro grupo entra nele.
        if (group != CurrentGroup || RealtimeState != RealtimeState.Connected)
        {
            await JoinAsync(group);
            if (group != CurrentGroup)
            {
                return;
            }
        }

        if (!group.Shares.TryGetValue(sharer.UserId, out var share))
        {
            ErrorMessage = $"{sharer.DisplayName} não está mais compartilhando a tela.";
            return;
        }

        // Uma janela que já terminou é substituída pela nova.
        foreach (var ended in _viewers.Where(v => v.GroupId == group.Id && v.SharerId == sharer.UserId).ToList())
        {
            ended.RequestClose();
        }

        var viewer = new StreamViewerViewModel(_screenShare, group.Id, group.Name, sharer, share);
        _viewers.Add(viewer);
        _shareUi.ShowViewer(viewer, () =>
        {
            _viewers.Remove(viewer);
            _ = viewer.DisposeAsync().AsTask();
        });
        await viewer.StartAsync();
    }

    private async Task StartShareAsync()
    {
        if (CurrentGroup is not { } group || !CanStartShare)
        {
            return;
        }

        var request = _shareUi.PickShare(_lastShareSettings, LimitsFor(group.Id));
        if (request is null || CurrentGroup != group)
        {
            return;
        }

        _lastShareSettings = request.Settings;
        ShareMessage = null;
        IsStartingShare = true;
        try
        {
            var active = await _screenShare.StartAsync(group.Id, request);
            if (CurrentGroup != group || !_isActive)
            {
                // Saiu da sala enquanto o compartilhamento começava.
                await active.StopAsync();
                return;
            }

            active.Ended += (_, reason) => Post(() => OnShareEnded(active, reason));
            _activeShare = active;
            _shareGroupId = group.Id;
            ShareMessage = active.AudioWarning;
            if (group.IsLocal)
            {
                _ = CheckFirewallAsync();
            }
        }
        catch (Exception ex)
        {
            ShareMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsStartingShare = false;
        }
    }

    private async Task ChangeShareAsync()
    {
        if (_activeShare is not { } current || CurrentGroup is not { } group || _shareGroupId != group.Id || !CanChangeShare)
        {
            return;
        }

        var request = _shareUi.PickShare(current.Request.Settings, LimitsFor(group.Id), current.Request.Source);
        if (request is null || _activeShare != current)
        {
            return;
        }

        _lastShareSettings = request.Settings;
        ShareMessage = null;
        IsStartingShare = true;
        try
        {
            var replacement = await _screenShare.StartAsync(group.Id, request, current);
            if (_activeShare != current || CurrentGroup != group || !_isActive)
            {
                // O compartilhamento terminou (ou a sala mudou) enquanto as configurações eram aplicadas.
                await replacement.StopAsync();
                return;
            }

            replacement.Ended += (_, reason) => Post(() => OnShareEnded(replacement, reason));
            _activeShare = replacement;
            ShareMessage = replacement.AudioWarning;
        }
        catch (Exception ex)
        {
            // Se a troca falhou antes de valer, o compartilhamento atual continua; senão, ele já avisou o fim.
            ShareMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsStartingShare = false;
        }
    }

    private Task StopShareAsync() => EndShareAsync(null);

    private async Task EndShareAsync(string? message)
    {
        if (_activeShare is not { } active)
        {
            return;
        }

        _activeShare = null;
        _shareGroupId = null;
        ShareMessage = message;
        OnShareStateChanged();
        try
        {
            await active.StopAsync();
        }
        catch (Exception)
        {
            // Melhor esforço: o servidor encerra o compartilhamento quando a conexão de mídia cai.
        }
    }

    private void OnShareEnded(IActiveScreenShare active, string reason)
    {
        if (_activeShare != active)
        {
            return;
        }

        _activeShare = null;
        _shareGroupId = null;
        ShareMessage = reason;
        OnShareStateChanged();
    }

    private void LeftRoom(Guid groupId)
    {
        if (_shareGroupId == groupId)
        {
            _ = EndShareAsync(null);
        }

        CloseViewers(groupId);
    }

    private void CloseViewers(Guid? groupId)
    {
        foreach (var viewer in _viewers.Where(v => groupId is null || v.GroupId == groupId).ToList())
        {
            viewer.RequestClose();
        }
    }

    /// <summary>Repassa às janelas abertas o fim (ou a mudança) dos compartilhamentos que elas mostram.</summary>
    private void SyncViewers(GroupItemViewModel group)
    {
        foreach (var viewer in _viewers.Where(v => v.GroupId == group.Id).ToList())
        {
            viewer.UpdateShare(group.Shares.GetValueOrDefault(viewer.SharerId));
        }
    }

    private void OnShareStateChanged()
    {
        OnPropertyChanged(nameof(IsSharing));
        OnPropertyChanged(nameof(CanStartShare));
        OnPropertyChanged(nameof(ShareSourceText));
        OnPropertyChanged(nameof(ShareDetailText));
        StartShareCommand?.NotifyCanExecuteChanged();
        StopShareCommand?.NotifyCanExecuteChanged();
        ChangeShareCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChangeShare));
        OnPropertyChanged(nameof(ShareProgressText));
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
