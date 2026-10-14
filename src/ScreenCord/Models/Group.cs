namespace ScreenCord.Models;

/// <summary>Integrante de um grupo e se ele está conectado à sala agora.</summary>
public sealed record GroupMember(Person Person, bool IsConnected);

public sealed record GroupDetails(Guid Id, string Name, IReadOnlyList<GroupMember> Members, long PresenceVersion);
