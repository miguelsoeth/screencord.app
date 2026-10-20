using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Services;
using Microsoft.Extensions.Logging;
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

public class ApiOptionsTests
{
    [Theory]
    [InlineData("https://154.16.50.20", "https://154.16.50.20/")]
    [InlineData("https://api.exemplo.com/base", "https://api.exemplo.com/base/")]
    [InlineData("http://localhost:5080", "http://localhost:5080/")]
    [InlineData("http://127.0.0.1:5080/", "http://127.0.0.1:5080/")]
    public void EnderecoValido_TerminaComBarra(string baseUrl, string expected)
    {
        var uri = new ApiOptions { BaseUrl = baseUrl }.GetBaseUri();

        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://154.16.50.20")]
    [InlineData("ws://localhost:5080")]
    public void HttpForaDeLoopback_Lanca(string baseUrl)
    {
        var ex = Assert.Throws<DiscordException>(() => new ApiOptions { BaseUrl = baseUrl }.GetBaseUri());

        Assert.Equal(DiscordErrorKind.Configuration, ex.Kind);
    }

    [Fact]
    public void Producao_HubEMidiaUsamHttpsEWss()
    {
        var baseUri = new ApiOptions { BaseUrl = "https://154.16.50.20" }.GetBaseUri();

        Assert.Equal("https://154.16.50.20/hubs/groups", new Uri(baseUri, GroupsHubContract.Path.TrimStart('/')).AbsoluteUri);
        Assert.StartsWith("wss://154.16.50.20/media/", MediaContract.BuildUri(baseUri, Guid.NewGuid(), Guid.NewGuid()).AbsoluteUri);
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

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ScreenCord.Tests", "logs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Grava_UmArquivoPorDia_ComNivelCategoriaEExcecao()
    {
        using (var provider = new FileLoggerProvider(_folder, _time))
        {
            var logger = provider.CreateLogger("ScreenCord.Audio");
            logger.LogWarning(new InvalidOperationException("sem dispositivo"), "Captura de áudio indisponível.");
            _time.Now = _time.Now.AddDays(1);
            logger.LogInformation("Outro dia.");
        }

        var files = Directory.GetFiles(_folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(2, files.Count);
        var first = File.ReadAllText(Path.Combine(_folder, files[0]!));
        Assert.Contains("WRN ScreenCord.Audio: Captura de áudio indisponível.", first);
        Assert.Contains("InvalidOperationException: sem dispositivo", first);
        Assert.Contains("INF ScreenCord.Audio: Outro dia.", File.ReadAllText(Path.Combine(_folder, files[1]!)));
    }

    [Fact]
    public void ApagaOsArquivosMaisAntigosQueOPrazo()
    {
        Directory.CreateDirectory(_folder);
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var old = Path.Combine(_folder, $"screencord-{today.AddDays(-FileLoggerProvider.KeepDays):yyyyMMdd}.log");
        var kept = Path.Combine(_folder, $"screencord-{today.AddDays(-(FileLoggerProvider.KeepDays - 1)):yyyyMMdd}.log");
        File.WriteAllText(old, "");
        File.WriteAllText(kept, "");

        using (var provider = new FileLoggerProvider(_folder, _time))
        {
            provider.CreateLogger("x").LogInformation("hoje");
        }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public class SingleInstanceTests
{
    [Fact]
    public async Task SecondInstance_IsRefusedAndActivatesTheFirst()
    {
        var key = $"Tests.{Guid.NewGuid():N}";
        using var first = SingleInstance.Acquire(key);
        var activated = new TaskCompletionSource();
        first.OnActivationRequested(() => activated.TrySetResult());

        using (var second = SingleInstance.Acquire(key))
        {
            Assert.True(first.IsFirst);
            Assert.False(second.IsFirst);
        }

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AfterTheFirstCloses_ANewInstanceIsFirstAgain()
    {
        var key = $"Tests.{Guid.NewGuid():N}";
        SingleInstance.Acquire(key).Dispose();

        using var next = SingleInstance.Acquire(key);
        Assert.True(next.IsFirst);
    }
}
