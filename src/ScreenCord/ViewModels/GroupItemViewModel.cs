using ScreenCord.Localization;
using System.Collections.ObjectModel;
using ScreenCord.Contracts;
using ScreenCord.Local;
using ScreenCord.Models;
using ScreenCord.Services;

namespace ScreenCord.ViewModels;

/// <summary>Uma linha da lista de grupos, com o popup de integrantes.</summary>
public sealed class GroupItemViewModel : ObservableObject
{
    private readonly GroupsViewModel _owner;
    private string _name;
    private int _memberCount;
    private bool _isCurrent;
    private bool _isJoining;
    private bool _isMembersOpen;
    private bool _isLoadingMembers;
    private bool _hasLoadedMembers;
    private bool _isConfirmingLeave;
    private bool _isConfirmingDelete;
    private bool _canDelete;
    private bool _canEditSettings;
    private GroupSettings _settings = GroupSettings.Default;
    private bool _isExpanded = true;
    private string? _membersError;

    public GroupItemViewModel(GroupsViewModel owner, GroupDto group)
    {
        _owner = owner;
        Id = group.Id;
        _name = group.Name;
        _memberCount = group.MemberCount;
        ApplyPresence(CentralApiClient.ToPeople(group.ConnectedUsers), group.PresenceVersion, group.ScreenShares);
        _settings = group.Settings ?? GroupSettings.Default;

        WatchCommand = new AsyncRelayCommand<Person>(person => _owner.WatchAsync(this, person, ShareKind.Screen));
        WatchCameraCommand = new AsyncRelayCommand<Person>(person => _owner.WatchAsync(this, person, ShareKind.Camera));
        AddMemberCommand = new AsyncRelayCommand<Person>(person => _owner.AddMemberAsync(this, person));
        LeaveGroupCommand = new AsyncRelayCommand(() => _owner.LeaveGroupAsync(this));
        ConfirmLeaveCommand = new RelayCommand(() => StartConfirmation(leave: true));
        DeleteGroupCommand = new AsyncRelayCommand(() => _owner.DeleteGroupAsync(this));
        ConfirmDeleteCommand = new RelayCommand(() => StartConfirmation(leave: false));
        CancelConfirmationCommand = new RelayCommand(() => IsConfirmingLeave = IsConfirmingDelete = false);
        InviteCommand = new RelayCommand(() => _owner.RequestInvite(this));
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public Guid Id { get; }

    /// <summary>Sala local: hospedada num computador da rede, sem a API; a tela vai direto entre os computadores.</summary>
    public bool IsLocal { get; private set; }

    public bool IsApiGroup => !IsLocal;

    /// <summary>Este computador hospeda a sala local.</summary>
    public bool IsLocalHost { get; private set; }

    /// <summary>Por onde entrar na sala pelo IP (para copiar e passar a quem não vê a sala na lista).</summary>
    public ObservableCollection<LocalAddress> LocalAddresses { get; } = [];

    public bool HasLocalAddresses => LocalAddresses.Count > 0;

    public string LocalAddressesHeader => IsLocalHost
        ? Strings.LocalAddressesHeader
        : Strings.LocalAddressHeader;

    public string DeleteText => IsLocal ? Strings.EndRoom : Strings.DeleteGroup;

    public string ConfirmDeleteTitle => IsLocal ? Strings.EndRoomQuestion : Strings.DeleteGroupQuestion;

    public string ConfirmDeleteSuffix => IsLocal ? Strings.EndRoomSuffix : Strings.DeleteGroupSuffix;

    internal void SetLocal(bool isHost, IReadOnlyList<LocalAddress> addresses)
    {
        IsLocal = true;
        IsLocalHost = isHost;
        if (!LocalAddresses.SequenceEqual(addresses))
        {
            LocalAddresses.Clear();
            foreach (var address in addresses)
            {
                LocalAddresses.Add(address);
            }
        }

        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsApiGroup));
        OnPropertyChanged(nameof(IsLocalHost));
        OnPropertyChanged(nameof(HasLocalAddresses));
        OnPropertyChanged(nameof(LocalAddressesHeader));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(ConfirmDeleteTitle));
        OnPropertyChanged(nameof(ConfirmDeleteSuffix));
    }

    /// <summary>Versão da presença já aplicada; notificações mais antigas são ignoradas.</summary>
    public long PresenceVersion { get; private set; } = -1;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int MemberCount
    {
        get => _memberCount;
        set
        {
            if (SetProperty(ref _memberCount, value))
            {
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    /// <summary>Quem está conectado à sala agora, exibido logo abaixo da linha do grupo.</summary>
    public ObservableCollection<Person> ConnectedUsers { get; } = [];

    public IReadOnlyList<Guid> ConnectedUserIds => ConnectedUsers.Select(p => p.UserId).ToList();

    public int ConnectedCount => ConnectedUsers.Count;

    /// <summary>Transmissões ativas na sala (telas e câmeras), por pessoa e tipo.</summary>
    public IReadOnlyDictionary<ShareKey, ScreenShareDto> Shares { get; private set; } = new Dictionary<ShareKey, ScreenShareDto>();

    /// <summary>Quem está compartilhando a tela agora (marcado "AO VIVO" na lista de conectados).</summary>
    public IReadOnlySet<Guid> SharingUserIds { get; private set; } = new HashSet<Guid>();

    /// <summary>Quem está com a câmera ligada agora (marcado com o ícone de câmera na lista de conectados).</summary>
    public IReadOnlySet<Guid> CameraUserIds { get; private set; } = new HashSet<Guid>();

    public bool HasShares => Shares.Count > 0;

    /// <summary>Clicar em "AO VIVO" ao lado de alguém: abre a tela dessa pessoa.</summary>
    public AsyncRelayCommand<Person> WatchCommand { get; }

    /// <summary>Clicar no ícone de câmera ao lado de alguém: abre a câmera dessa pessoa.</summary>
    public AsyncRelayCommand<Person> WatchCameraCommand { get; }

    /// <summary>Contagem da linha: conectados / integrantes (ex.: "1/2").</summary>
    public string CountText => $"{ConnectedCount}/{MemberCount}";

    /// <summary>Lista de conectados aberta abaixo do grupo; a seta da linha recolhe e expande.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ShowConnectedUsers));
            }
        }
    }

    public bool ShowConnectedUsers => IsExpanded && HasConnected;

    /// <summary>Há alguém conectado à sala agora.</summary>
    public bool HasConnected => ConnectedCount > 0;

    /// <summary>Este aplicativo está conectado à sala.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsJoining
    {
        get => _isJoining;
        set => SetProperty(ref _isJoining, value);
    }

    /// <summary>Texto do tooltip da contagem: "3 integrantes · 1 conectado".</summary>
    public string Summary => Strings.MembersSummary(MemberCount, ConnectedCount);

    public bool IsMembersOpen
    {
        get => _isMembersOpen;
        set
        {
            if (SetProperty(ref _isMembersOpen, value))
            {
                if (value)
                {
                    _ = _owner.LoadMembersAsync(this);
                }
            }
        }
    }

    public ObservableCollection<GroupMemberItem> Members { get; } = [];

    /// <summary>Ids dos integrantes, trocado a cada recarga (a janela de amigos mostra quem já foi convidado).</summary>
    public IReadOnlySet<Guid> MemberIds { get; private set; } = new HashSet<Guid>();

    public bool IsLoadingMembers
    {
        get => _isLoadingMembers;
        set => SetProperty(ref _isLoadingMembers, value);
    }

    public bool HasLoadedMembers
    {
        get => _hasLoadedMembers;
        set => SetProperty(ref _hasLoadedMembers, value);
    }

    public string? MembersError
    {
        get => _membersError;
        set
        {
            if (SetProperty(ref _membersError, value))
            {
                OnPropertyChanged(nameof(HasMembersError));
            }
        }
    }

    public bool HasMembersError => !string.IsNullOrEmpty(MembersError);

    public bool IsConfirmingLeave
    {
        get => _isConfirmingLeave;
        set
        {
            if (SetProperty(ref _isConfirmingLeave, value))
            {
                if (value)
                {
                    IsConfirmingDelete = false;
                }

                _owner.UpdateConfirmation(this);
            }
        }
    }

    /// <summary>Quem está usando o aplicativo criou o grupo e pode excluí-lo (vem com os integrantes).</summary>
    public bool CanDelete
    {
        get => _canDelete;
        set => SetProperty(ref _canDelete, value);
    }

    /// <summary>O que o grupo aceita transmitir na sala.</summary>
    public GroupSettings Settings => _settings;

    /// <summary>Quem criou o grupo (ou hospeda a sala local) pode mudar o que ele aceita (vem com os integrantes).</summary>
    public bool CanEditSettings
    {
        get => _canEditSettings;
        set => SetProperty(ref _canEditSettings, value);
    }

    /// <summary>O grupo aceita ao menos um tipo de transmissão.</summary>
    public bool AllowsSharing => _settings.AllowScreenShare || _settings.AllowCamera;

    /// <summary>Marcar ou desmarcar muda a configuração do grupo (só para quem pode: <see cref="CanEditSettings"/>).</summary>
    public bool AllowScreenShare
    {
        get => _settings.AllowScreenShare;
        set => ChangeSettings(_settings with { AllowScreenShare = value });
    }

    public bool AllowCamera
    {
        get => _settings.AllowCamera;
        set => ChangeSettings(_settings with { AllowCamera = value });
    }

    /// <summary>Aplica as configurações que vieram do servidor (ou da sala local); retorna se mudaram algo.</summary>
    public bool ApplySettings(GroupSettings settings)
    {
        if (_settings == settings)
        {
            return false;
        }

        _settings = settings;
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(AllowsSharing));
        RefreshSettings();
        return true;
    }

    /// <summary>Refaz as caixas de marcar com o que vale (ex.: a mudança pedida não foi aceita).</summary>
    public void RefreshSettings()
    {
        OnPropertyChanged(nameof(AllowScreenShare));
        OnPropertyChanged(nameof(AllowCamera));
    }

    private void ChangeSettings(GroupSettings settings)
    {
        if (!CanEditSettings || settings == _settings)
        {
            return;
        }

        _ = _owner.UpdateSettingsAsync(this, settings);
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set
        {
            if (SetProperty(ref _isConfirmingDelete, value))
            {
                if (value)
                {
                    IsConfirmingLeave = false;
                }

                _owner.UpdateConfirmation(this);
            }
        }
    }

    /// <summary>Fecha o menu de integrantes e abre a confirmação (janela central) de sair ou de excluir.</summary>
    private void StartConfirmation(bool leave)
    {
        IsMembersOpen = false;
        MembersError = null;
        if (leave)
        {
            IsConfirmingLeave = true;
        }
        else
        {
            IsConfirmingDelete = true;
        }
    }

    /// <summary>Abre a janela de amigos para convidar para este grupo.</summary>
    public RelayCommand InviteCommand { get; }

    public RelayCommand CancelConfirmationCommand { get; }

    public AsyncRelayCommand<Person> AddMemberCommand { get; }

    public AsyncRelayCommand LeaveGroupCommand { get; }

    public RelayCommand ConfirmLeaveCommand { get; }

    public AsyncRelayCommand DeleteGroupCommand { get; }

    public RelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand ToggleExpandCommand { get; }

    /// <summary>
    /// Aplica uma presença se ela for mais nova que a atual. Retorna falso se foi descartada. Sem
    /// <paramref name="shares"/>, os compartilhamentos conhecidos são mantidos (ex.: detalhes do grupo).
    /// </summary>
    public bool ApplyPresence(IReadOnlyList<Person> connectedUsers, long version, IReadOnlyList<ScreenShareDto>? shares = null)
    {
        if (version < PresenceVersion)
        {
            return false;
        }

        PresenceVersion = version;
        if (shares is not null)
        {
            Shares = shares
                .Where(share => share is not null && share.UserId != Guid.Empty && Enum.IsDefined(share.Kind))
                .DistinctBy(ShareKey.Of)
                .ToDictionary(ShareKey.Of);
            SharingUserIds = Shares.Keys.Where(k => k.Kind == ShareKind.Screen).Select(k => k.UserId).ToHashSet();
            CameraUserIds = Shares.Keys.Where(k => k.Kind == ShareKind.Camera).Select(k => k.UserId).ToHashSet();
            OnPropertyChanged(nameof(Shares));
            OnPropertyChanged(nameof(SharingUserIds));
            OnPropertyChanged(nameof(CameraUserIds));
            OnPropertyChanged(nameof(HasShares));
        }

        var sorted = connectedUsers
            .DistinctBy(p => p.UserId)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!sorted.SequenceEqual(ConnectedUsers))
        {
            ConnectedUsers.Clear();
            foreach (var person in sorted)
            {
                ConnectedUsers.Add(person);
            }
        }

        OnPropertyChanged(nameof(ConnectedUserIds));
        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(HasConnected));
        OnPropertyChanged(nameof(ShowConnectedUsers));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CountText));

        var connected = sorted.Select(p => p.UserId).ToHashSet();
        foreach (var member in Members)
        {
            member.IsConnected = connected.Contains(member.Person.UserId);
        }

        SortMembers();
        return true;
    }

    public void SetMembers(IEnumerable<GroupMember> members)
    {
        Members.Clear();
        foreach (var member in members)
        {
            Members.Add(new GroupMemberItem(member.Person, member.IsConnected, member.IsOwner));
        }

        SortMembers();
        MemberIds = Members.Select(m => m.Person.UserId).ToHashSet();
        OnPropertyChanged(nameof(MemberIds));
        HasLoadedMembers = true;
    }

    /// <summary>Conectados primeiro, depois por nome.</summary>
    private void SortMembers()
    {
        var sorted = Members
            .OrderByDescending(m => m.IsConnected)
            .ThenBy(m => m.Person.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Members.IndexOf(sorted[i]);
            if (current != i)
            {
                Members.Move(current, i);
            }
        }
    }
}

public sealed class GroupMemberItem : ObservableObject
{
    private bool _isConnected;

    public GroupMemberItem(Person person, bool isConnected, bool isOwner = false)
    {
        Person = person;
        _isConnected = isConnected;
        IsOwner = isOwner;
    }

    public Person Person { get; }

    /// <summary>Quem criou o grupo (marcado com uma coroa na lista de integrantes).</summary>
    public bool IsOwner { get; }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }
}
