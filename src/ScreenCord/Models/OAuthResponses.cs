using System.Text.Json.Serialization;

namespace ScreenCord.Models;

/// <summary>Resposta do endpoint <c>/oauth2/token</c>.</summary>
public sealed record OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>Corpo de erro OAuth2 (RFC 6749, seção 5.2).</summary>
public sealed record OAuthErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Corpo de uma resposta HTTP 429 da API.</summary>
public sealed record RateLimitResponse
{
    [JsonPropertyName("retry_after")]
    public double? RetryAfter { get; init; }

    [JsonPropertyName("global")]
    public bool Global { get; init; }
}
