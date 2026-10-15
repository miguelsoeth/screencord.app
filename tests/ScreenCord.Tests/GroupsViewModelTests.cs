using ScreenCord.Contracts;
using ScreenCord.Models;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;

namespace ScreenCord.Tests;

public class GroupsViewModelTests
{
    private readonly FakeGroupsService _groups = new();
    private readonly FakeGroupsRealtime _realtime = new();
    private readonly FakeFriendsService _friends = new();
    private readonly Person _bruno = FakeFriendsService.Create("bruno", "Bruno", RelationshipStatus.Friend);
    private readonly Person _carla = FakeFriendsService.Create("carla", "Carla", RelationshipStatus.Friend);

    private GroupsViewModel Create() => new(_groups, _realtime, new FakeScreenShareService(), new FakeScreenShareUi());

    private async Task<GroupsViewModel> LoadedAsync()
    {
        var vm = Create();
        await vm.LoadAsync();
        _realtime.SetState(RealtimeState.Connected);
        return vm;
    }

    [Fact]
    public async Task Carregar_AbreConexaoEmTempoRealEListaGruposEmOrdemAlfabetica()
    {
        _groups.Add("Zeta");
        _groups.Add("alfa", _bruno);
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(1, _realtime.StartCount);
        Assert.Equal(["alfa", "Zeta"], vm.Groups.Select(g => g.Name));
        Assert.Equal(2, vm.Groups[0].MemberCount);
        Assert.False(vm.ShowNoGroups);
        Assert.Equal("Conectando ao servidor…", vm.RealtimeStatusText);
    }

    [Fact]
    public async Task SemGrupos_MostraAvisoDeListaVazia()
    {
        var vm = await LoadedAsync();

        Assert.Empty(vm.Groups);
        Assert.True(vm.ShowNoGroups);
    }

    [Fact]
    public async Task ClicarNoNome_ConectaNaSalaEMarcaComoAtual()
    {
        _groups.Add("Amigos", _bruno);
        var vm = await LoadedAsync();
        var group = vm.Groups[0];

        await vm.JoinCommand.ExecuteAsync(group);

        Assert.Contains($"join:{group.Id}", _realtime.Calls);
        Assert.Same(group, vm.CurrentGroup);
        Assert.True(group.IsCurrent);
        Assert.True(vm.IsInGroup);
        Assert.Equal(1, group.ConnectedCount);
        Assert.True(group.HasConnected);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task EntrarEmOutroGrupo_TrocaASalaAtual()
    {
        _groups.Add("A");
        _groups.Add("B");
        var vm = await LoadedAsync();
        await vm.JoinCommand.ExecuteAsync(vm.Groups[0]);

        await vm.JoinCommand.ExecuteAsync(vm.Groups[1]);

        Assert.False(vm.Groups[0].IsCurrent);
        Assert.True(vm.Groups[1].IsCurrent);
        Assert.Same(vm.Groups[1], vm.CurrentGroup);
    }

    [Fact]
    public async Task FalhaAoEntrar_MostraErroENaoMarcaComoAtual()
    {
        _groups.Add("Amigos");
        _realtime.OnJoin = _ => null;
        var vm = await LoadedAsync();

        await vm.JoinCommand.ExecuteAsync(vm.Groups[0]);

        Assert.Null(vm.CurrentGroup);
        Assert.True(vm.HasError);
        Assert.False(vm.Groups[0].IsJoining);
    }

    [Fact]
    public async Task SairDaSala_AvisaOServidorELimpaASalaAtual()
    {
        _groups.Add("Amigos");
        var vm = await LoadedAsync();
        await vm.JoinCommand.ExecuteAsync(vm.Groups[0]);

        await vm.LeaveCurrentCommand.ExecuteAsync();

        Assert.Contains("leave", _realtime.Calls);
        Assert.Null(vm.CurrentGroup);
        Assert.False(vm.Groups[0].IsCurrent);
        Assert.False(vm.LeaveCurrentCommand.CanExecute(null));
    }

    [Fact]
    public async Task PresencaDeOutroIntegrante_AtualizaEmTempoReal_EIgnoraNotificacaoAntiga()
    {
        var id = _groups.Add("Amigos", _bruno);
        var vm = await LoadedAsync();
        var group = vm.Groups[0];

        _realtime.RaisePresence(FakeGroupsService.Presence(id, 10, _bruno));
        Assert.Equal(1, group.ConnectedCount);
        Assert.Contains("1 conectado", group.Summary);

        _realtime.RaisePresence(FakeGroupsService.Presence(id, 9));
        Assert.Equal(1, group.ConnectedCount);

        _realtime.RaisePresence(FakeGroupsService.Presence(id, 11));
        Assert.False(group.HasConnected);
    }

    [Fact]
    public async Task Linha_MostraConectadosSobreIntegrantes_EListaQuemEstaConectado()
    {
        var id = _groups.Add("Amigos", _bruno);
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        Assert.Equal("0/2", group.CountText);
        Assert.False(group.ShowConnectedUsers);

        _realtime.RaisePresence(FakeGroupsService.Presence(id, 10, _bruno));

        Assert.Equal("1/2", group.CountText);
        Assert.Equal(["Bruno"], group.ConnectedUsers.Select(p => p.DisplayName));
        Assert.True(group.ShowConnectedUsers);

        _realtime.RaisePresence(FakeGroupsService.Presence(id, 11, _bruno, FakeGroupsService.Me));
        Assert.Equal("2/2", group.CountText);
        Assert.Equal(["Bruno", "Eu"], group.ConnectedUsers.Select(p => p.DisplayName));
    }

    [Fact]
    public async Task Recolher_EscondeOsConectadosSemMudarAContagem()
    {
        var id = _groups.Add("Amigos", _bruno);
        _groups.Connected[id] = [_bruno];
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        Assert.True(group.ShowConnectedUsers);

        group.ToggleExpandCommand.Execute(null);

        Assert.False(group.IsExpanded);
        Assert.False(group.ShowConnectedUsers);
        Assert.Equal("1/2", group.CountText);

        // A escolha se mantém quando a lista é recarregada (ex.: após reconectar).
        _realtime.SetState(RealtimeState.Connected);
        _realtime.SetState(RealtimeState.Reconnecting);
        _realtime.SetState(RealtimeState.Connected);
        Assert.False(vm.Groups[0].IsExpanded);

        group.ToggleExpandCommand.Execute(null);
        Assert.True(group.ShowConnectedUsers);
    }

    [Fact]
    public async Task PopupDeIntegrantes_MostraIntegrantesConectadosPrimeiro()
    {
        var id = _groups.Add("Amigos", _bruno);
        _groups.Connected[id] = [_bruno];
        _friends.Friends.AddRange([_bruno, _carla]);
        var vm = await LoadedAsync();
        var group = vm.Groups[0];

        group.IsMembersOpen = true;

        Assert.False(group.IsLoadingMembers);
        Assert.Equal(["Bruno", "Eu"], group.Members.Select(m => m.Person.DisplayName));
        Assert.True(group.Members[0].IsConnected);
        Assert.False(group.Members[1].IsConnected);
        Assert.Contains(_bruno.UserId, group.MemberIds);
        Assert.DoesNotContain(_carla.UserId, group.MemberIds);
    }

    [Fact]
    public async Task PopupDeIntegrantes_DetalhesComPresencaAntiga_MantemAPresencaMaisNova()
    {
        var id = _groups.Add("Amigos", _bruno);
        _groups.PresenceVersion = 1;
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        _realtime.RaisePresence(FakeGroupsService.Presence(id, 5, FakeGroupsService.Me));

        group.IsMembersOpen = true;

        Assert.True(group.Members.Single(m => m.Person.UserId == FakeGroupsService.Me.UserId).IsConnected);
        Assert.Equal(1, group.ConnectedCount);
    }

    [Fact]
    public async Task PresencaComPopupAberto_AtualizaOStatusDosIntegrantes()
    {
        var id = _groups.Add("Amigos", _bruno);
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        group.IsMembersOpen = true;
        var bruno = group.Members.Single(m => m.Person.UserId == _bruno.UserId);
        Assert.False(bruno.IsConnected);

        _realtime.RaisePresence(FakeGroupsService.Presence(id, 50, _bruno));

        Assert.True(bruno.IsConnected);
        Assert.Same(bruno, group.Members[0]);
    }

    [Fact]
    public async Task AdicionarAmigo_ChamaAApiERecarregaOsIntegrantes()
    {
        _groups.Add("Amigos");
        _friends.Friends.Add(_carla);
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        group.IsMembersOpen = true;

        await group.AddMemberCommand.ExecuteAsync(_carla);

        Assert.Contains($"add:{_carla.UserId}", _groups.Calls);
        Assert.Equal(2, group.MemberCount);
        Assert.Contains(_carla.UserId, group.MemberIds);
    }

    [Fact]
    public async Task AdicionadoPorOutraPessoa_GrupoApareceNaLista()
    {
        var vm = await LoadedAsync();
        var id = _groups.Add("Da Ana", _bruno);

        _realtime.RaiseGroupChanged(_groups.Dto(id));

        Assert.Equal("Da Ana", Assert.Single(vm.Groups).Name);
        Assert.False(vm.ShowNoGroups);
    }

    [Fact]
    public async Task NovoIntegrante_AtualizaAContagemSemDuplicarOGrupo()
    {
        var id = _groups.Add("Amigos");
        var vm = await LoadedAsync();
        _groups.Groups[id].Members.Add(_bruno);

        _realtime.RaiseGroupChanged(_groups.Dto(id));

        Assert.Equal(2, Assert.Single(vm.Groups).MemberCount);
    }

    [Fact]
    public async Task RemovidoDoGrupo_SaiDaListaEDaSalaAtual()
    {
        var id = _groups.Add("Amigos");
        var vm = await LoadedAsync();
        await vm.JoinCommand.ExecuteAsync(vm.Groups[0]);

        _realtime.RaiseGroupRemoved(id);

        Assert.Empty(vm.Groups);
        Assert.Null(vm.CurrentGroup);
    }

    [Fact]
    public async Task Convidar_FechaOMenuEPedeAJanelaDeAmigos()
    {
        _groups.Add("Amigos");
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        GroupItemViewModel? requested = null;
        vm.InviteRequested += g => requested = g;
        group.IsMembersOpen = true;

        group.InviteCommand.Execute(null);

        Assert.Same(group, requested);
        Assert.False(group.IsMembersOpen);
    }

    [Fact]
    public async Task JanelaDeAmigosNoConvite_ConvidaEMarcaComoConvidado()
    {
        _groups.Add("Amigos");
        _friends.Friends.Add(_carla);
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        var friends = new FriendsViewModel(_friends);
        vm.InviteRequested += friends.OpenInvite;
        group.IsMembersOpen = true;

        group.InviteCommand.Execute(null);
        Assert.True(friends.IsOpen);
        Assert.Same(group, friends.InviteGroup);

        await friends.InviteCommand.ExecuteAsync(_carla);
        Assert.Contains($"add:{_carla.UserId}", _groups.Calls);
        Assert.Contains(_carla.UserId, group.MemberIds);

        friends.CloseCommand.Execute(null);
        Assert.Null(friends.InviteGroup);
        Assert.False(friends.IsInviting);
    }

    [Fact]
    public async Task CancelarConfirmacao_FechaAJanela()
    {
        _groups.Add("Amigos");
        var vm = await LoadedAsync();
        var group = vm.Groups[0];

        group.ConfirmLeaveCommand.Execute(null);
        Assert.True(vm.IsConfirming);

        group.CancelConfirmationCommand.Execute(null);
        Assert.False(group.IsConfirmingLeave);
        Assert.False(vm.IsConfirming);
    }

    [Fact]
    public async Task SairDoGrupo_PeloPopup_RemoveDaLista()
    {
        _groups.Add("Amigos");
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        group.IsMembersOpen = true;
        group.ConfirmLeaveCommand.Execute(null);
        Assert.True(group.IsConfirmingLeave);
        Assert.False(group.IsMembersOpen);
        Assert.Same(group, vm.ConfirmingGroup);

        await group.LeaveGroupCommand.ExecuteAsync();

        Assert.Contains("leave:Amigos", _groups.Calls);
        Assert.Empty(vm.Groups);
        Assert.False(group.IsMembersOpen);
        Assert.Null(vm.ConfirmingGroup);
    }

    [Fact]
    public async Task ExcluirGrupo_SoAparecePraQuemCriou_ERemoveDaLista()
    {
        _groups.Add("Amigos");
        var vm = await LoadedAsync();
        var group = vm.Groups[0];

        group.IsMembersOpen = true;
        Assert.False(group.CanDelete);
        group.IsMembersOpen = false;

        _groups.CanDelete = true;
        group.IsMembersOpen = true;
        Assert.True(group.CanDelete);

        group.ConfirmDeleteCommand.Execute(null);
        Assert.True(group.IsConfirmingDelete);
        Assert.Same(group, vm.ConfirmingGroup);

        // Abrir uma confirmação fecha a outra.
        group.ConfirmLeaveCommand.Execute(null);
        Assert.False(group.IsConfirmingDelete);
        group.ConfirmDeleteCommand.Execute(null);
        Assert.False(group.IsConfirmingLeave);

        await group.DeleteGroupCommand.ExecuteAsync();

        Assert.Contains("delete:Amigos", _groups.Calls);
        Assert.Empty(vm.Groups);
        Assert.False(group.IsMembersOpen);
        Assert.Null(vm.ConfirmingGroup);
    }

    [Fact]
    public async Task ExcluirGrupo_Falha_MostraOErroEMantemOGrupo()
    {
        _groups.Add("Amigos");
        _groups.CanDelete = true;
        var vm = await LoadedAsync();
        var group = vm.Groups[0];
        group.IsMembersOpen = true;

        _groups.Failure = new InvalidOperationException("Só quem criou o grupo pode excluí-lo.");
        await group.DeleteGroupCommand.ExecuteAsync();

        Assert.Single(vm.Groups);
        Assert.True(group.HasMembersError);
    }

    [Fact]
    public async Task Reconectado_RecarregaGruposEVoltaParaASala()
    {
        var id = _groups.Add("Amigos");
        var vm = await LoadedAsync();
        await vm.JoinCommand.ExecuteAsync(vm.Groups[0]);

        _realtime.SetState(RealtimeState.Reconnecting);
        Assert.Equal("Conexão perdida. Reconectando…", vm.RealtimeStatusText);
        _realtime.Calls.Clear();
        _realtime.SetState(RealtimeState.Connected);

        Assert.Equal([$"join:{id}"], _realtime.Calls);
        Assert.Same(vm.Groups[0], vm.CurrentGroup);
        Assert.False(vm.HasRealtimeStatus);
    }

    [Fact]
    public async Task CriarGrupo_AdicionaNaListaEAbreOsIntegrantes()
    {
        var vm = await LoadedAsync();
        vm.StartCreateCommand.Execute(null);
        vm.NewGroupName = "  Jogos   de sexta ";

        await vm.CreateCommand.ExecuteAsync();

        Assert.Contains("create:Jogos de sexta", _groups.Calls);
        var group = Assert.Single(vm.Groups);
        Assert.True(group.IsMembersOpen);
        Assert.False(vm.IsCreating);
        Assert.Equal("", vm.NewGroupName);
    }

    [Fact]
    public async Task CriarGrupo_SemNome_MostraErroSemChamarAApi()
    {
        var vm = await LoadedAsync();
        vm.StartCreateCommand.Execute(null);
        vm.NewGroupName = "   ";

        await vm.CreateCommand.ExecuteAsync();

        Assert.DoesNotContain(_groups.Calls, c => c.StartsWith("create"));
        Assert.True(vm.HasError);
        Assert.True(vm.IsCreating);
    }

    [Fact]
    public async Task Desconectar_FechaATempoRealELimpaALista()
    {
        _groups.Add("Amigos");
        var vm = await LoadedAsync();
        await vm.JoinCommand.ExecuteAsync(vm.Groups[0]);

        await vm.SignOutAsync();

        Assert.Equal(1, _realtime.StopCount);
        Assert.Empty(vm.Groups);
        Assert.Null(vm.CurrentGroup);
        Assert.False(vm.HasRealtimeStatus);
    }
}
