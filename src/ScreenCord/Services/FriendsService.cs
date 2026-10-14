using System.Net.Http;
using ScreenCord.Contracts;
using ScreenCord.Models;

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

/// <summary>Amigos pela API central; a sessão na API é do <see cref="CentralApiClient"/>.</summary>
public sealed class CentralFriendsService : IFriendsService
{
    private readonly CentralApiClient _api;

    public CentralFriendsService(CentralApiClient api)
    {
        _api = api;
    }

    public async Task<IReadOnlyList<Person>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        var users = await _api.SendAsync<List<UserDto>>(HttpMethod.Get, "friends", null, cancellationToken).ConfigureAwait(false);
        return CentralApiClient.ToPeople(users);
    }

    public async Task<FriendRequests> GetRequestsAsync(CancellationToken cancellationToken = default)
    {
        var requests = await _api.SendAsync<FriendRequestsResponse>(HttpMethod.Get, "friends/requests", null, cancellationToken).ConfigureAwait(false);
        return new FriendRequests(CentralApiClient.ToPeople(requests.Incoming), CentralApiClient.ToPeople(requests.Outgoing));
    }

    public async Task<IReadOnlyList<Person>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        var users = await _api.SendAsync<List<UserDto>>(
            HttpMethod.Get,
            "users/search?q=" + Uri.EscapeDataString(query),
            null,
            cancellationToken).ConfigureAwait(false);
        return CentralApiClient.ToPeople(users);
    }

    public Task SendRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(HttpMethod.Post, "friends/requests", new FriendRequestCreate(userId), cancellationToken, readBody: false);

    public Task AcceptRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(HttpMethod.Post, $"friends/requests/{userId}/accept", null, cancellationToken, readBody: false);

    public Task DeleteRequestAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(HttpMethod.Delete, $"friends/requests/{userId}", null, cancellationToken, readBody: false);

    public Task RemoveFriendAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(HttpMethod.Delete, $"friends/{userId}", null, cancellationToken, readBody: false);

    public Task SignOutAsync(CancellationToken cancellationToken = default) => _api.SignOutAsync(cancellationToken);

    public void ForgetSession() => _api.ForgetSession();
}
