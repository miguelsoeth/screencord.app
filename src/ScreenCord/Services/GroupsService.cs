using System.Net.Http;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Models;

namespace ScreenCord.Services;

/// <summary>Grupos (salas de presença) pela API central. A entrada e a saída das salas ficam no <see cref="IGroupsRealtime"/>.</summary>
public interface IGroupsService
{
    Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default);

    Task<GroupDetails> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default);

    Task<GroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adiciona um amigo do usuário conectado ao grupo.</summary>
    Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default);

    Task LeaveGroupAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Exclui o grupo para todos os integrantes (só quem o criou pode).</summary>
    Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default);
}

public sealed class CentralGroupsService : IGroupsService
{
    private readonly CentralApiClient _api;

    public CentralGroupsService(CentralApiClient api)
    {
        _api = api;
    }

    public async Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _api.SendAsync<List<GroupDto>>(HttpMethod.Get, "groups", null, cancellationToken).ConfigureAwait(false);
        return groups.Where(IsValid).ToList();
    }

    public async Task<GroupDetails> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var group = await _api.SendAsync<GroupDetailsDto>(HttpMethod.Get, $"groups/{groupId}", null, cancellationToken).ConfigureAwait(false);
        var members = new List<GroupMember>();
        foreach (var member in group.Members ?? [])
        {
            if (member is not null &&
                CentralApiClient.ToPerson(member.Id, member.DiscordId, member.Username, member.GlobalName, member.Discriminator, member.AvatarHash, RelationshipStatus.None) is { } person)
            {
                members.Add(new GroupMember(person, member.IsConnected, member.IsOwner));
            }
        }

        return new GroupDetails(group.Id, group.Name ?? "", members, group.PresenceVersion, group.CanDelete);
    }

    public async Task<GroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        var group = await _api.SendAsync<GroupDto>(HttpMethod.Post, "groups", new GroupCreate(name), cancellationToken).ConfigureAwait(false);
        return IsValid(group)
            ? group
            : throw CentralApiClient.Error(DiscordErrorKind.InvalidResponse, "A API central retornou um grupo inválido.");
    }

    public Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(HttpMethod.Post, $"groups/{groupId}/members", new GroupMemberCreate(userId), cancellationToken, readBody: false);

    public Task LeaveGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(HttpMethod.Delete, $"groups/{groupId}/members/me", null, cancellationToken, readBody: false);

    public Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(HttpMethod.Delete, $"groups/{groupId}", null, cancellationToken, readBody: false);

    internal static bool IsValid(GroupDto? group) =>
        group is not null && group.Id != Guid.Empty && !string.IsNullOrWhiteSpace(group.Name) && group.ConnectedUsers is not null;
}
