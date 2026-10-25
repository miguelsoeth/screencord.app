using ScreenCord.Contracts;
using ScreenCord.Infrastructure;

namespace ScreenCord.Mockup;

/// <summary>
/// Estado em memória do ambiente de mockup (DOTNET_ENVIRONMENT=Mockup): usuários, amizades, solicitações e
/// grupos falsos, compartilhados pelos serviços simulados. Nada vai para a rede; tudo volta ao estado
/// inicial quando o app fecha.
/// </summary>
public sealed class MockupWorld
{
    public sealed record User(Guid Id, string DiscordId, string Username, string? GlobalName);

    public sealed class Group(Guid id, string name, Guid ownerId)
    {
        public Guid Id { get; } = id;

        public string Name { get; } = name;

        public Guid OwnerId { get; } = ownerId;

        public List<Guid> Members { get; } = [];

        public List<Guid> Connected { get; } = [];

        /// <summary>Uma transmissão por pessoa e tipo (tela e câmera podem estar no ar juntas).</summary>
        public Dictionary<(Guid UserId, ShareKind Kind), ScreenShareDto> Shares { get; } = [];

        public void RemoveSharesOf(Guid userId)
        {
            foreach (var key in Shares.Keys.Where(k => k.UserId == userId).ToList())
            {
                Shares.Remove(key);
            }
        }

        public GroupSettings Settings { get; set; } = GroupSettings.Default;

        public long Version { get; set; } = 1;
    }

    private readonly Dictionary<Guid, User> _users = [];

    public MockupWorld()
    {
        Me = AddUser("voce.dev", "Você (mockup)");
        var ana = AddUser("ana.souza", "Ana Souza");
        var bruno = AddUser("brunao", "Bruno");
        var carla = AddUser("carla_m", "Carla Mendes");
        var diego = AddUser("diegofps", null);
        var elisa = AddUser("elisa", "Elisa ✨");
        var fabio = AddUser("fabio.r", "Fábio Rocha");
        var gabi = AddUser("gabi", "Gabi");
        var heitor = AddUser("heitor99", "Heitor");
        AddUser("igor.lima", "Igor Lima");
        AddUser("julia", "Júlia");
        AddUser("kaique", null);
        AddUser("larissa.p", "Larissa Pereira");

        Friends.UnionWith([ana.Id, bruno.Id, carla.Id, diego.Id, elisa.Id, fabio.Id]);
        Incoming.UnionWith([gabi.Id, heitor.Id]);
        Outgoing.Add(Users.Single(u => u.Username == "igor.lima").Id);

        var games = AddGroup("Jogatina de sexta", Me.Id, [ana, bruno, carla, diego]);
        games.Connected.AddRange([bruno.Id, carla.Id]);
        games.Shares[(bruno.Id, ShareKind.Screen)] = new ScreenShareDto(bruno.Id, 960, 540, 60, 3000, HasAudio: true);
        games.Shares[(carla.Id, ShareKind.Camera)] = new ScreenShareDto(carla.Id, 640, 360, 30, 1500, false, ShareKind.Camera);

        var work = AddGroup("Trabalho", ana.Id, [Me, ana, elisa, fabio]);
        work.Connected.Add(elisa.Id);

        AddGroup("Família", Me.Id, [carla]);
    }

    /// <summary>Protege todo o estado: os serviços são chamados da interface e do simulador de atividade.</summary>
    public Lock Lock { get; } = new();

    public User Me { get; }

    public IEnumerable<User> Users => _users.Values;

    public HashSet<Guid> Friends { get; } = [];

    /// <summary>Solicitações recebidas pela conta simulada.</summary>
    public HashSet<Guid> Incoming { get; } = [];

    /// <summary>Solicitações enviadas pela conta simulada.</summary>
    public HashSet<Guid> Outgoing { get; } = [];

    public List<Group> Groups { get; } = [];

    /// <summary>Sala em que a conta simulada está (null fora de sala).</summary>
    public Guid? CurrentGroupId { get; set; }

    public User GetUser(Guid id) =>
        _users.TryGetValue(id, out var user) ? user : throw Error("Usuário não encontrado.");

    public Group GetGroup(Guid id) =>
        Groups.FirstOrDefault(g => g.Id == id && g.Members.Contains(Me.Id)) ?? throw Error("Grupo não encontrado.");

    public RelationshipStatus RelationshipWith(Guid id) =>
        Friends.Contains(id) ? RelationshipStatus.Friend :
        Incoming.Contains(id) ? RelationshipStatus.PendingIncoming :
        Outgoing.Contains(id) ? RelationshipStatus.PendingOutgoing :
        RelationshipStatus.None;

    public UserDto ToUserDto(User user) =>
        new(user.Id, user.DiscordId, user.Username, user.GlobalName, "0", null, RelationshipWith(user.Id));

    public static PresenceUserDto ToPresenceDto(User user) =>
        new(user.Id, user.DiscordId, user.Username, user.GlobalName, "0", null);

    public GroupDto ToGroupDto(Group group) =>
        new(group.Id, group.Name, group.Members.Count, group.Connected.Select(id => ToPresenceDto(GetUser(id))).ToList(), group.Version, group.Shares.Values.ToList(), group.Settings);

    public GroupPresenceDto ToPresence(Group group) =>
        new(group.Id, group.Connected.Select(id => ToPresenceDto(GetUser(id))).ToList(), group.Version, group.Shares.Values.ToList());

    public Group AddGroup(string name, Guid ownerId, IEnumerable<User> members)
    {
        var group = new Group(Guid.NewGuid(), name, ownerId);
        group.Members.AddRange(members.Select(m => m.Id).Append(Me.Id).Distinct());
        Groups.Add(group);
        return group;
    }

    /// <summary>Erro no formato do que a API central devolve, para a interface mostrar a mensagem.</summary>
    public static DiscordException Error(string message) =>
        new(DiscordErrorKind.Rejected, message) { Source = ErrorSource.CentralApi };

    private User AddUser(string username, string? globalName)
    {
        // Snowflakes válidos e diferentes entre si: o avatar padrão do Discord varia com o id.
        var discordId = (100_000_000_000_000_000UL + ((ulong)_users.Count << 22) * 7919).ToString();
        var user = new User(Guid.NewGuid(), discordId, username, globalName);
        _users.Add(user.Id, user);
        return user;
    }
}
