using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ScreenCord.Infrastructure;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;

namespace ScreenCord.Tests;

public class RateLimitHandlerTests
{
    private readonly StubHttpHandler _inner = new();
    private readonly List<TimeSpan> _delays = [];

    private HttpClient CreateClient() =>
        new(new RateLimitHandler((delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        })
        {
            InnerHandler = _inner,
        });

    private static Action<HttpResponseMessage> RetryAfter(double seconds) =>
        r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task Resposta429Curta_AguardaERepete()
    {
        _inner.Enqueue(HttpStatusCode.TooManyRequests, configure: RetryAfter(2)).Enqueue(HttpStatusCode.OK, "{}");

        using var response = await CreateClient().GetAsync("https://discord.com/api/v10/users/@me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(2)], _delays);
        Assert.Equal(2, _inner.Requests.Count);
    }

    [Fact]
    public async Task Resposta429Longa_NaoBloqueia_DevolveA429()
    {
        _inner.Enqueue(HttpStatusCode.TooManyRequests, configure: RetryAfter(120));

        using var response = await CreateClient().GetAsync("https://discord.com/api/v10/users/@me");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(_delays);
        Assert.Single(_inner.Requests);
    }

    [Fact]
    public async Task Resposta429Repetida_NaoInsisteAlemDeUmaVez()
    {
        _inner.Enqueue(HttpStatusCode.TooManyRequests, configure: RetryAfter(1))
              .Enqueue(HttpStatusCode.TooManyRequests, configure: RetryAfter(1));

        using var response = await CreateClient().GetAsync("https://discord.com/api/v10/users/@me");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(2, _inner.Requests.Count);
    }

    [Fact]
    public async Task Resposta429SemRetryAfter_NaoRepete()
    {
        _inner.Enqueue(HttpStatusCode.TooManyRequests);

        using var response = await CreateClient().GetAsync("https://discord.com/api/v10/users/@me");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(_inner.Requests);
    }
}

public class LoopbackCallbackListenerTests
{
    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static async Task<(Task<AuthorizationCallback> Listening, Uri RedirectUri)> StartAsync(CancellationToken cancellationToken = default)
    {
        var redirectUri = new Uri($"http://127.0.0.1:{GetFreePort()}/callback");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listening = new LoopbackCallbackListener().ListenAsync(redirectUri, ready.SetResult, cancellationToken);
        await ready.Task;
        return (listening, redirectUri);
    }

    [Fact]
    public async Task RecebeCodigoEState_SemRefletirParametrosNaPagina()
    {
        var (listening, redirectUri) = await StartAsync();
        using var http = new HttpClient();

        var html = await http.GetStringAsync(redirectUri + "?code=abc123&state=xyz%3Cscript%3E");
        var callback = await listening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("abc123", callback.Code);
        Assert.Equal("xyz<script>", callback.State);
        Assert.Null(callback.Error);
        Assert.DoesNotContain("abc123", html);
        Assert.DoesNotContain("xyz", html);
    }

    [Fact]
    public async Task RecebeErroDeAutorizacao()
    {
        var (listening, redirectUri) = await StartAsync();
        using var http = new HttpClient();

        await http.GetStringAsync(redirectUri + "?error=access_denied&state=s");
        var callback = await listening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(callback.Code);
        Assert.Equal("access_denied", callback.Error);
    }

    [Fact]
    public async Task IgnoraOutrosCaminhos_EContinuaAguardando()
    {
        var (listening, redirectUri) = await StartAsync();
        using var http = new HttpClient();

        var notFound = await http.GetAsync($"http://127.0.0.1:{redirectUri.Port}/favicon.ico");
        var badRequest = await http.GetAsync(redirectUri);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.False(listening.IsCompleted);

        await http.GetStringAsync(redirectUri + "?code=c&state=s");
        Assert.Equal("c", (await listening.WaitAsync(TimeSpan.FromSeconds(10))).Code);
    }

    [Fact]
    public async Task Cancelamento_EncerraOServidor()
    {
        using var cts = new CancellationTokenSource();
        var (listening, redirectUri) = await StartAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listening.WaitAsync(TimeSpan.FromSeconds(10)));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(redirectUri + "?code=c&state=s"));
    }

    [Fact]
    public async Task RedirectUriNaoLoopback_EhRecusado()
    {
        var ex = await Assert.ThrowsAsync<DiscordException>(() =>
            new LoopbackCallbackListener().ListenAsync(new Uri("http://example.com:8080/callback"), () => { }, CancellationToken.None));

        Assert.Equal(DiscordErrorKind.Configuration, ex.Kind);
    }
}

public class DiscordOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void ClientIdInvalido_Lanca(string clientId)
    {
        var options = new DiscordOptions { ClientId = clientId };

        var ex = Assert.Throws<DiscordException>(options.Validate);

        Assert.Equal(DiscordErrorKind.Configuration, ex.Kind);
    }

    [Fact]
    public void ConfiguracaoValida_MontaRedirectUriDeLoopback()
    {
        var options = new DiscordOptions { ClientId = "123", RedirectPort = 5000, RedirectPath = "/cb" };

        options.Validate();

        Assert.Equal("http://127.0.0.1:5000/cb", options.RedirectUri);
    }
}

public class ErrorMessagesTests
{
    [Theory]
    [InlineData(DiscordErrorKind.Network, "conexão")]
    [InlineData(DiscordErrorKind.AuthorizationDenied, "negada")]
    [InlineData(DiscordErrorKind.Unauthorized, "sessão expirou")]
    [InlineData(DiscordErrorKind.ServiceUnavailable, "indisponível")]
    [InlineData(DiscordErrorKind.InvalidState, "segurança")]
    public void CadaTipoDeErro_TemMensagemPropria(DiscordErrorKind kind, string expected)
    {
        var message = ErrorMessages.For(new DiscordException(kind, "detalhe interno"));

        Assert.Contains(expected, message, StringComparison.CurrentCultureIgnoreCase);
        Assert.DoesNotContain("detalhe interno", message);
    }

    [Fact]
    public void RateLimit_InformaTempoDeEspera()
    {
        var message = ErrorMessages.For(new DiscordException(DiscordErrorKind.RateLimited, "x") { RetryAfter = TimeSpan.FromSeconds(64.57) });

        Assert.Contains("65 s", message);
    }

    [Fact]
    public void InvalidClient_OrientaAtivarPublicClient()
    {
        var message = ErrorMessages.For(new DiscordException(DiscordErrorKind.OAuthRejected, "x") { ErrorCode = "invalid_client" });

        Assert.Contains("Public Client", message);
    }

    [Fact]
    public void ExcecaoDesconhecida_NaoVazaDetalhes()
    {
        var message = ErrorMessages.For(new InvalidOperationException("segredo interno"));

        Assert.DoesNotContain("segredo interno", message);
    }
}
