namespace ScreenCord.Models;

/// <summary>Integrante de um grupo, se ele está conectado à sala agora e se é quem criou o grupo.</summary>
public sealed record GroupMember(Person Person, bool IsConnected, bool IsOwner = false);

public sealed record GroupDetails(Guid Id, string Name, IReadOnlyList<GroupMember> Members, long PresenceVersion, bool CanDelete = false);
