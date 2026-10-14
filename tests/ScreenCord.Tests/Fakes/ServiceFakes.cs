using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Security;
using ScreenCord.Services;

namespace ScreenCord.Tests.Fakes;

public sealed class FakeOAuthService : IDiscordOAuthService
{
    public Func<CancellationToken, Task<TokenSession>> OnAuthorize { get; set; } =
        _ => throw new InvalidOperationException("AuthorizeAsync não esperado.");

    public Func<string, Task<TokenSession>> OnRefresh { get; set; } =
        _ => throw new InvalidOperationException("RefreshAsync não esperado.");

    public Func<string, Task> OnRevoke { get; set; } = _ => Task.CompletedTask;

    public List<string> RefreshCalls { get; } = [];

    public List<string> RevokeCalls { get; } = [];

    public Task<TokenSession> AuthorizeAsync(CancellationToken cancellationToken = default) => OnAuthorize(cancellationToken);

    public Task<TokenSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        RefreshCalls.Add(refreshToken);
        return OnRefresh(refreshToken);
    }

    public Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        RevokeCalls.Add(token);
        return OnRevoke(token);
    }
}

public sealed class FakeSessionManager : ISessionManager
{
    public bool HasSession { get; set; }

    public string AccessToken { get; set; } = "access-1";

    public Func<CancellationToken, Task> OnSignIn { get; set; } = _ => Task.CompletedTask;

    public Func<Task<string>> OnRefresh { get; set; } = () => Task.FromResult("access-2");

    public int RefreshCount { get; private set; }

    public int SignOutCount { get; private set; }

    public Task<bool> HasSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(HasSession);

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        await OnSignIn(cancellationToken);
        HasSession = true;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        HasSession
            ? Task.FromResult(AccessToken)
            : Task.FromException<string>(new DiscordException(DiscordErrorKind.Unauthorized, "sem sessão"));

    public Task<string> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return OnRefresh();
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOutCount++;
        HasSession = false;
        return Task.CompletedTask;
    }
}

public sealed class FakeApiClient : IDiscordApiClient
{
    private readonly Queue<Func<DiscordUser>> _results = new();

    public List<string> Tokens { get; } = [];

    public FakeApiClient Returns(DiscordUser user)
    {
        _results.Enqueue(() => user);
        return this;
    }

    public FakeApiClient Throws(Exception exception)
    {
        _results.Enqueue(() => throw exception);
        return this;
    }

    public Task<DiscordUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        Tokens.Add(accessToken);
        try
        {
            return Task.FromResult(_results.Dequeue()());
        }
        catch (Exception ex)
        {
            return Task.FromException<DiscordUser>(ex);
        }
    }
}

public sealed class FakeProfileService : IProfileService
{
    public Func<Task<UserProfile>> OnGet { get; set; } = () => Task.FromResult(SampleProfile);

    public int Calls { get; private set; }

    public static UserProfile SampleProfile { get; } = new(
        "80351110224678912",
        "nelly",
        "Nelly",
        "0",
        "https://cdn.discordapp.com/embed/avatars/0.png",
        HasCustomAvatar: false);

    public Task<UserProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return OnGet();
    }
}

/// <summary>API central em memória: as ações alteram as listas, como o servidor faria.</summary>
public sealed class FakeFriendsService : IFriendsService
{
    public List<Person> Friends { get; } = [];

    public List<Person> Incoming { get; } = [];

    public List<Person> Outgoing { get; } = [];

    /// <summary>Todos os outros usuários do sistema.</summary>
    public List<Person> Directory { get; } = [];

    /// <summary>Quando definido, toda chamada falha com este erro.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Quando definido, só a próxima ação (não as leituras) falha.</summary>
    public Exception? NextActionFailure { get; set; }

    public List<string> Calls { get; } = [];

    public int SignOutCount { get; private set; }

    public int ForgetCount { get; private set; }

    public static Person Create(string username, string? globalName = null, RelationshipStatus relationship = RelationshipStatus.None) =>
        new(Guid.NewGuid(), "1" + Math.Abs(username.GetHashCode()), username, globalName, "https://cdn.discordapp.com/embed/avatars/0.png", relationship);

    public Task<IReadOnlyList<Person>> GetFriendsAsync(CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<Person>>(() => Friends.ToList());

    public Task<FriendRequests> GetRequestsAsync(CancellationToken cancellationToken = default) =>
        Read(() => new FriendRequests(Incoming.ToList(), Outgoing.ToList()));

    public Task<IReadOnlyList<Person>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        Calls.Add("search:" + query);
        return Read<IReadOnlyList<Person>>(() => Directory
            .Where(p => p.Username.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => p with { Relationship = RelationshipOf(p) })
            .ToList());
    }

    public Task SendRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Act("add", userId, p => Outgoing.Add(p with { Relationship = RelationshipStatus.PendingOutgoing }));

    public Task AcceptRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Act("accept", userId, p =>
        {
            Incoming.RemoveAll(x => x.UserId == userId);
            Friends.Add(p with { Relationship = RelationshipStatus.Friend });
        });

    public Task DeleteRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Act("decline", userId, _ =>
        {
            Incoming.RemoveAll(x => x.UserId == userId);
            Outgoing.RemoveAll(x => x.UserId == userId);
        });

    public Task RemoveFriendAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Act("remove", userId, _ => Friends.RemoveAll(x => x.UserId == userId));

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOutCount++;
        return Task.CompletedTask;
    }

    public void ForgetSession() => ForgetCount++;

    private RelationshipStatus RelationshipOf(Person person) =>
        Friends.Any(x => x.UserId == person.UserId) ? RelationshipStatus.Friend
        : Incoming.Any(x => x.UserId == person.UserId) ? RelationshipStatus.PendingIncoming
        : Outgoing.Any(x => x.UserId == person.UserId) ? RelationshipStatus.PendingOutgoing
        : RelationshipStatus.None;

    private Task<T> Read<T>(Func<T> read) =>
        Failure is null ? Task.FromResult(read()) : Task.FromException<T>(Failure);

    private Task Act(string name, Guid userId, Action<Person> apply)
    {
        Calls.Add($"{name}:{userId}");
        var failure = Failure ?? NextActionFailure;
        NextActionFailure = null;
        if (failure is not null)
        {
            return Task.FromException(failure);
        }

        var person = Directory.Concat(Incoming).Concat(Outgoing).Concat(Friends).First(p => p.UserId == userId);
        apply(person);
        return Task.CompletedTask;
    }
}

/// <summary>Grupos em memória: o usuário atual é <see cref="Me"/>; as ações alteram os dados como o servidor faria.</summary>
public sealed class FakeGroupsService : IGroupsService
{
    public static readonly Person Me = FakeFriendsService.Create("eu", "Eu");

    public Dictionary<Guid, (string Name, List<Person> Members)> Groups { get; } = [];

    /// <summary>Quem está conectado em cada grupo e a versão da presença, como o servidor informaria.</summary>
    public Dictionary<Guid, List<Person>> Connected { get; } = [];

    public long PresenceVersion { get; set; } = 1;

    public Exception? Failure { get; set; }

    public List<string> Calls { get; } = [];

    public Guid Add(string name, params Person[] others)
    {
        var id = Guid.NewGuid();
        Groups[id] = (name, [Me, .. others]);
        return id;
    }

    public GroupDto Dto(Guid id) =>
        new(id, Groups[id].Name, Groups[id].Members.Count, Connected.GetValueOrDefault(id, []).Select(ToDto).ToList(), PresenceVersion);

    public static PresenceUserDto ToDto(Person person) =>
        new(person.UserId, person.DiscordId, person.Username, person.GlobalName, "0", null);

    public static GroupPresenceDto Presence(Guid groupId, long version, params Person[] connected) =>
        new(groupId, connected.Select(ToDto).ToList(), version);

    public Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<GroupDto>>("list", () => Groups.Keys.Select(Dto).ToList());

    public Task<GroupDetails> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
        Run("get:" + Groups[groupId].Name, () =>
        {
            var connected = Connected.GetValueOrDefault(groupId, []);
            var members = Groups[groupId].Members.Select(p => new GroupMember(p, connected.Any(c => c.UserId == p.UserId))).ToList();
            return new GroupDetails(groupId, Groups[groupId].Name, members, PresenceVersion);
        });

    public Task<GroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken = default) =>
        Run("create:" + name, () => Dto(Add(name)));

    public Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default) =>
        Run<object?>($"add:{userId}", () =>
        {
            Groups[groupId].Members.Add(FakeFriendsService.Create("x") with { UserId = userId });
            return null;
        });

    public Task LeaveGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
        Run<object?>("leave:" + Groups[groupId].Name, () => Groups.Remove(groupId));

    private Task<T> Run<T>(string call, Func<T> action)
    {
        Calls.Add(call);
        return Failure is null ? Task.FromResult(action()) : Task.FromException<T>(Failure);
    }
}

/// <summary>Hub simulado: os testes disparam os eventos do servidor diretamente.</summary>
public sealed class FakeGroupsRealtime : IGroupsRealtime
{
    public RealtimeState State { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public List<string> Calls { get; } = [];

    public Func<Guid, GroupPresenceDto?> OnJoin { get; set; } = id => FakeGroupsService.Presence(id, 100, FakeGroupsService.Me);

    public event EventHandler<RealtimeState>? StateChanged;

    public event EventHandler<GroupPresenceDto>? PresenceChanged;

    public event EventHandler<GroupDto>? GroupChanged;

    public event EventHandler<Guid>? GroupRemoved;

    public void Start() => StartCount++;

    public Task<GroupPresenceDto> JoinAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"join:{groupId}");
        return OnJoin(groupId) is { } presence
            ? Task.FromResult(presence)
            : Task.FromException<GroupPresenceDto>(new DiscordException(DiscordErrorKind.Network, "offline") { Source = ErrorSource.CentralApi });
    }

    public Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("leave");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        SetState(RealtimeState.Disconnected);
        return Task.CompletedTask;
    }

    public void SetState(RealtimeState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void RaisePresence(GroupPresenceDto presence) => PresenceChanged?.Invoke(this, presence);

    public void RaiseGroupChanged(GroupDto group) => GroupChanged?.Invoke(this, group);

    public void RaiseGroupRemoved(Guid groupId) => GroupRemoved?.Invoke(this, groupId);
}
