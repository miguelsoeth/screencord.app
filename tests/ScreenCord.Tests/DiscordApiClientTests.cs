using System.Net;
using System.Net.Http.Headers;
using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;

namespace ScreenCord.Tests;

public class DiscordApiClientTests
{
    private const string UserJson = """
        {"id":"80351110224678912","username":"nelly","discriminator":"0","global_name":"Nelly","avatar":"8342729096ea3675442027381ff50dfe","verified":true}
        """;

    private static (DiscordApiClient Client, StubHttpHandler Handler) Create()
    {
        var handler = new StubHttpHandler();
        return (new DiscordApiClient(new StubHttpClientFactory(handler)), handler);
    }

    [Fact]
    public async Task GetCurrentUser_EnviaBearerParaUsersMe_EDesserializa()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, UserJson);

        var user = await client.GetCurrentUserAsync("token-abc");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://discord.com/api/v10/users/@me", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer token-abc", request.Authorization);
        Assert.Equal("80351110224678912", user.Id);
        Assert.Equal("nelly", user.Username);
        Assert.Equal("Nelly", user.GlobalName);
        Assert.Equal("8342729096ea3675442027381ff50dfe", user.Avatar);
    }

    [Fact]
    public async Task GetCurrentUser_AceitaCamposOpcionaisNulos()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"1","username":"x","discriminator":"0","global_name":null,"avatar":null}""");

        var user = await client.GetCurrentUserAsync("t");

        Assert.Null(user.GlobalName);
        Assert.Null(user.Avatar);
    }

    [Fact]
    public async Task TokenInvalido_GeraUnauthorized()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized","code":0}""");

        var ex = await Assert.ThrowsAsync<DiscordException>(() => client.GetCurrentUserAsync("t"));

        Assert.Equal(DiscordErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task RateLimit_UsaCabecalhoRetryAfter()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests, """{"message":"You are being rate limited.","retry_after":64.57,"global":false}""",
            r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(65)));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => client.GetCurrentUserAsync("t"));

        Assert.Equal(DiscordErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(65), ex.RetryAfter);
    }

    [Fact]
    public async Task RateLimit_SemCabecalho_UsaCorpo()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests, """{"message":"You are being rate limited.","retry_after":1.5,"global":true}""");

        var ex = await Assert.ThrowsAsync<DiscordException>(() => client.GetCurrentUserAsync("t"));

        Assert.Equal(TimeSpan.FromSeconds(1.5), ex.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ApiIndisponivel_GeraServiceUnavailable(HttpStatusCode status)
    {
        var (client, handler) = Create();
        handler.Enqueue(status, "<html>erro do gateway</html>");

        var ex = await Assert.ThrowsAsync<DiscordException>(() => client.GetCurrentUserAsync("t"));

        Assert.Equal(DiscordErrorKind.ServiceUnavailable, ex.Kind);
        Assert.DoesNotContain("gateway", ex.Message);
    }

    [Fact]
    public async Task SemConexao_GeraNetwork()
    {
        var (client, handler) = Create();
        handler.EnqueueException(new HttpRequestException("No such host is known."));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => client.GetCurrentUserAsync("t"));

        Assert.Equal(DiscordErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task TimeoutDoHttpClient_GeraTimeout()
    {
        var (client, handler) = Create();
        handler.EnqueueException(new TaskCanceledException("timeout"));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => client.GetCurrentUserAsync("t"));

        Assert.Equal(DiscordErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task CancelamentoDoChamador_PropagaOperationCanceled()
    {
        var (client, handler) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        handler.EnqueueException(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCurrentUserAsync("t", cts.Token));
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("null")]
    [InlineData("""{"username":"sem-id"}""")]
    [InlineData("""{"id":"nao-numerico","username":"x"}""")]
    [InlineData("""{"id":"1","username":""}""")]
    [InlineData("""{"id":"1","username":"x","avatar":"../../etc/passwd"}""")]
    public async Task RespostaInvalida_GeraInvalidResponse(string body)
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<DiscordException>(() => client.GetCurrentUserAsync("t"));

        Assert.Equal(DiscordErrorKind.InvalidResponse, ex.Kind);
    }
}

public class AvatarUrlBuilderTests
{
    [Fact]
    public void AvatarPersonalizado_UsaIdHashEPng()
    {
        var user = new DiscordUser { Id = "80351110224678912", Avatar = "8342729096ea3675442027381ff50dfe", Discriminator = "0" };

        Assert.Equal(
            "https://cdn.discordapp.com/avatars/80351110224678912/8342729096ea3675442027381ff50dfe.png?size=128",
            AvatarUrlBuilder.Build(user));
    }

    [Fact]
    public void AvatarAnimado_MantemPrefixo()
    {
        var user = new DiscordUser { Id = "1", Avatar = "a_1269e74af4df7417b13759eae50c83dc" };

        Assert.Equal(
            "https://cdn.discordapp.com/avatars/1/a_1269e74af4df7417b13759eae50c83dc.png?size=256",
            AvatarUrlBuilder.Build(user, 256));
    }

    [Fact]
    public void SemAvatar_SistemaNovo_UsaIdDeslocado()
    {
        var user = new DiscordUser { Id = "80351110224678912", Discriminator = "0" };
        var expectedIndex = (80351110224678912UL >> 22) % 6;

        Assert.Equal($"https://cdn.discordapp.com/embed/avatars/{expectedIndex}.png", AvatarUrlBuilder.Build(user));
    }

    [Fact]
    public void SemAvatar_SistemaLegado_UsaDiscriminador()
    {
        var user = new DiscordUser { Id = "80351110224678912", Discriminator = "1337" };

        Assert.Equal("https://cdn.discordapp.com/embed/avatars/2.png", AvatarUrlBuilder.Build(user));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(8192)]
    public void TamanhoInvalido_Lanca(int size)
    {
        var user = new DiscordUser { Id = "1", Avatar = "abc123" };

        Assert.Throws<ArgumentOutOfRangeException>(() => AvatarUrlBuilder.Build(user, size));
    }

    [Theory]
    [InlineData("8342729096ea3675442027381ff50dfe", true)]
    [InlineData("a_1269e74af4df7417b13759eae50c83dc", true)]
    [InlineData("a_", false)]
    [InlineData("", false)]
    [InlineData("abc/../def", false)]
    [InlineData("abc?x=1", false)]
    public void IsValidHash_AceitaSomenteHexadecimal(string hash, bool expected)
    {
        Assert.Equal(expected, AvatarUrlBuilder.IsValidHash(hash));
    }
}
