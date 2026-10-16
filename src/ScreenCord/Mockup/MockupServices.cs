using System.Runtime.InteropServices;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Security;
using ScreenCord.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ScreenCord.Mockup;

public static class MockupServiceCollectionExtensions
{
    /// <summary>
    /// Troca login, perfil, amigos, grupos, tempo real e compartilhamento de tela por versões em memória com
    /// dados falsos (<see cref="MockupWorld"/>): o app abre sem Discord e sem API. Registrado depois do
    /// <c>AddScreenCord</c>, prevalece sobre os serviços reais.
    /// </summary>
    public static IServiceCollection AddScreenCordMockup(this IServiceCollection services)
    {
        services.AddSingleton<MockupWorld>();
        services.AddSingleton<ISessionManager, MockupSessionManager>();
        services.AddSingleton<IProfileService, MockupProfileService>();
        services.AddSingleton<IFriendsService, MockupFriendsService>();
        services.AddSingleton<IGroupsService, MockupGroupsService>();
        services.AddSingleton<MockupGroupsRealtime>();
        services.AddSingleton<IGroupsRealtime>(sp => sp.GetRequiredService<MockupGroupsRealtime>());
        services.AddSingleton<IScreenShareService, MockupScreenShareService>();
        return services;
    }
}

/// <summary>Já começa conectado; "Conectar" entra na hora, sem navegador.</summary>
public sealed class MockupSessionManager : ISessionManager
{
    private bool _signedIn = true;

    public Task<bool> HasSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(_signedIn);

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        _signedIn = true;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("mockup");

    public Task<string> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult("mockup");

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _signedIn = false;
        return Task.CompletedTask;
    }
}

public sealed class MockupProfileService(MockupWorld world) : IProfileService
{
    public Task<UserProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var me = world.Me;
        var avatar = AvatarUrlBuilder.Build(new DiscordUser { Id = me.DiscordId, Discriminator = "0" });
        return Task.FromResult(new UserProfile(me.DiscordId, me.Username, me.GlobalName, "0", avatar, HasCustomAvatar: false));
    }
}

public sealed class MockupFriendsService(MockupWorld world) : IFriendsService
{
    public Task<IReadOnlyList<Person>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            return Task.FromResult(People(world.Friends));
        }
    }

    public Task<FriendRequests> GetRequestsAsync(CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            return Task.FromResult(new FriendRequests(People(world.Incoming), People(world.Outgoing)));
        }
    }

    public Task<IReadOnlyList<Person>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            var term = query.Trim();
            var ids = world.Users
                .Where(u => u.Id != world.Me.Id &&
                            (u.Username.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                             (u.GlobalName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Select(u => u.Id);
            return Task.FromResult(People(ids));
        }
    }

    public Task SendRequestAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            world.GetUser(userId);
            if (world.Incoming.Remove(userId))
            {
                // Como a API: pedir quem já pediu você aceita a amizade.
                world.Friends.Add(userId);
            }
            else if (!world.Friends.Contains(userId))
            {
                world.Outgoing.Add(userId);
            }
        }

        return Task.CompletedTask;
    }

    public Task AcceptRequestAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            if (!world.Incoming.Remove(userId))
            {
                throw MockupWorld.Error("Essa solicitação não existe mais.");
            }

            world.Friends.Add(userId);
        }

        return Task.CompletedTask;
    }

    public Task DeleteRequestAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            world.Incoming.Remove(userId);
            world.Outgoing.Remove(userId);
        }

        return Task.CompletedTask;
    }

    public Task RemoveFriendAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            world.Friends.Remove(userId);
        }

        return Task.CompletedTask;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void ForgetSession()
    {
    }

    private IReadOnlyList<Person> People(IEnumerable<Guid> ids) =>
        CentralApiClient.ToPeople(ids.Select(id => world.ToUserDto(world.GetUser(id))))
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

public sealed class MockupGroupsService(MockupWorld world, MockupGroupsRealtime realtime) : IGroupsService
{
    public Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            IReadOnlyList<GroupDto> groups = world.Groups.Where(g => g.Members.Contains(world.Me.Id)).Select(world.ToGroupDto).ToList();
            return Task.FromResult(groups);
        }
    }

    public Task<GroupDetails> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            var group = world.GetGroup(groupId);
            var members = new List<GroupMember>();
            foreach (var id in group.Members)
            {
                var user = world.GetUser(id);
                if (CentralApiClient.ToPerson(user.Id, user.DiscordId, user.Username, user.GlobalName, "0", null, RelationshipStatus.None) is { } person)
                {
                    members.Add(new GroupMember(person, group.Connected.Contains(id), id == group.OwnerId));
                }
            }

            return Task.FromResult(new GroupDetails(group.Id, group.Name, members, group.Version, CanDelete: group.OwnerId == world.Me.Id));
        }
    }

    public Task<GroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            if (name.Length > GroupsHubContract.MaxNameLength)
            {
                throw MockupWorld.Error($"O nome do grupo pode ter no máximo {GroupsHubContract.MaxNameLength} caracteres.");
            }

            return Task.FromResult(world.ToGroupDto(world.AddGroup(name, world.Me.Id, [])));
        }
    }

    public Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default)
    {
        GroupDto changed;
        lock (world.Lock)
        {
            var group = world.GetGroup(groupId);
            if (!world.Friends.Contains(userId))
            {
                throw MockupWorld.Error("Só é possível adicionar amigos ao grupo.");
            }

            if (group.Members.Contains(userId))
            {
                throw MockupWorld.Error("Essa pessoa já está no grupo.");
            }

            if (group.Members.Count >= GroupsHubContract.MaxMembers)
            {
                throw MockupWorld.Error($"Um grupo pode ter no máximo {GroupsHubContract.MaxMembers} integrantes.");
            }

            group.Members.Add(userId);
            changed = world.ToGroupDto(group);
        }

        realtime.RaiseGroupChanged(changed);
        return Task.CompletedTask;
    }

    public async Task LeaveGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        Guid? current;
        lock (world.Lock)
        {
            current = world.CurrentGroupId;
        }

        if (current == groupId)
        {
            await realtime.LeaveAsync(cancellationToken);
        }

        lock (world.Lock)
        {
            var group = world.GetGroup(groupId);
            group.Members.Remove(world.Me.Id);
            if (group.Members.Count == 0)
            {
                world.Groups.Remove(group);
            }
        }
    }

    public async Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        lock (world.Lock)
        {
            if (world.GetGroup(groupId).OwnerId != world.Me.Id)
            {
                throw MockupWorld.Error("Só quem criou o grupo pode excluí-lo.");
            }
        }

        await LeaveGroupAsync(groupId, cancellationToken);
        lock (world.Lock)
        {
            world.Groups.RemoveAll(g => g.Id == groupId);
        }
    }
}

/// <summary>
/// Tempo real simulado: conecta logo depois de <see cref="Start"/> e, enquanto conectado, de tempos em tempos
/// alguém entra ou sai de uma sala ou começa/para de compartilhar a tela, para a interface reagir a eventos.
/// </summary>
public sealed class MockupGroupsRealtime(MockupWorld world) : IGroupsRealtime, IDisposable
{
    private static readonly TimeSpan ActivityInterval = TimeSpan.FromSeconds(8);

    private readonly Lock _lock = new();
    private Timer? _activity;
    private CancellationTokenSource? _lifetime;

    public RealtimeState State { get; private set; }

    public event EventHandler<RealtimeState>? StateChanged;

    public event EventHandler<GroupPresenceDto>? PresenceChanged;

    public event EventHandler<GroupDto>? GroupChanged;

    // Sair ou excluir um grupo já tira o grupo da lista; não há outras janelas desta conta para avisar.
    public event EventHandler<Guid>? GroupRemoved
    {
        add { }
        remove { }
    }

    // As ações de amizade já recarregam as listas; ninguém de fora muda as amizades do mockup.
    public event EventHandler? FriendsChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
        CancellationTokenSource lifetime;
        lock (_lock)
        {
            if (_lifetime is not null)
            {
                return;
            }

            _lifetime = lifetime = new CancellationTokenSource();
        }

        SetState(RealtimeState.Connecting);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            SetState(RealtimeState.Connected);
            lock (_lock)
            {
                _activity ??= new Timer(_ => SimulateActivity(), null, ActivityInterval, ActivityInterval);
            }
        });
    }

    public Task<GroupPresenceDto> JoinAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        GroupPresenceDto presence;
        GroupPresenceDto? left;
        lock (world.Lock)
        {
            var group = world.GetGroup(groupId);
            left = RemoveMe();
            group.Connected.Add(world.Me.Id);
            group.Version++;
            world.CurrentGroupId = groupId;
            presence = world.ToPresence(group);
        }

        if (left is not null)
        {
            PresenceChanged?.Invoke(this, left);
        }

        return Task.FromResult(presence);
    }

    public Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        GroupPresenceDto? left;
        lock (world.Lock)
        {
            left = RemoveMe();
        }

        if (left is not null)
        {
            PresenceChanged?.Invoke(this, left);
        }

        return Task.CompletedTask;
    }

    public Task<ScreenShareDto> StartScreenShareAsync(ScreenShareStart start, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        ScreenShareDto share;
        GroupPresenceDto presence;
        lock (world.Lock)
        {
            var group = world.CurrentGroupId is { } id ? world.GetGroup(id) : throw MockupWorld.Error("Entre em uma sala para compartilhar a tela.");
            share = new ScreenShareDto(world.Me.Id, start.Width, start.Height, start.FrameRate, start.BitrateKbps, start.HasAudio);
            group.Shares[world.Me.Id] = share;
            group.Version++;
            presence = world.ToPresence(group);
        }

        PresenceChanged?.Invoke(this, presence);
        return Task.FromResult(share);
    }

    public Task StopScreenShareAsync(CancellationToken cancellationToken = default)
    {
        GroupPresenceDto? presence = null;
        lock (world.Lock)
        {
            if (world.Groups.FirstOrDefault(g => g.Id == world.CurrentGroupId) is { } group && group.Shares.Remove(world.Me.Id))
            {
                group.Version++;
                presence = world.ToPresence(group);
            }
        }

        if (presence is not null)
        {
            PresenceChanged?.Invoke(this, presence);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            _lifetime?.Cancel();
            _lifetime = null;
            _activity?.Dispose();
            _activity = null;
        }

        await LeaveAsync();
        SetState(RealtimeState.Disconnected);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _lifetime?.Cancel();
            _activity?.Dispose();
        }
    }

    internal void RaiseGroupChanged(GroupDto group) => GroupChanged?.Invoke(this, group);

    /// <summary>Um integrante qualquer (nunca a conta simulada) entra, sai ou liga/desliga o compartilhamento.</summary>
    internal void SimulateActivity()
    {
        if (State != RealtimeState.Connected)
        {
            return;
        }

        GroupPresenceDto? presence = null;
        lock (world.Lock)
        {
            var candidates = world.Groups
                .Where(g => g.Members.Contains(world.Me.Id))
                .SelectMany(g => g.Members.Where(m => m != world.Me.Id).Select(m => (Group: g, UserId: m)))
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var (group, userId) = candidates[Random.Shared.Next(candidates.Count)];
            if (!group.Connected.Contains(userId))
            {
                group.Connected.Add(userId);
            }
            else if (Random.Shared.Next(3) == 0)
            {
                if (!group.Shares.Remove(userId))
                {
                    var height = ScreenShareLimits.Heights[Random.Shared.Next(ScreenShareLimits.Heights.Length)];
                    group.Shares[userId] = new ScreenShareDto(userId, height * 16 / 9, height, 30, 4000, HasAudio: Random.Shared.Next(2) == 0);
                }
            }
            else
            {
                group.Connected.Remove(userId);
                group.Shares.Remove(userId);
            }

            group.Version++;
            presence = world.ToPresence(group);
        }

        PresenceChanged?.Invoke(this, presence);
    }

    private GroupPresenceDto? RemoveMe()
    {
        if (world.CurrentGroupId is not { } id)
        {
            return null;
        }

        world.CurrentGroupId = null;
        var group = world.Groups.FirstOrDefault(g => g.Id == id);
        if (group is null)
        {
            return null;
        }

        group.Connected.Remove(world.Me.Id);
        group.Shares.Remove(world.Me.Id);
        group.Version++;
        return world.ToPresence(group);
    }

    private void RequireConnected()
    {
        if (State != RealtimeState.Connected)
        {
            throw new DiscordException(DiscordErrorKind.Network, "Tempo real simulado desconectado.") { Source = ErrorSource.CentralApi };
        }
    }

    private void SetState(RealtimeState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}

/// <summary>
/// Compartilhar não captura nada: só anuncia na sala. Assistir mostra uma animação gerada aqui, no tamanho
/// do compartilhamento anunciado.
/// </summary>
public sealed class MockupScreenShareService(MockupWorld world, MockupGroupsRealtime realtime) : IScreenShareService
{
    public Guid? CurrentUserId => world.Me.Id;

    public async Task<IActiveScreenShare> StartAsync(Guid groupId, ShareRequest request, IActiveScreenShare? replacing = null, CancellationToken cancellationToken = default)
    {
        var settings = request.Settings;
        var width = Math.Min(ScreenShareLimits.MaxWidth, settings.Height * 16 / 9 / 2 * 2);
        (replacing as ActiveShare)?.MarkReplaced();
        var share = await realtime.StartScreenShareAsync(
            new ScreenShareStart(width, settings.Height, settings.Fps, settings.BitrateKbps, settings.ShareAudio),
            cancellationToken);
        return new ActiveShare(share, request, realtime);
    }

    public Task<IStreamViewer> WatchAsync(Guid groupId, ScreenShareDto share, CancellationToken cancellationToken = default) =>
        Task.FromResult<IStreamViewer>(new SyntheticStreamViewer(share));

    private sealed class ActiveShare(ScreenShareDto share, ShareRequest request, MockupGroupsRealtime realtime) : IActiveScreenShare
    {
        private bool _replaced;

        public ScreenShareDto Share { get; } = share;

        public ShareRequest Request { get; } = request;

        // O compartilhamento simulado só termina quando a pessoa para.
        public event EventHandler<string>? Ended
        {
            add { }
            remove { }
        }

        public void MarkReplaced() => _replaced = true;

        // Substituído, o anúncio já é do novo compartilhamento e não pode ser removido.
        public Task StopAsync() => _replaced ? Task.CompletedTask : realtime.StopScreenShareAsync();
    }

    /// <summary>Gera quadros BGRA animados (gradiente que corre e um quadrado que quica) no ritmo do compartilhamento.</summary>
    private sealed class SyntheticStreamViewer : IStreamViewer
    {
        private readonly ScreenShareDto _share;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public SyntheticStreamViewer(ScreenShareDto share)
        {
            _share = share;
            HasAudio = share.HasAudio;
            _thread = new Thread(Run) { IsBackground = true, Name = "Mockup stream" };
            _thread.Start();
        }

        public event DecodedFrameHandler? FrameReady;

        public event EventHandler<string?>? Ended
        {
            add { }
            remove { }
        }

        public bool HasAudio { get; }

        public float Volume { get; set; } = 1;

        public bool IsMuted { get; set; }

        public bool IsVideoPaused { get; set; }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _thread.Join(1000);
            _stop.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Run()
        {
            var width = Math.Max(ScreenShareLimits.MinSize, _share.Width);
            var height = Math.Max(ScreenShareLimits.MinSize, _share.Height);
            var buffer = new byte[width * height * 4];
            var row = new uint[width];
            var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _share.FrameRate));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var frame = 0L;

            while (!_stop.IsCancellationRequested)
            {
                if (!IsVideoPaused)
                {
                    Draw(buffer, row, width, height, frame);
                    FrameReady?.Invoke(buffer, width, height, (long)clock.Elapsed.TotalMicroseconds);
                }

                frame++;
                var next = interval * frame - clock.Elapsed;
                if (next > TimeSpan.Zero && _stop.Token.WaitHandle.WaitOne(next))
                {
                    break;
                }
            }
        }

        private static void Draw(byte[] buffer, uint[] row, int width, int height, long frame)
        {
            var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan());
            for (var x = 0; x < width; x++)
            {
                var t = (x + frame * 4) % (width * 2) / (double)(width * 2);
                var r = (byte)(80 + 60 * Math.Sin(t * Math.Tau));
                var g = (byte)(70 + 50 * Math.Sin(t * Math.Tau + 2));
                var b = (byte)(140 + 80 * Math.Sin(t * Math.Tau + 4));
                row[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            }

            for (var y = 0; y < height; y++)
            {
                row.CopyTo(pixels.Slice(y * width, width));
            }

            // Quadrado branco quicando: deixa claro que a imagem está "ao vivo".
            var size = height / 6;
            var bx = Bounce(frame * 5, width - size);
            var by = Bounce(frame * 3, height - size);
            for (var y = by; y < by + size; y++)
            {
                pixels.Slice(y * width + bx, size).Fill(0xFFF0F0F0u);
            }
        }

        private static int Bounce(long position, int range)
        {
            if (range <= 0)
            {
                return 0;
            }

            var p = (int)(position % (range * 2));
            return p < range ? p : range * 2 - p;
        }
    }
}
