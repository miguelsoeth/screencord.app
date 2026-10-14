using ScreenCord.Contracts;

namespace ScreenCord.Models;

/// <summary>Usuário do sistema ScreenCord, com a relação dele com a conta conectada.</summary>
public sealed record Person(
    Guid UserId,
    string DiscordId,
    string Username,
    string? GlobalName,
    string AvatarUrl,
    RelationshipStatus Relationship)
{
    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName;

    public string Handle => $"@{Username}";

    public bool CanAdd => Relationship == RelationshipStatus.None;

    public bool CanAccept => Relationship == RelationshipStatus.PendingIncoming;

    public bool IsPendingOutgoing => Relationship == RelationshipStatus.PendingOutgoing;

    public bool IsFriend => Relationship == RelationshipStatus.Friend;
}

public sealed record FriendRequests(IReadOnlyList<Person> Incoming, IReadOnlyList<Person> Outgoing)
{
    public static FriendRequests Empty { get; } = new([], []);
}
