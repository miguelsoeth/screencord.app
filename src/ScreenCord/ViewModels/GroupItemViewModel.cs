using System.Collections.ObjectModel;
using ScreenCord.Contracts;
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
    private bool _isExpanded = true;
    private string? _membersError;

    public GroupItemViewModel(GroupsViewModel owner, GroupDto group)
    {
        _owner = owner;
        Id = group.Id;
        _name = group.Name;
        _memberCount = group.MemberCount;
        ApplyPresence(CentralApiClient.ToPeople(group.ConnectedUsers), group.PresenceVersion, group.ScreenShares);

        WatchCommand = new AsyncRelayCommand<Person>(person => _owner.WatchAsync(this, person));
        AddMemberCommand = new AsyncRelayCommand<Person>(person => _owner.AddMemberAsync(this, person));
        LeaveGroupCommand = new AsyncRelayCommand(() => _owner.LeaveGroupAsync(this));
        ConfirmLeaveCommand = new RelayCommand(() => IsConfirmingLeave = true);
        CancelLeaveCommand = new RelayCommand(() => IsConfirmingLeave = false);
        DeleteGroupCommand = new AsyncRelayCommand(() => _owner.DeleteGroupAsync(this));
        ConfirmDeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public Guid Id { get; }

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

    /// <summary>Compartilhamentos de tela ativos na sala, por quem compartilha.</summary>
    public IReadOnlyDictionary<Guid, ScreenShareDto> Shares { get; private set; } = new Dictionary<Guid, ScreenShareDto>();

    /// <summary>Quem está compartilhando a tela agora (marcado "AO VIVO" na lista de conectados).</summary>
    public IReadOnlySet<Guid> SharingUserIds { get; private set; } = new HashSet<Guid>();

    public bool HasShares => Shares.Count > 0;

    /// <summary>Clicar em "AO VIVO" ao lado de alguém: abre a tela dessa pessoa.</summary>
    public AsyncRelayCommand<Person> WatchCommand { get; }

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
    public string Summary =>
        $"{MemberCount} {(MemberCount == 1 ? "integrante" : "integrantes")} · " +
        (ConnectedCount == 0 ? "ninguém conectado" : $"{ConnectedCount} {(ConnectedCount == 1 ? "conectado" : "conectados")}");

    public bool IsMembersOpen
    {
        get => _isMembersOpen;
        set
        {
            if (SetProperty(ref _isMembersOpen, value))
            {
                IsConfirmingLeave = false;
                IsConfirmingDelete = false;
                if (value)
                {
                    _ = _owner.LoadMembersAsync(this);
                }
            }
        }
    }

    public ObservableCollection<GroupMemberItem> Members { get; } = [];

    /// <summary>Amigos de quem está usando o aplicativo que ainda não estão no grupo.</summary>
    public ObservableCollection<Person> AddableFriends { get; } = [];

    public bool IsLoadingMembers
    {
        get => _isLoadingMembers;
        set
        {
            if (SetProperty(ref _isLoadingMembers, value))
            {
                OnPropertyChanged(nameof(ShowNoAddableFriends));
            }
        }
    }

    public bool HasLoadedMembers
    {
        get => _hasLoadedMembers;
        set
        {
            if (SetProperty(ref _hasLoadedMembers, value))
            {
                OnPropertyChanged(nameof(ShowNoAddableFriends));
            }
        }
    }

    public bool ShowNoAddableFriends => HasLoadedMembers && !IsLoadingMembers && AddableFriends.Count == 0;

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

                OnPropertyChanged(nameof(ShowGroupActions));
            }
        }
    }

    /// <summary>Quem está usando o aplicativo criou o grupo e pode excluí-lo (vem com os integrantes).</summary>
    public bool CanDelete
    {
        get => _canDelete;
        set => SetProperty(ref _canDelete, value);
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

                OnPropertyChanged(nameof(ShowGroupActions));
            }
        }
    }

    /// <summary>Botões "Sair do grupo" e "Excluir grupo", escondidos enquanto uma das confirmações está aberta.</summary>
    public bool ShowGroupActions => !IsConfirmingLeave && !IsConfirmingDelete;

    public AsyncRelayCommand<Person> AddMemberCommand { get; }

    public AsyncRelayCommand LeaveGroupCommand { get; }

    public RelayCommand ConfirmLeaveCommand { get; }

    public RelayCommand CancelLeaveCommand { get; }

    public AsyncRelayCommand DeleteGroupCommand { get; }

    public RelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

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
                .Where(share => share is not null && share.UserId != Guid.Empty)
                .DistinctBy(share => share.UserId)
                .ToDictionary(share => share.UserId);
            SharingUserIds = Shares.Keys.ToHashSet();
            OnPropertyChanged(nameof(Shares));
            OnPropertyChanged(nameof(SharingUserIds));
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

    public void SetMembers(IEnumerable<GroupMember> members, IEnumerable<Person> addableFriends)
    {
        Members.Clear();
        foreach (var member in members)
        {
            Members.Add(new GroupMemberItem(member.Person, member.IsConnected, member.IsOwner));
        }

        SortMembers();

        AddableFriends.Clear();
        foreach (var friend in addableFriends)
        {
            AddableFriends.Add(friend);
        }

        HasLoadedMembers = true;
        OnPropertyChanged(nameof(ShowNoAddableFriends));
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
