using System.Net;
using System.Text.Json;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ScreenCord.Tests;

public class CentralFriendsServiceTests
{
    private const string BaseUrl = "http://localhost:5080";
    private static readonly Guid BrunoId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly StubHttpHandler _handler = new();
    private readonly FakeSessionManager _session = new() { HasSession = true, AccessToken = "discord-access" };

    private CentralFriendsService Create(string baseUrl = BaseUrl) =>
        new(
            new StubHttpClientFactory(_handler),
            _session,
            Options.Create(new ApiOptions { BaseUrl = baseUrl }),
            NullLogger<CentralFriendsService>.Instance);

    private static string LoginJson(string token = "api-token-1") => $$$"""
        {"token":"{{{token}}}","expiresAtUtc":"2026-11-12T12:00:00+00:00","user":{"id":"22222222-2222-2222-2222-222222222222","discordId":"1001","username":"ana.silva","globalName":"Ana","discriminator":"0","avatarHash":null,"relationship":"None"}}
        """;

    private static string UsersJson(string relationship = "Friend") => $$"""
        [{"id":"{{BrunoId}}","discordId":"80351110224678912","username":"bruno","globalName":"Bruno","discriminator":"0","avatarHash":"8342729096ea3675442027381ff50dfe","relationship":"{{relationship}}"}]
        """;

    [Fact]
    public async Task PrimeiraChamada_FazLoginNaApiComOTokenDoDiscord_EUsaOTokenDaApiDepois()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson()).Enqueue(HttpStatusCode.OK, UsersJson());
        var service = Create();

        var friends = await service.GetFriendsAsync();

        Assert.Equal(2, _handler.Requests.Count);
        var login = _handler.Requests[0];
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.Equal("http://localhost:5080/auth/discord", login.Uri.AbsoluteUri);
        Assert.Null(login.Authorization);
        Assert.Equal("discord-access", JsonDocument.Parse(login.Body).RootElement.GetProperty("accessToken").GetString());

        var list = _handler.Requests[1];
        Assert.Equal("http://localhost:5080/friends", list.Uri.AbsoluteUri);
        Assert.Equal("Bearer api-token-1", list.Authorization);
        Assert.DoesNotContain("discord-access", list.Authorization);

        var bruno = Assert.Single(friends);
        Assert.Equal(BrunoId, bruno.UserId);
        Assert.Equal("Bruno", bruno.DisplayName);
        Assert.Equal("@bruno", bruno.Handle);
        Assert.True(bruno.IsFriend);
        Assert.Equal("https://cdn.discordapp.com/avatars/80351110224678912/8342729096ea3675442027381ff50dfe.png?size=64", bruno.AvatarUrl);
    }

    [Fact]
    public async Task ChamadasSeguintes_ReutilizamASessaoDaApi()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson()).Enqueue(HttpStatusCode.OK, "[]").Enqueue(HttpStatusCode.OK, "[]");
        var service = Create();

        await service.GetFriendsAsync();
        await service.GetFriendsAsync();

        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task SessaoDaApiVencida_RefazOLoginERepeteAChamada()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson("api-token-1"))
                .Enqueue(HttpStatusCode.Unauthorized)
                .Enqueue(HttpStatusCode.OK, LoginJson("api-token-2"))
                .Enqueue(HttpStatusCode.OK, UsersJson());
        var service = Create();

        var friends = await service.GetFriendsAsync();

        Assert.Single(friends);
        Assert.Equal("Bearer api-token-2", _handler.Requests[3].Authorization);
    }

    [Fact]
    public async Task Api401Persistente_GeraUnauthorizedDaApiCentral()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson()).Enqueue(HttpStatusCode.Unauthorized)
                .Enqueue(HttpStatusCode.OK, LoginJson()).Enqueue(HttpStatusCode.Unauthorized);
        var service = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.GetFriendsAsync());

        Assert.Equal(DiscordErrorKind.Unauthorized, ex.Kind);
        Assert.Equal(ErrorSource.CentralApi, ex.Source);
    }

    [Fact]
    public async Task LoginRecusadoPelaApi_GeraUnauthorized()
    {
        _handler.Enqueue(HttpStatusCode.Unauthorized, """{"code":"invalid_discord_token","message":"O Discord não confirmou este login."}""");
        var service = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.GetFriendsAsync());

        Assert.Equal(DiscordErrorKind.Unauthorized, ex.Kind);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Buscar_CodificaOTermoNaUrl()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson()).Enqueue(HttpStatusCode.OK, UsersJson("None"));
        var service = Create();

        var results = await service.SearchUsersAsync("a&b c");

        Assert.Equal("http://localhost:5080/users/search?q=a%26b%20c", _handler.Requests[1].Uri.AbsoluteUri);
        Assert.True(Assert.Single(results).CanAdd);
    }

    [Fact]
    public async Task AcoesUsamOsEndpointsCorretos()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson())
                .Enqueue(HttpStatusCode.Created, UsersJson("PendingOutgoing").Trim('[', ']', '\r', '\n', ' '))
                .Enqueue(HttpStatusCode.NoContent)
                .Enqueue(HttpStatusCode.NoContent)
                .Enqueue(HttpStatusCode.NoContent);
        var service = Create();

        await service.SendRequestAsync(BrunoId);
        await service.AcceptRequestAsync(BrunoId);
        await service.DeleteRequestAsync(BrunoId);
        await service.RemoveFriendAsync(BrunoId);

        var requests = _handler.Requests.Skip(1).Select(r => $"{r.Method} {r.Uri.AbsolutePath}").ToList();
        Assert.Equal(
        [
            "POST /friends/requests",
            $"POST /friends/requests/{BrunoId}/accept",
            $"DELETE /friends/requests/{BrunoId}",
            $"DELETE /friends/{BrunoId}",
        ], requests);
        Assert.Equal(BrunoId, JsonDocument.Parse(_handler.Requests[1].Body).RootElement.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task ErroDeNegocio_RepassaAMensagemDaApi()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson())
                .Enqueue(HttpStatusCode.Conflict, """{"code":"already_friends","message":"Vocês já são amigos."}""");
        var service = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.SendRequestAsync(BrunoId));

        Assert.Equal(DiscordErrorKind.Rejected, ex.Kind);
        Assert.Equal("already_friends", ex.ErrorCode);
        Assert.Equal("Vocês já são amigos.", ErrorMessages.For(ex));
    }

    [Fact]
    public async Task ApiForaDoAr_GeraErroDeRedeDaApiCentral()
    {
        _handler.EnqueueException(new HttpRequestException("connection refused"));
        var service = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.GetFriendsAsync());

        Assert.Equal(DiscordErrorKind.Network, ex.Kind);
        Assert.Contains("servidor do ScreenCord", ErrorMessages.For(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, DiscordErrorKind.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, DiscordErrorKind.RateLimited)]
    public async Task ErrosHttp_SaoClassificados(HttpStatusCode status, DiscordErrorKind expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson()).Enqueue(status);
        var service = Create();

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.GetFriendsAsync());

        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task ItensInvalidosDaApi_SaoDescartados()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson()).Enqueue(HttpStatusCode.OK, """
            [{"id":"11111111-1111-1111-1111-111111111111","discordId":"nao-numerico","username":"x","relationship":"Friend"},
             {"id":"33333333-3333-3333-3333-333333333333","discordId":"5","username":"","relationship":"Friend"},
             {"id":"44444444-4444-4444-4444-444444444444","discordId":"80351110224678912","username":"ok","avatarHash":"../../x","relationship":"Friend"}]
            """);
        var service = Create();

        var friends = await service.GetFriendsAsync();

        var ok = Assert.Single(friends);
        Assert.Equal("ok", ok.Username);
        Assert.StartsWith("https://cdn.discordapp.com/embed/avatars/", ok.AvatarUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-url")]
    [InlineData("http://api.exemplo.com")]
    [InlineData("ftp://localhost")]
    public async Task EnderecoInseguroOuAusente_NaoEnviaNada(string baseUrl)
    {
        var service = Create(baseUrl);

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.GetFriendsAsync());

        Assert.Equal(DiscordErrorKind.Configuration, ex.Kind);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task SignOut_EncerraASessaoNaApiEExigeNovoLoginDepois()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson("api-token-1")).Enqueue(HttpStatusCode.OK, "[]")
                .Enqueue(HttpStatusCode.NoContent)
                .Enqueue(HttpStatusCode.OK, LoginJson("api-token-2")).Enqueue(HttpStatusCode.OK, "[]");
        var service = Create();
        await service.GetFriendsAsync();

        await service.SignOutAsync();
        await service.GetFriendsAsync();

        Assert.Equal("POST /auth/logout", $"{_handler.Requests[2].Method} {_handler.Requests[2].Uri.AbsolutePath}");
        Assert.Equal("Bearer api-token-1", _handler.Requests[2].Authorization);
        Assert.Equal("/auth/discord", _handler.Requests[3].Uri.AbsolutePath);
        Assert.Equal("Bearer api-token-2", _handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task SignOut_SemSessao_NaoFazChamada()
    {
        await Create().SignOutAsync();

        Assert.Empty(_handler.Requests);
    }
}
