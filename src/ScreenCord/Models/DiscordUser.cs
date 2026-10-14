using System.Text.Json.Serialization;

namespace ScreenCord.Models;

/// <summary>
/// Subconjunto do objeto User retornado por <c>GET /users/@me</c> com o escopo <c>identify</c>.
/// Somente campos documentados pela API oficial.
/// </summary>
public sealed record DiscordUser
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>"0" para contas no sistema novo de nomes de usuário.</summary>
    [JsonPropertyName("discriminator")]
    public string? Discriminator { get; init; }

    [JsonPropertyName("global_name")]
    public string? GlobalName { get; init; }

    /// <summary>Hash do avatar; nulo quando o usuário usa o avatar padrão.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }
}
