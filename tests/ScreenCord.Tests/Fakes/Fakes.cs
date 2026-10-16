using System.Net;
using System.Text;
using System.Web;
using ScreenCord.Infrastructure;
using ScreenCord.Security;
using ScreenCord.Services;
using Microsoft.Extensions.Options;

namespace ScreenCord.Tests.Fakes;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body)
{
    public string? Form(string key) => HttpUtility.ParseQueryString(Body)[key];

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Responde às requisições com uma fila de respostas pré-definidas e grava o que foi enviado.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public StubHttpHandler Enqueue(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status);
            if (json is not null)
            {
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            configure?.Invoke(response);
            return response;
        });
        return this;
    }

    public StubHttpHandler EnqueueException(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body)
        {
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
        });

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("Requisição inesperada: " + request.RequestUri);
        }

        return _responses.Dequeue()();
    }
}

public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { BaseAddress = DiscordHttp.BaseAddress };
}

public sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class InMemoryTokenStore : ITokenStore
{
    public TokenSession? Session { get; set; }

    public int SaveCount { get; private set; }

    public Task<TokenSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);

    public Task SaveAsync(TokenSession session, CancellationToken cancellationToken = default)
    {
        Session = session;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Session = null;
        return Task.CompletedTask;
    }
}

public sealed class FakeBrowserLauncher : IBrowserLauncher
{
    public Uri? OpenedUrl { get; private set; }

    public void Open(Uri url) => OpenedUrl = url;

    public string? Query(string key) =>
        OpenedUrl is null ? null : HttpUtility.ParseQueryString(OpenedUrl.Query)[key];
}

/// <summary>Simula o redirect do Discord a partir da URL de autorização aberta no "navegador".</summary>
public sealed class FakeCallbackListener : IAuthorizationCallbackListener
{
    private readonly FakeBrowserLauncher _browser;
    private readonly Func<string?, AuthorizationCallback>? _respond;

    /// <param name="respond">Recebe o <c>state</c> enviado ao Discord; nulo faz o listener aguardar até o cancelamento.</param>
    public FakeCallbackListener(FakeBrowserLauncher browser, Func<string?, AuthorizationCallback>? respond)
    {
        _browser = browser;
        _respond = respond;
    }

    public async Task<AuthorizationCallback> ListenAsync(Uri redirectUri, Action onListening, CancellationToken cancellationToken)
    {
        onListening();
        if (_respond is null)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return _respond!(_browser.Query("state"));
    }
}

public static class TestOptions
{
    public const string ClientId = "123456789012345678";

    public static IOptions<DiscordOptions> Create(Action<DiscordOptions>? configure = null)
    {
        var options = new DiscordOptions { ClientId = ClientId };
        configure?.Invoke(options);
        return Options.Create(options);
    }
}
