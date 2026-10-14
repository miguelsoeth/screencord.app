using System.Net.Http;
using System.Net.Http.Headers;
using ScreenCord.Infrastructure;
using ScreenCord.Models;

namespace ScreenCord.Services;

public interface IDiscordApiClient
{
    /// <summary><c>GET /users/@me</c> — requer o escopo <c>identify</c>.</summary>
    Task<DiscordUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default);
}

public sealed class DiscordApiClient : IDiscordApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public DiscordApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<DiscordUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        var client = _httpClientFactory.CreateClient(DiscordHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, DiscordHttp.ApiVersionPrefix + "users/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await DiscordHttp.SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await DiscordHttp.CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var user = await DiscordHttp.ReadJsonAsync<DiscordUser>(response, cancellationToken).ConfigureAwait(false);
        Validate(user);
        return user;
    }

    private static void Validate(DiscordUser user)
    {
        // Snowflakes são inteiros de 64 bits serializados como string.
        if (string.IsNullOrEmpty(user.Id) || !ulong.TryParse(user.Id, out _))
        {
            throw new DiscordException(DiscordErrorKind.InvalidResponse, "O Discord retornou um usuário sem ID válido.");
        }

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            throw new DiscordException(DiscordErrorKind.InvalidResponse, "O Discord retornou um usuário sem nome.");
        }

        if (user.Avatar is not null && !AvatarUrlBuilder.IsValidHash(user.Avatar))
        {
            throw new DiscordException(DiscordErrorKind.InvalidResponse, "O Discord retornou um hash de avatar inválido.");
        }
    }
}
