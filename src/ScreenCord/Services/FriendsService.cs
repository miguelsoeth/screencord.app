using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ScreenCord.Services;

/// <summary>
/// Amigos no sistema ScreenCord (API central). Não é a lista de amigos do Discord, que a API
/// oficial não disponibiliza: são pessoas que também entraram neste sistema com a conta do Discord.
/// </summary>
public interface IFriendsService
{
    Task<IReadOnlyList<Person>> GetFriendsAsync(CancellationToken cancellationToken = default);

    Task<FriendRequests> GetRequestsAsync(CancellationToken cancellationToken = default);

    /// <summary>Busca usuários do sistema por nome de usuário ou de exibição.</summary>
    Task<IReadOnlyList<Person>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);

    Task SendRequestAsync(Guid userId, CancellationToken cancellationToken = default);

    Task AcceptRequestAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Recusa uma solicitação recebida ou cancela uma enviada.</summary>
    Task DeleteRequestAsync(Guid userId, CancellationToken cancellationToken = default);

    Task RemoveFriendAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Encerra a sessão na API (melhor esforço) e esquece o token local.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>Descarta o token da API guardado em memória, sem chamada de rede.</summary>
    void ForgetSession();
}

/// <summary>
/// Cliente da API central. O login na API acontece sob demanda: o access token do Discord é trocado
/// por um token de sessão da própria API, mantido só em memória e renovado quando a API responde 401.
/// </summary>
public sealed class CentralFriendsService : IFriendsService
{
    public const string ClientName = "CentralApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISessionManager _session;
    private readonly IOptions<ApiOptions> _options;
    private readonly ILogger<CentralFriendsService> _logger;
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    private string? _apiToken;

    public CentralFriendsService(
        IHttpClientFactory httpClientFactory,
        ISessionManager session,
        IOptions<ApiOptions> options,
        ILogger<CentralFriendsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _session = session;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Person>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        var users = await SendAsync<List<UserDto>>(HttpMethod.Get, "friends", null, cancellationToken).ConfigureAwait(false);
        return ToPeople(users);
    }

    public async Task<FriendRequests> GetRequestsAsync(CancellationToken cancellationToken = default)
    {
        var requests = await SendAsync<FriendRequestsResponse>(HttpMethod.Get, "friends/requests", null, cancellationToken).ConfigureAwait(false);
        return new FriendRequests(ToPeople(requests.Incoming), ToPeople(requests.Outgoing));
    }

    public async Task<IReadOnlyList<Person>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        var users = await SendAsync<List<UserDto>>(
            HttpMethod.Get,
            "users/search?q=" + Uri.EscapeDataString(query),
            null,
            cancellationToken).ConfigureAwait(false);
        return ToPeople(users);
    }

    public Task SendRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Post, "friends/requests", new FriendRequestCreate(userId), cancellationToken, readBody: false);

    public Task AcceptRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Post, $"friends/requests/{userId}/accept", null, cancellationToken, readBody: false);

    public Task DeleteRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"friends/requests/{userId}", null, cancellationToken, readBody: false);

    public Task RemoveFriendAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"friends/{userId}", null, cancellationToken, readBody: false);

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var token = _apiToken;
        ForgetSession();
        if (token is null)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.Value.GetBaseUri(), "auth/logout"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordException ex)
        {
            _logger.LogWarning("Não foi possível encerrar a sessão na API central ({Kind}).", ex.Kind);
        }
    }

    public void ForgetSession() => _apiToken = null;

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        bool readBody = true)
        where T : class
    {
        var baseUri = _options.Value.GetBaseUri();

        for (var attempt = 0; ; attempt++)
        {
            var token = await GetApiTokenAsync(baseUri, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            using var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);

            // Sessão da API vencida ou revogada: refaz o login na API uma vez, sem envolver o usuário.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                Interlocked.CompareExchange(ref _apiToken, null, token);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (!readBody)
            {
                return null!;
            }

            return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetApiTokenAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        if (_apiToken is { } cached)
        {
            return cached;
        }

        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_apiToken is { } existing)
            {
                return existing;
            }

            var discordToken = await _session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "auth/discord"))
            {
                Content = JsonContent.Create(new DiscordLoginRequest(discordToken)),
            };
            using var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
            }

            var login = await ReadJsonAsync<LoginResponse>(response, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(login.Token))
            {
                throw Error(DiscordErrorKind.InvalidResponse, "A API central não retornou um token de sessão.");
            }

            _logger.LogInformation("Sessão iniciada na API central; válida até {ExpiresAtUtc:O}.", login.ExpiresAtUtc);
            _apiToken = login.Token;
            return login.Token;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClientFactory.CreateClient(ClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw Error(DiscordErrorKind.Timeout, "Tempo limite excedido ao contatar a API central.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Error(DiscordErrorKind.Network, "Falha de rede ao contatar a API central.", ex);
        }
    }

    private static async Task<DiscordException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.Unauthorized)
        {
            return Error(DiscordErrorKind.Unauthorized, "A API central recusou a sessão.", status: status);
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return Error(DiscordErrorKind.RateLimited, "Limite de requisições da API central atingido.", status: status);
        }

        if ((int)status >= 500)
        {
            return Error(DiscordErrorKind.ServiceUnavailable, $"A API central está indisponível (HTTP {(int)status}).", status: status);
        }

        // Erros 4xx trazem um texto próprio para exibição (ex.: "Vocês já são amigos.").
        ErrorResponse? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or NotSupportedException)
        {
        }

        return error is { Message.Length: > 0 and <= 300 }
            ? new DiscordException(DiscordErrorKind.Rejected, error.Message) { Source = ErrorSource.CentralApi, StatusCode = status, ErrorCode = error.Code }
            : Error(DiscordErrorKind.Unexpected, $"Resposta inesperada da API central (HTTP {(int)status}).", status: status);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                ?? throw Error(DiscordErrorKind.InvalidResponse, "A API central retornou um corpo vazio.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw Error(DiscordErrorKind.InvalidResponse, "A API central retornou um corpo inválido.", ex);
        }
    }

    private static List<Person> ToPeople(IEnumerable<UserDto>? users)
    {
        var people = new List<Person>();
        foreach (var user in users ?? [])
        {
            // Os dados vêm do servidor, mas vão para a tela e para URLs: são validados mesmo assim.
            if (user is null || user.Id == Guid.Empty || string.IsNullOrWhiteSpace(user.Username) || !ulong.TryParse(user.DiscordId, out _))
            {
                continue;
            }

            var avatarUrl = AvatarUrlBuilder.Build(
                new DiscordUser { Id = user.DiscordId, Avatar = user.AvatarHash, Discriminator = user.Discriminator },
                size: 64);
            people.Add(new Person(user.Id, user.DiscordId, user.Username, user.GlobalName, avatarUrl, user.Relationship));
        }

        return people;
    }

    private static DiscordException Error(DiscordErrorKind kind, string message, Exception? inner = null, HttpStatusCode? status = null) =>
        new(kind, message, inner) { Source = ErrorSource.CentralApi, StatusCode = status };
}
