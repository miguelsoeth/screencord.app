namespace ScreenCord.Models;

/// <summary>Perfil pronto para exibição, derivado de <see cref="DiscordUser"/> (ou o perfil local, sem conta).</summary>
/// <param name="AvatarHash">Hash do avatar no Discord, para as salas locais mostrarem a foto aos outros.</param>
public sealed record UserProfile(
    string Id,
    string Username,
    string? GlobalName,
    string? Discriminator,
    string AvatarUrl,
    bool HasCustomAvatar,
    string? AvatarHash = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName;

    /// <summary>Nome de usuário; contas legadas mantêm o sufixo <c>#0000</c>.</summary>
    public string Handle => Discriminator is null or "0" or "0000" ? $"@{Username}" : $"{Username}#{Discriminator}";

    public string Initial => DisplayName[..1].ToUpperInvariant();
}
