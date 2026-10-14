using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Security;

namespace ScreenCord.Services;

public interface IProfileService
{
    /// <summary>Consulta o perfil da conta conectada, renovando o token quando necessário.</summary>
    Task<UserProfile> GetProfileAsync(CancellationToken cancellationToken = default);
}

public sealed class ProfileService : IProfileService
{
    private readonly IDiscordApiClient _api;
    private readonly ISessionManager _session;

    public ProfileService(IDiscordApiClient api, ISessionManager session)
    {
        _api = api;
        _session = session;
    }

    public async Task<UserProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await _session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        DiscordUser user;
        try
        {
            user = await _api.GetCurrentUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordException ex) when (ex.Kind == DiscordErrorKind.Unauthorized)
        {
            // Token revogado antes do vencimento: tenta uma única renovação.
            accessToken = await _session.RefreshAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                user = await _api.GetCurrentUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
            }
            catch (DiscordException retry) when (retry.Kind == DiscordErrorKind.Unauthorized)
            {
                await _session.SignOutAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        return new UserProfile(
            user.Id!,
            user.Username!,
            user.GlobalName,
            user.Discriminator,
            AvatarUrlBuilder.Build(user),
            HasCustomAvatar: !string.IsNullOrEmpty(user.Avatar));
    }
}
