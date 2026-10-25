using ScreenCord.Contracts;

namespace ScreenCord.Models;

/// <summary>Integrante de um grupo, se ele está conectado à sala agora e se é quem criou o grupo.</summary>
public sealed record GroupMember(Person Person, bool IsConnected, bool IsOwner = false);

/// <param name="Settings">O que o grupo aceita transmitir (tela e câmera).</param>
/// <param name="CanEdit">Quem pediu os detalhes pode mudar as configurações do grupo (só quem o criou).</param>
public sealed record GroupDetails(
    Guid Id,
    string Name,
    IReadOnlyList<GroupMember> Members,
    long PresenceVersion,
    bool CanDelete = false,
    GroupSettings? Settings = null,
    bool CanEdit = false);
