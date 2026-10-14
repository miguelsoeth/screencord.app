using System.Net;
using ScreenCord.Infrastructure;
using ScreenCord.Security;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ScreenCord.Tests;

public class DiscordOAuthServiceTests
{
    private const string TokenJson = """
        {"access_token":"access-1","token_type":"Bearer","expires_in":604800,"refresh_token":"refresh-1","scope":"identify"}
        """;

    private readonly StubHttpHandler _handler = new();
    private readonly FakeBrowserLauncher _browser = new();
    private readonly FakeTimeProvider _time = new();

    private DiscordOAuthService Create(
        Func<string?, AuthorizationCallback>? respond,
        IOptions<DiscordOptions>? options = null) =>
        new(
            new StubHttpClientFactory(_handler),
            new FakeCallbackListener(_browser, respond),
            _browser,
            options ?? TestOptions.Create(),
            _time,
            NullLogger<DiscordOAuthService>.Instance);

    [Fact]
    public async Task Authorize_AbreNavegadorComPkceEState()
    {
        _handler.Enqueue(HttpStatusCode.OK, TokenJson);
        var service = Create(state => new AuthorizationCallback("code-1", state, null));

        await service.AuthorizeAsync();

        var url = _browser.OpenedUrl!;
        Assert.StartsWith("https://discord.com/oauth2/authorize?", url.AbsoluteUri);
        Assert.Equal("code", _browser.Query("response_type"));
        Assert.Equal(TestOptions.ClientId, _browser.Query("client_id"));
        Assert.Equal("identify", _browser.Query("scope"));
        Assert.Equal("http://127.0.0.1:47821/callback", _browser.Query("redirect_uri"));
        Assert.Equal("S256", _browser.Query("code_challenge_method"));
        Assert.False(string.IsNullOrEmpty(_browser.Query("state")));
        Assert.False(string.IsNullOrEmpty(_browser.Query("code_challenge")));
    }

    [Fact]
    public async Task Authorize_TrocaOCodigoComCodeVerifierESemClientSecret()
    {
        _handler.Enqueue(HttpStatusCode.OK, TokenJson);
        var service = Create(state => new AuthorizationCallback("code-1", state, null));

        var session = await service.AuthorizeAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://discord.com/api/oauth2/token", request.Uri.AbsoluteUri);
        Assert.Equal("authorization_code", request.Form("grant_type"));
        Assert.Equal("code-1", request.Form("code"));
        Assert.Equal(TestOptions.ClientId, request.Form("client_id"));
        Assert.Equal("http://127.0.0.1:47821/callback", request.Form("redirect_uri"));
        Assert.Null(request.Form("client_secret"));
        Assert.Null(request.Authorization);

        // O verifier enviado precisa corresponder ao challenge da URL de autorização.
        var verifier = request.Form("code_verifier")!;
        Assert.Equal(_browser.Query("code_challenge"), Pkce.CreateCodeChallenge(verifier));

        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal("refresh-1", session.RefreshToken);
        Assert.Equal(_time.Now.AddSeconds(604800), session.ExpiresAtUtc);
    }

    [Fact]
    public async Task Authorize_StateDivergente_DescartaSemTrocarOCodigo()
    {
        var service = Create(_ => new AuthorizationCallback("code-do-atacante", "state-forjado", null));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.AuthorizeAsync());

        Assert.Equal(DiscordErrorKind.InvalidState, ex.Kind);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Authorize_StateAusente_Descarta()
    {
        var service = Create(_ => new AuthorizationCallback("code", null, null));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.AuthorizeAsync());

        Assert.Equal(DiscordErrorKind.InvalidState, ex.Kind);
    }

    [Fact]
    public async Task Authorize_AutorizacaoNegada_GeraAuthorizationDenied()
    {
        var service = Create(state => new AuthorizationCallback(null, state, "access_denied"));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.AuthorizeAsync());

        Assert.Equal(DiscordErrorKind.AuthorizationDenied, ex.Kind);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Authorize_CanceladoPeloUsuario_PropagaCancelamento()
    {
        var service = Create(respond: null);
        using var cts = new CancellationTokenSource();

        var task = service.AuthorizeAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Authorize_SemClientId_GeraErroDeConfiguracaoSemAbrirNavegador()
    {
        var service = Create(state => new AuthorizationCallback("c", state, null), TestOptions.Create(o => o.ClientId = ""));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.AuthorizeAsync());

        Assert.Equal(DiscordErrorKind.Configuration, ex.Kind);
        Assert.Null(_browser.OpenedUrl);
    }

    [Fact]
    public async Task Authorize_CodigoRejeitado_GeraOAuthRejectedComCodigoDeErro()
    {
        _handler.Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Invalid \"code\" in request."}""");
        var service = Create(state => new AuthorizationCallback("code-1", state, null));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.AuthorizeAsync());

        Assert.Equal(DiscordErrorKind.OAuthRejected, ex.Kind);
        Assert.Equal("invalid_grant", ex.ErrorCode);
    }

    [Theory]
    [InlineData("""{"access_token":"","token_type":"Bearer","expires_in":10}""")]
    [InlineData("""{"access_token":"a","token_type":"MAC","expires_in":10}""")]
    [InlineData("""{"access_token":"a","token_type":"Bearer","expires_in":0}""")]
    [InlineData("não é json")]
    public async Task Authorize_RespostaDeTokenInvalida_GeraInvalidResponse(string body)
    {
        _handler.Enqueue(HttpStatusCode.OK, body);
        var service = Create(state => new AuthorizationCallback("code-1", state, null));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.AuthorizeAsync());

        Assert.Equal(DiscordErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task Authorize_SemConexaoNaTroca_GeraNetwork()
    {
        _handler.EnqueueException(new HttpRequestException("offline"));
        var service = Create(state => new AuthorizationCallback("code-1", state, null));

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.AuthorizeAsync());

        Assert.Equal(DiscordErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Refresh_EnviaRefreshTokenSemSegredo()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"access_token":"access-2","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh-2","scope":"identify"}""");
        var service = Create(respond: null);

        var session = await service.RefreshAsync("refresh-1");

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("refresh_token", request.Form("grant_type"));
        Assert.Equal("refresh-1", request.Form("refresh_token"));
        Assert.Null(request.Form("client_secret"));
        Assert.Equal("access-2", session.AccessToken);
        Assert.Equal("refresh-2", session.RefreshToken);
    }

    [Fact]
    public async Task Refresh_SemNovoRefreshToken_MantemOAnterior()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"access_token":"access-2","token_type":"Bearer","expires_in":3600}""");
        var service = Create(respond: null);

        var session = await service.RefreshAsync("refresh-1");

        Assert.Equal("refresh-1", session.RefreshToken);
    }

    [Fact]
    public async Task Refresh_ApiIndisponivel_GeraServiceUnavailable()
    {
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        var service = Create(respond: null);

        var ex = await Assert.ThrowsAsync<DiscordException>(() => service.RefreshAsync("refresh-1"));

        Assert.Equal(DiscordErrorKind.ServiceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task Revoke_EnviaTokenParaOEndpointDeRevogacao()
    {
        _handler.Enqueue(HttpStatusCode.OK, "{}");
        var service = Create(respond: null);

        await service.RevokeAsync("refresh-1");

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://discord.com/api/oauth2/token/revoke", request.Uri.AbsoluteUri);
        Assert.Equal("refresh-1", request.Form("token"));
    }
}
