using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScreenCord.Tests;

public class FriendsViewModelTests
{
    private readonly FakeFriendsService _service = new();
    private readonly Person _ana = FakeFriendsService.Create("ana.silva", "Ana");
    private readonly Person _bruno = FakeFriendsService.Create("bruno");
    private readonly Person _carla = FakeFriendsService.Create("carla_dev", "Carla");

    public FriendsViewModelTests()
    {
        _service.Directory.AddRange([_ana, _bruno, _carla]);
    }

    private static DiscordException CentralError(DiscordErrorKind kind, string message = "x") =>
        new(kind, message) { Source = ErrorSource.CentralApi };

    [Fact]
    public async Task Load_SemAmigos_MostraEstadoVazio()
    {
        var vm = new FriendsViewModel(_service);

        await vm.LoadAsync();

        Assert.True(vm.ShowNoFriends);
        Assert.False(vm.HasFriends);
        Assert.False(vm.HasError);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Load_PreencheAmigosESolicitacoes()
    {
        _service.Friends.Add(_ana with { Relationship = RelationshipStatus.Friend });
        _service.Incoming.Add(_bruno with { Relationship = RelationshipStatus.PendingIncoming });
        _service.Outgoing.Add(_carla with { Relationship = RelationshipStatus.PendingOutgoing });
        var vm = new FriendsViewModel(_service);

        await vm.LoadAsync();

        Assert.Equal("Ana", Assert.Single(vm.Friends).DisplayName);
        Assert.True(Assert.Single(vm.IncomingRequests).CanAccept);
        Assert.True(Assert.Single(vm.OutgoingRequests).IsPendingOutgoing);
        Assert.True(vm.HasFriends);
        Assert.True(vm.HasIncomingRequests);
        Assert.True(vm.HasOutgoingRequests);
        Assert.False(vm.ShowNoFriends);
    }

    [Fact]
    public async Task Load_ApiForaDoAr_MostraErroSemAfirmarQueNaoHaAmigos()
    {
        _service.Failure = CentralError(DiscordErrorKind.Network);
        var vm = new FriendsViewModel(_service);

        await vm.LoadAsync();

        Assert.Contains("servidor do ScreenCord", vm.ErrorMessage);
        Assert.False(vm.ShowNoFriends);
    }

    [Theory]
    [InlineData("bruno")]
    [InlineData("@bruno")]
    [InlineData("  @bruno  ")]
    public async Task Buscar_IgnoraArrobaEEspacos(string text)
    {
        var vm = new FriendsViewModel(_service);
        vm.SearchText = text;

        await vm.SearchCommand.ExecuteAsync();

        Assert.Contains("search:bruno", _service.Calls);
        Assert.True(vm.ShowSearchResults);
        Assert.False(vm.ShowFriends);
        var result = Assert.Single(vm.SearchResults);
        Assert.Equal("@bruno", result.Handle);
        Assert.True(result.CanAdd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("@")]
    public async Task Buscar_TermoCurto_AvisaSemChamarAApi(string text)
    {
        var vm = new FriendsViewModel(_service);
        vm.SearchText = text;

        await vm.SearchCommand.ExecuteAsync();

        Assert.Empty(_service.Calls);
        Assert.Contains("pelo menos 2", vm.SearchMessage);
    }

    [Fact]
    public async Task Buscar_SemResultados_ExplicaQueAPessoaPrecisaTerEntrado()
    {
        var vm = new FriendsViewModel(_service);
        vm.SearchText = "fulano";

        await vm.SearchCommand.ExecuteAsync();

        Assert.Empty(vm.SearchResults);
        Assert.Contains("fulano", vm.SearchMessage);
    }

    [Fact]
    public async Task Adicionar_EnviaSolicitacaoEAtualizaOResultadoDaBusca()
    {
        var vm = new FriendsViewModel(_service);
        await vm.LoadAsync();
        vm.SearchText = "bruno";
        await vm.SearchCommand.ExecuteAsync();

        await vm.AddCommand.ExecuteAsync(vm.SearchResults[0]);

        Assert.Contains($"add:{_bruno.UserId}", _service.Calls);
        Assert.True(Assert.Single(vm.SearchResults).IsPendingOutgoing);
        Assert.Equal(_bruno.UserId, Assert.Single(vm.OutgoingRequests).UserId);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Aceitar_MoveASolicitacaoParaAmigos()
    {
        _service.Incoming.Add(_bruno with { Relationship = RelationshipStatus.PendingIncoming });
        var vm = new FriendsViewModel(_service);
        await vm.LoadAsync();

        await vm.AcceptCommand.ExecuteAsync(vm.IncomingRequests[0]);

        Assert.Empty(vm.IncomingRequests);
        Assert.True(Assert.Single(vm.Friends).IsFriend);
    }

    [Fact]
    public async Task Recusar_RemoveASolicitacao()
    {
        _service.Incoming.Add(_bruno with { Relationship = RelationshipStatus.PendingIncoming });
        var vm = new FriendsViewModel(_service);
        await vm.LoadAsync();

        await vm.DeclineCommand.ExecuteAsync(vm.IncomingRequests[0]);

        Assert.Empty(vm.IncomingRequests);
        Assert.Empty(vm.Friends);
    }

    [Fact]
    public async Task Remover_TiraDaListaDeAmigos()
    {
        _service.Friends.Add(_ana with { Relationship = RelationshipStatus.Friend });
        var vm = new FriendsViewModel(_service);
        await vm.LoadAsync();

        await vm.RemoveCommand.ExecuteAsync(vm.Friends[0]);

        Assert.Empty(vm.Friends);
        Assert.True(vm.ShowNoFriends);
    }

    [Fact]
    public async Task AcaoRecusadaPelaApi_MostraAMensagemDoServidorEMantemAsListas()
    {
        _service.Friends.Add(_ana with { Relationship = RelationshipStatus.Friend });
        var vm = new FriendsViewModel(_service);
        await vm.LoadAsync();
        _service.NextActionFailure = CentralError(DiscordErrorKind.Rejected, "Vocês já são amigos.");

        await vm.AddCommand.ExecuteAsync(_ana);

        Assert.Equal("Vocês já são amigos.", vm.ErrorMessage);
        Assert.Single(vm.Friends);
    }

    [Fact]
    public async Task LimparBusca_VoltaParaAListaDeAmigos()
    {
        var vm = new FriendsViewModel(_service);
        vm.SearchText = "bruno";
        await vm.SearchCommand.ExecuteAsync();

        vm.ClearSearchCommand.Execute(null);

        Assert.True(vm.ShowFriends);
        Assert.Empty(vm.SearchResults);
        Assert.Equal("", vm.SearchText);
    }

    [Fact]
    public async Task SignOut_EncerraASessaoDaApiELimpaATela()
    {
        _service.Friends.Add(_ana with { Relationship = RelationshipStatus.Friend });
        var vm = new FriendsViewModel(_service);
        await vm.LoadAsync();

        await vm.SignOutAsync();

        Assert.Equal(1, _service.SignOutCount);
        Assert.Empty(vm.Friends);
        Assert.False(vm.ShowNoFriends);
    }
}

public class MainViewModelTests
{
    private readonly FakeSessionManager _session = new();
    private readonly FakeProfileService _profile = new();
    private readonly FakeFriendsService _friends = new();
    private readonly FakeGroupsService _groups = new();
    private readonly FakeGroupsRealtime _realtime = new();

    private MainViewModel Create() =>
        new(
            _session,
            _profile,
            new FriendsViewModel(_friends),
            new GroupsViewModel(_groups, _realtime, _friends, new FakeScreenShareService(), new FakeScreenShareUi()),
            NullLogger<MainViewModel>.Instance);

    [Fact]
    public async Task SemSessao_MostraTelaDeLogin()
    {
        var vm = Create();

        await vm.InitializeAsync();

        Assert.True(vm.ShowLogin);
        Assert.True(vm.ShowConnectButton);
        Assert.False(vm.IsConnected);
        Assert.Equal(0, _profile.Calls);
    }

    [Fact]
    public async Task ComSessaoSalva_CarregaPerfilEAmigosSemPedirLogin()
    {
        _session.HasSession = true;
        _friends.Friends.Add(FakeFriendsService.Create("bruno", relationship: RelationshipStatus.Friend));
        var vm = Create();

        await vm.InitializeAsync();

        Assert.True(vm.IsConnected);
        Assert.False(vm.ShowLogin);
        Assert.Equal("Nelly", vm.Profile?.DisplayName);
        Assert.Single(vm.Friends.Friends);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task ApiCentralForaDoAr_NaoDerrubaOPerfil()
    {
        _session.HasSession = true;
        _friends.Failure = new DiscordException(DiscordErrorKind.Network, "x") { Source = ErrorSource.CentralApi };
        var vm = Create();

        await vm.InitializeAsync();

        Assert.True(vm.IsConnected);
        Assert.NotNull(vm.Profile);
        Assert.False(vm.HasError);
        Assert.True(vm.Friends.HasError);
    }

    [Fact]
    public async Task Conectar_ComSucesso_CarregaPerfil()
    {
        var vm = Create();
        await vm.InitializeAsync();

        await vm.ConnectCommand.ExecuteAsync();

        Assert.True(vm.IsConnected);
        Assert.False(vm.IsSigningIn);
        Assert.NotNull(vm.Profile);
    }

    [Fact]
    public async Task Conectar_AutorizacaoNegada_MostraErroEPermaneceNoLogin()
    {
        _session.OnSignIn = _ => Task.FromException(new DiscordException(DiscordErrorKind.AuthorizationDenied, "negado"));
        var vm = Create();
        await vm.InitializeAsync();

        await vm.ConnectCommand.ExecuteAsync();

        Assert.False(vm.IsConnected);
        Assert.True(vm.ShowConnectButton);
        Assert.Contains("negada", vm.ErrorMessage);
    }

    [Fact]
    public async Task Conectar_Cancelado_VoltaAoLoginSemErro()
    {
        _session.OnSignIn = ct => Task.Delay(Timeout.Infinite, ct);
        var vm = Create();
        await vm.InitializeAsync();

        var connecting = vm.ConnectCommand.ExecuteAsync();
        Assert.True(vm.IsSigningIn);
        Assert.True(vm.CancelConnectCommand.CanExecute(null));
        vm.CancelConnectCommand.Execute(null);
        await connecting;

        Assert.False(vm.IsSigningIn);
        Assert.False(vm.IsConnected);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Atualizar_SemConexao_MantemSessaoEMostraErro()
    {
        _session.HasSession = true;
        var vm = Create();
        await vm.InitializeAsync();
        _profile.OnGet = () => Task.FromException<UserProfile>(new DiscordException(DiscordErrorKind.Network, "offline"));

        await vm.RefreshProfileCommand.ExecuteAsync();

        Assert.True(vm.IsConnected);
        Assert.NotNull(vm.Profile);
        Assert.Contains("conexão", vm.ErrorMessage);
    }

    [Fact]
    public async Task Atualizar_TokenInvalido_VoltaParaOLoginEEsqueceASessaoDaApi()
    {
        _session.HasSession = true;
        var vm = Create();
        await vm.InitializeAsync();
        _profile.OnGet = () => Task.FromException<UserProfile>(new DiscordException(DiscordErrorKind.Unauthorized, "401"));

        await vm.RefreshProfileCommand.ExecuteAsync();

        Assert.False(vm.IsConnected);
        Assert.True(vm.ShowLogin);
        Assert.Null(vm.Profile);
        Assert.Contains("sessão expirou", vm.ErrorMessage);
        Assert.True(_friends.ForgetCount > 0);
    }

    [Fact]
    public async Task Desconectar_EncerraAsDuasSessoesELimpaOsDados()
    {
        _session.HasSession = true;
        _friends.Friends.Add(FakeFriendsService.Create("bruno", relationship: RelationshipStatus.Friend));
        var vm = Create();
        await vm.InitializeAsync();

        vm.DisconnectCommand.Execute(null);
        await vm.ConfirmDisconnectCommand.ExecuteAsync();

        Assert.Equal(1, _session.SignOutCount);
        Assert.Equal(1, _friends.SignOutCount);
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsConfirmingDisconnect);
        Assert.Null(vm.Profile);
        Assert.True(vm.ShowLogin);
        Assert.Empty(vm.Friends.Friends);
        Assert.True(_realtime.StopCount >= 1);
        Assert.Empty(vm.Groups.Groups);
    }

    [Fact]
    public async Task ComSessaoSalva_CarregaGruposEAbreATempoReal()
    {
        _session.HasSession = true;
        _groups.Add("Amigos");
        var vm = Create();

        await vm.InitializeAsync();

        Assert.Equal(1, _realtime.StartCount);
        Assert.Equal("Amigos", Assert.Single(vm.Groups.Groups).Name);
    }

    [Fact]
    public async Task Desconectar_SoAbreAConfirmacao()
    {
        _session.HasSession = true;
        var vm = Create();
        await vm.InitializeAsync();

        vm.DisconnectCommand.Execute(null);

        Assert.True(vm.IsConfirmingDisconnect);
        Assert.False(vm.IsMainInteractive);
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.IsConnected);
        Assert.Equal(0, _session.SignOutCount);
        Assert.Equal(0, _friends.SignOutCount);
    }

    [Fact]
    public async Task CancelarDesconexao_MantemAContaConectada()
    {
        _session.HasSession = true;
        var vm = Create();
        await vm.InitializeAsync();
        vm.DisconnectCommand.Execute(null);

        vm.CancelDisconnectCommand.Execute(null);

        Assert.False(vm.IsConfirmingDisconnect);
        Assert.True(vm.IsMainInteractive);
        Assert.False(vm.CancelDisconnectCommand.CanExecute(null));
        Assert.True(vm.IsConnected);
        Assert.NotNull(vm.Profile);
        Assert.Equal(0, _session.SignOutCount);
    }

    [Fact]
    public async Task SessaoExpiradaComConfirmacaoAberta_FechaAConfirmacao()
    {
        _session.HasSession = true;
        var vm = Create();
        await vm.InitializeAsync();
        vm.DisconnectCommand.Execute(null);
        _profile.OnGet = () => Task.FromException<UserProfile>(new DiscordException(DiscordErrorKind.Unauthorized, "401"));

        await vm.RefreshProfileCommand.ExecuteAsync();

        Assert.False(vm.IsConfirmingDisconnect);
        Assert.True(vm.ShowLogin);
    }
}
