using ScreenCord.Contracts;
using ScreenCord.Media;
using ScreenCord.Mockup;
using ScreenCord.Services;

namespace ScreenCord.Tests;

public class MockupTests
{
    private readonly MockupWorld _world = new();
    private readonly MockupGroupsRealtime _realtime;
    private readonly MockupFriendsService _friends;
    private readonly MockupGroupsService _groups;

    public MockupTests()
    {
        _realtime = new MockupGroupsRealtime(_world);
        _friends = new MockupFriendsService(_world);
        _groups = new MockupGroupsService(_world, _realtime);
    }

    [Fact]
    public async Task DadosIniciais_TemAmigosSolicitacoesEGrupos()
    {
        Assert.NotEmpty(await _friends.GetFriendsAsync());
        var requests = await _friends.GetRequestsAsync();
        Assert.NotEmpty(requests.Incoming);
        Assert.NotEmpty(requests.Outgoing);

        var groups = await _groups.GetGroupsAsync();
        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.ScreenShares is { Count: > 0 });
    }

    [Fact]
    public async Task AceitarSolicitacao_ViraAmigo()
    {
        var incoming = (await _friends.GetRequestsAsync()).Incoming[0];

        await _friends.AcceptRequestAsync(incoming.UserId);

        Assert.Contains(await _friends.GetFriendsAsync(), p => p.UserId == incoming.UserId && p.IsFriend);
        Assert.DoesNotContain((await _friends.GetRequestsAsync()).Incoming, p => p.UserId == incoming.UserId);
    }

    [Fact]
    public async Task Busca_EncontraPorNomeSemIncluirAPropriaConta()
    {
        var found = await _friends.SearchUsersAsync("a");

        Assert.NotEmpty(found);
        Assert.DoesNotContain(found, p => p.UserId == _world.Me.Id);
    }

    [Fact]
    public async Task EntrarNaSala_AnunciaPresencaECompartilhamento()
    {
        await StartConnectedAsync();
        var group = (await _groups.GetGroupsAsync())[0];

        var presence = await _realtime.JoinAsync(group.Id);
        Assert.Contains(_world.Me.Id, presence.ConnectedUserIds);

        var share = await new MockupScreenShareService(_world, _realtime).StartAsync(
            group.Id,
            new ShareRequest(new CaptureSource(CaptureSourceKind.Screen, 1, "Tela", "", 1920, 1080, 0), new ShareSettings()));
        Assert.Contains((await _groups.GetGroupAsync(group.Id)).Members, m => m.Person.UserId == _world.Me.Id && m.IsConnected);
        Assert.Contains((await _groups.GetGroupsAsync()).Single(g => g.Id == group.Id).ScreenShares!, s => s.UserId == _world.Me.Id);

        await share.StopAsync();
        Assert.DoesNotContain((await _groups.GetGroupsAsync()).Single(g => g.Id == group.Id).ScreenShares!, s => s.UserId == _world.Me.Id);
    }

    [Fact]
    public async Task DetalhesDoGrupo_MarcamQuemCriou()
    {
        await StartConnectedAsync();
        foreach (var group in await _groups.GetGroupsAsync())
        {
            var owner = Assert.Single((await _groups.GetGroupAsync(group.Id)).Members, m => m.IsOwner);
            Assert.Equal(_world.GetGroup(group.Id).OwnerId, owner.Person.UserId);
        }
    }

    [Fact]
    public async Task Assistir_GeraQuadros()
    {
        var share = new ScreenShareDto(Guid.NewGuid(), 960, 540, 30, 3500, HasAudio: false);
        await using var viewer = await new MockupScreenShareService(_world, _realtime).WatchAsync(Guid.NewGuid(), share);
        var frame = new TaskCompletionSource<(int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FrameReady += (_, width, height, _) => frame.TrySetResult((width, height));

        var (w, h) = await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((960, 540), (w, h));
    }

    [Fact]
    public async Task AdicionarQuemNaoEAmigo_EhRecusado()
    {
        var group = (await _groups.GetGroupsAsync())[0];
        var stranger = (await _friends.SearchUsersAsync("kaique"))[0];

        await Assert.ThrowsAsync<Infrastructure.DiscordException>(() => _groups.AddMemberAsync(group.Id, stranger.UserId));
    }

    private async Task StartConnectedAsync()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _realtime.StateChanged += (_, state) =>
        {
            if (state == RealtimeState.Connected)
            {
                connected.TrySetResult();
            }
        };
        _realtime.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
