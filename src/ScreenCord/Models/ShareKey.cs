using ScreenCord.Contracts;

namespace ScreenCord.Models;

/// <summary>Uma transmissão da sala: a tela ou a câmera de uma pessoa (cada pessoa tem no máximo uma de cada).</summary>
public readonly record struct ShareKey(Guid UserId, ShareKind Kind)
{
    public static ShareKey Of(ScreenShareDto share) => new(share.UserId, share.Kind);
}
