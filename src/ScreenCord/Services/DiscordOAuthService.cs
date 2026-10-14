using System.Net;
using System.Net.Http;
using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ScreenCord.Services;

public interface IDiscordOAuthService
{
    /// <summary>Executa o fluxo Authorization Code + PKCE no navegador padrão.</summary>
    Task<TokenSession> AuthorizeAsync(CancellationToken cancellationToken = default);

    Task<TokenSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task RevokeAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>
/// OAuth2 do Discord como cliente público: a troca do código usa <c>code_verifier</c> (PKCE S256)
/// e nenhum <c>client_secret</c> é enviado ou embutido no aplicativo.
/// </summary>
public sealed class DiscordOAuthService : IDiscordOAuthService
{
    public const string AuthorizeEndpoint = "https://discord.com/oauth2/authorize";
    private const string TokenPath = "oauth2/token";
    private const string RevokePath = "oauth2/token/revoke";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthorizationCallbackListener _listener;
    private readonly IBrowserLauncher _browser;
    private readonly IOptions<DiscordOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscordOAuthService> _logger;

    public DiscordOAuthService(
        IHttpClientFactory httpClientFactory,
        IAuthorizationCallbackListener listener,
        IBrowserLauncher browser,
        IOptions<DiscordOptions> options,
        TimeProvider time,
        ILogger<DiscordOAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _listener = listener;
        _browser = browser;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<TokenSession> AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        options.Validate();

        var codeVerifier = Pkce.CreateCodeVerifier();
        var state = Pkce.CreateState();
        var redirectUri = new Uri(options.RedirectUri);
        var authorizeUrl = BuildAuthorizeUrl(options, Pkce.CreateCodeChallenge(codeVerifier), state);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, options.LoginTimeoutSeconds)));

        AuthorizationCallback callback;
        try
        {
            _logger.LogInformation("Aguardando autorização do usuário no navegador.");
            callback = await _listener
                .ListenAsync(redirectUri, () => _browser.Open(authorizeUrl), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DiscordException(DiscordErrorKind.Timeout, "O login não foi concluído dentro do tempo limite.");
        }

        // O state é validado antes de qualquer outro parâmetro: callbacks forjados são descartados por inteiro.
        if (!Pkce.FixedTimeEquals(state, callback.State))
        {
            _logger.LogWarning("Callback de autorização descartado: state inválido.");
            throw new DiscordException(DiscordErrorKind.InvalidState, "O parâmetro state do callback não confere.");
        }

        if (callback.Error is not null)
        {
            throw callback.Error == "access_denied"
                ? new DiscordException(DiscordErrorKind.AuthorizationDenied, "O usuário negou a autorização.") { ErrorCode = callback.Error }
                : new DiscordException(DiscordErrorKind.OAuthRejected, "O Discord retornou um erro de autorização.") { ErrorCode = SanitizeErrorCode(callback.Error) };
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            throw new DiscordException(DiscordErrorKind.InvalidResponse, "O callback não trouxe um código de autorização.");
        }

        return await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = callback.Code,
                ["redirect_uri"] = options.RedirectUri,
                ["code_verifier"] = codeVerifier,
            },
            previousRefreshToken: null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<TokenSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var options = _options.Value;
        options.Validate();

        return RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            },
            previousRefreshToken: refreshToken,
            cancellationToken);
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var options = _options.Value;
        options.Validate();

        var client = _httpClientFactory.CreateClient(DiscordHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokePath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["token"] = token,
            }),
        };

        using var response = await DiscordHttp.SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateOAuthExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public static Uri BuildAuthorizeUrl(DiscordOptions options, string codeChallenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = options.ClientId,
            ["scope"] = options.Scopes.Trim(),
            ["state"] = state,
            ["redirect_uri"] = options.RedirectUri,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        return new Uri(AuthorizeEndpoint + "?" + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")));
    }

    private async Task<TokenSession> RequestTokenAsync(
        Dictionary<string, string> form,
        string? previousRefreshToken,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(DiscordHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenPath) { Content = new FormUrlEncodedContent(form) };

        using var response = await DiscordHttp.SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateOAuthExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var token = await DiscordHttp.ReadJsonAsync<OAuthTokenResponse>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.AccessToken) ||
            !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            token.ExpiresIn <= 0)
        {
            throw new DiscordException(DiscordErrorKind.InvalidResponse, "O Discord retornou uma resposta de token inválida.");
        }

        return new TokenSession(
            token.AccessToken,
            string.IsNullOrEmpty(token.RefreshToken) ? previousRefreshToken : token.RefreshToken,
            _time.GetUtcNow().AddSeconds(token.ExpiresIn),
            token.Scope);
    }

    private static async Task<DiscordException> CreateOAuthExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var error = await DiscordHttp.TryReadJsonAsync<OAuthErrorResponse>(response, cancellationToken).ConfigureAwait(false);
            return new DiscordException(DiscordErrorKind.OAuthRejected, "O endpoint OAuth2 do Discord rejeitou a requisição.")
            {
                StatusCode = response.StatusCode,
                ErrorCode = SanitizeErrorCode(error?.Error),
            };
        }

        return await DiscordHttp.CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // Códigos de erro OAuth2 são identificadores curtos; qualquer outra coisa não é repassada a mensagens ou logs.
    private static string? SanitizeErrorCode(string? code) =>
        code is { Length: > 0 and <= 40 } && code.All(c => char.IsAsciiLetterLower(c) || c == '_') ? code : null;
}
