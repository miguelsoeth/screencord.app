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
/// Cliente da API central, compartilhado por amigos, grupos e a conexão em tempo real. O login na API
/// acontece sob demanda: o access token do Discord é trocado por um token de sessão da própria API,
/// mantido só em memória e renovado quando a API responde 401.
/// </summary>
public sealed class CentralApiClient
{
    public const string ClientName = "CentralApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISessionManager _session;
    private readonly IOptions<ApiOptions> _options;
    private readonly ILogger<CentralApiClient> _logger;
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    private string? _apiToken;

    public CentralApiClient(
        IHttpClientFactory httpClientFactory,
        ISessionManager session,
        IOptions<ApiOptions> options,
        ILogger<CentralApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _session = session;
        _options = options;
        _logger = logger;
    }

    public Uri BaseUri => _options.Value.GetBaseUri();

    /// <summary>Id do usuário na API central, conhecido depois do primeiro login na sessão.</summary>
    public Guid? CurrentUserId { get; private set; }

    public async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        bool readBody = true)
        where T : class
    {
        var baseUri = BaseUri;

        for (var attempt = 0; ; attempt++)
        {
            var token = await GetApiTokenAsync(cancellationToken).ConfigureAwait(false);

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
                InvalidateToken(token);
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

    /// <summary>Token de sessão da API; faz o login com o token do Discord se ainda não houver um.</summary>
    public async Task<string> GetApiTokenAsync(CancellationToken cancellationToken = default)
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

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "auth/discord"))
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
            CurrentUserId = login.User?.Id;
            return login.Token;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>Descarta o token se ele ainda for o atual (a API o recusou).</summary>
    public void InvalidateToken(string token) => Interlocked.CompareExchange(ref _apiToken, null, token);

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
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "auth/logout"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordException ex)
        {
            _logger.LogWarning("Não foi possível encerrar a sessão na API central ({Kind}).", ex.Kind);
        }
    }

    public void ForgetSession()
    {
        _apiToken = null;
        CurrentUserId = null;
    }

    public static List<Person> ToPeople(IEnumerable<UserDto>? users)
    {
        var people = new List<Person>();
        foreach (var user in users ?? [])
        {
            if (user is not null && ToPerson(user.Id, user.DiscordId, user.Username, user.GlobalName, user.Discriminator, user.AvatarHash, user.Relationship) is { } person)
            {
                people.Add(person);
            }
        }

        return people;
    }

    public static List<Person> ToPeople(IEnumerable<PresenceUserDto>? users)
    {
        var people = new List<Person>();
        foreach (var user in users ?? [])
        {
            if (user is not null && ToPerson(user.Id, user.DiscordId, user.Username, user.GlobalName, user.Discriminator, user.AvatarHash, RelationshipStatus.None) is { } person)
            {
                people.Add(person);
            }
        }

        return people;
    }

    /// <summary>Os dados vêm do servidor, mas vão para a tela e para URLs: são validados mesmo assim.</summary>
    public static Person? ToPerson(
        Guid id,
        string? discordId,
        string? username,
        string? globalName,
        string? discriminator,
        string? avatarHash,
        RelationshipStatus relationship)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(username) || !ulong.TryParse(discordId, out _))
        {
            return null;
        }

        var avatarUrl = AvatarUrlBuilder.Build(
            new DiscordUser { Id = discordId, Avatar = avatarHash, Discriminator = discriminator },
            size: 64);
        return new Person(id, discordId, username, globalName, avatarUrl, relationship);
    }

    public static DiscordException Error(DiscordErrorKind kind, string message, Exception? inner = null, HttpStatusCode? status = null) =>
        new(kind, message, inner) { Source = ErrorSource.CentralApi, StatusCode = status };

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
}
