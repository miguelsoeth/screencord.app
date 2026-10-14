using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Security;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScreenCord.Tests;

public class SessionManagerTests
{
    private readonly FakeOAuthService _oauth = new();
    private readonly InMemoryTokenStore _store = new();
    private readonly FakeTimeProvider _time = new();

    private SessionManager Create() => new(_oauth, _store, _time, NullLogger<SessionManager>.Instance);

    private TokenSession Valid(string access = "access-1", string? refresh = "refresh-1") =>
        new(access, refresh, _time.Now.AddDays(7), "identify");

    private TokenSession Expired() => new("access-velho", "refresh-1", _time.Now.AddMinutes(-1), "identify");

    [Fact]
    public async Task SemSessaoSalva_HasSessionFalso_EGetAccessTokenLancaUnauthorized()
    {
        var manager = Create();

        Assert.False(await manager.HasSessionAsync());
        var ex = await Assert.ThrowsAsync<DiscordException>(() => manager.GetAccessTokenAsync());
        Assert.Equal(DiscordErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task SessaoSalvaValida_EhReutilizadaSemLoginNemRefresh()
    {
        _store.Session = Valid();
        var manager = Create();

        Assert.True(await manager.HasSessionAsync());
        Assert.Equal("access-1", await manager.GetAccessTokenAsync());
        Assert.Empty(_oauth.RefreshCalls);
    }

    [Fact]
    public async Task SignIn_PersisteASessao()
    {
        _oauth.OnAuthorize = _ => Task.FromResult(Valid("access-novo"));
        var manager = Create();

        await manager.SignInAsync();

        Assert.Equal("access-novo", _store.Session?.AccessToken);
        Assert.Equal("access-novo", await manager.GetAccessTokenAsync());
    }

    [Fact]
    public async Task SignIn_Falhando_NaoCriaSessao()
    {
        _oauth.OnAuthorize = _ => Task.FromException<TokenSession>(new DiscordException(DiscordErrorKind.AuthorizationDenied, "negado"));
        var manager = Create();

        await Assert.ThrowsAsync<DiscordException>(() => manager.SignInAsync());

        Assert.Null(_store.Session);
        Assert.False(await manager.HasSessionAsync());
    }

    [Fact]
    public async Task TokenExpirado_EhRenovadoEPersistido()
    {
        _store.Session = Expired();
        _oauth.OnRefresh = _ => Task.FromResult(Valid("access-renovado", "refresh-2"));
        var manager = Create();

        var token = await manager.GetAccessTokenAsync();

        Assert.Equal("access-renovado", token);
        Assert.Equal(["refresh-1"], _oauth.RefreshCalls);
        Assert.Equal("refresh-2", _store.Session?.RefreshToken);
    }

    [Fact]
    public async Task RefreshRejeitado_LimpaASessaoELancaUnauthorized()
    {
        _store.Session = Expired();
        _oauth.OnRefresh = _ => Task.FromException<TokenSession>(
            new DiscordException(DiscordErrorKind.OAuthRejected, "rejeitado") { ErrorCode = "invalid_grant" });
        var manager = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => manager.GetAccessTokenAsync());

        Assert.Equal(DiscordErrorKind.Unauthorized, ex.Kind);
        Assert.Null(_store.Session);
        Assert.False(await manager.HasSessionAsync());
    }

    [Fact]
    public async Task RefreshSemRede_MantemASessaoParaNovaTentativa()
    {
        _store.Session = Expired();
        _oauth.OnRefresh = _ => Task.FromException<TokenSession>(new DiscordException(DiscordErrorKind.Network, "offline"));
        var manager = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => manager.GetAccessTokenAsync());

        Assert.Equal(DiscordErrorKind.Network, ex.Kind);
        Assert.NotNull(_store.Session);
    }

    [Fact]
    public async Task TokenExpiradoSemRefreshToken_EncerraASessao()
    {
        _store.Session = Expired() with { RefreshToken = null };
        var manager = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => manager.GetAccessTokenAsync());

        Assert.Equal(DiscordErrorKind.Unauthorized, ex.Kind);
        Assert.Null(_store.Session);
    }

    [Fact]
    public async Task SignOut_RevogaELimpaOsDados()
    {
        _store.Session = Valid();
        var manager = Create();

        await manager.SignOutAsync();

        Assert.Equal(["refresh-1"], _oauth.RevokeCalls);
        Assert.Null(_store.Session);
        Assert.False(await manager.HasSessionAsync());
    }

    [Fact]
    public async Task SignOut_ComFalhaNaRevogacao_AindaLimpaOsDados()
    {
        _store.Session = Valid();
        _oauth.OnRevoke = _ => Task.FromException(new DiscordException(DiscordErrorKind.Network, "offline"));
        var manager = Create();

        await manager.SignOutAsync();

        Assert.Null(_store.Session);
    }
}

public class ProfileServiceTests
{
    private static readonly DiscordUser User = new()
    {
        Id = "80351110224678912",
        Username = "nelly",
        Discriminator = "0",
        GlobalName = "Nelly",
        Avatar = "8342729096ea3675442027381ff50dfe",
    };

    private static DiscordException Unauthorized() => new(DiscordErrorKind.Unauthorized, "401");

    [Fact]
    public async Task GetProfile_MapeiaOsCamposDaApi()
    {
        var api = new FakeApiClient().Returns(User);
        var service = new ProfileService(api, new FakeSessionManager { HasSession = true });

        var profile = await service.GetProfileAsync();

        Assert.Equal("80351110224678912", profile.Id);
        Assert.Equal("nelly", profile.Username);
        Assert.Equal("Nelly", profile.DisplayName);
        Assert.Equal("@nelly", profile.Handle);
        Assert.True(profile.HasCustomAvatar);
        Assert.Equal("https://cdn.discordapp.com/avatars/80351110224678912/8342729096ea3675442027381ff50dfe.png?size=128", profile.AvatarUrl);
        Assert.Equal(["access-1"], api.Tokens);
    }

    [Fact]
    public async Task GetProfile_SemNomeGlobal_UsaNomeDeUsuario()
    {
        var api = new FakeApiClient().Returns(User with { GlobalName = null, Avatar = null, Discriminator = "1337" });
        var service = new ProfileService(api, new FakeSessionManager { HasSession = true });

        var profile = await service.GetProfileAsync();

        Assert.Equal("nelly", profile.DisplayName);
        Assert.Equal("nelly#1337", profile.Handle);
        Assert.False(profile.HasCustomAvatar);
    }

    [Fact]
    public async Task Api401_RenovaOTokenETentaDeNovo()
    {
        var api = new FakeApiClient().Throws(Unauthorized()).Returns(User);
        var session = new FakeSessionManager { HasSession = true };
        var service = new ProfileService(api, session);

        var profile = await service.GetProfileAsync();

        Assert.Equal("nelly", profile.Username);
        Assert.Equal(1, session.RefreshCount);
        Assert.Equal(["access-1", "access-2"], api.Tokens);
    }

    [Fact]
    public async Task Api401Persistente_EncerraASessao()
    {
        var api = new FakeApiClient().Throws(Unauthorized()).Throws(Unauthorized());
        var session = new FakeSessionManager { HasSession = true };
        var service = new ProfileService(api, session);

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.GetProfileAsync());

        Assert.Equal(DiscordErrorKind.Unauthorized, ex.Kind);
        Assert.Equal(1, session.SignOutCount);
    }

    [Fact]
    public async Task OutrosErros_NaoDisparamRefresh()
    {
        var api = new FakeApiClient().Throws(new DiscordException(DiscordErrorKind.ServiceUnavailable, "503"));
        var session = new FakeSessionManager { HasSession = true };
        var service = new ProfileService(api, session);

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.GetProfileAsync());

        Assert.Equal(DiscordErrorKind.ServiceUnavailable, ex.Kind);
        Assert.Equal(0, session.RefreshCount);
        Assert.Equal(0, session.SignOutCount);
    }
}
