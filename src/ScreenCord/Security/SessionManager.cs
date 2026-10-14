using ScreenCord.Infrastructure;
using ScreenCord.Services;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Security;

public interface ISessionManager
{
    /// <summary>Indica se há uma sessão armazenada que pode ser reutilizada.</summary>
    Task<bool> HasSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Executa o login no navegador e guarda a nova sessão.</summary>
    Task SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Retorna um access token válido, renovando-o se estiver vencido.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Força a renovação (usado quando a API responde 401 a um token aparentemente válido).</summary>
    Task<string> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Revoga os tokens no Discord (melhor esforço) e apaga os dados locais.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public sealed class SessionManager : ISessionManager
{
    private readonly IDiscordOAuthService _oauth;
    private readonly ITokenStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TokenSession? _session;
    private bool _loaded;

    public SessionManager(IDiscordOAuthService oauth, ITokenStore store, TimeProvider time, ILogger<SessionManager> logger)
    {
        _oauth = oauth;
        _store = store;
        _time = time;
        _logger = logger;
    }

    public async Task<bool> HasSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        var session = await _oauth.AuthorizeAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StoreAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Sessão iniciada; token válido até {ExpiresAtUtc:O}.", session.ExpiresAtUtc);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw NoSession();
            if (session.IsExpired(_time.GetUtcNow()))
            {
                session = await RefreshCoreAsync(session, cancellationToken).ConfigureAwait(false);
            }

            return session.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? throw NoSession();
            return (await RefreshCoreAsync(session, cancellationToken).ConfigureAwait(false)).AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await LoadAsync(cancellationToken).ConfigureAwait(false);

            // Os dados locais são apagados primeiro: a desconexão vale mesmo offline.
            await ClearAsync().ConfigureAwait(false);

            if (session is not null)
            {
                try
                {
                    // Revogar o refresh token invalida também o access token associado.
                    await _oauth.RevokeAsync(session.RefreshToken ?? session.AccessToken, cancellationToken).ConfigureAwait(false);
                }
                catch (DiscordException ex)
                {
                    _logger.LogWarning("Não foi possível revogar o token no Discord ({Kind}). Os dados locais foram removidos.", ex.Kind);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TokenSession> RefreshCoreAsync(TokenSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(session.RefreshToken))
        {
            await ClearAsync().ConfigureAwait(false);
            throw NoSession();
        }

        try
        {
            var refreshed = await _oauth.RefreshAsync(session.RefreshToken, cancellationToken).ConfigureAwait(false);
            await StoreAsync(refreshed, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Token renovado; válido até {ExpiresAtUtc:O}.", refreshed.ExpiresAtUtc);
            return refreshed;
        }
        catch (DiscordException ex) when (ex.Kind is DiscordErrorKind.OAuthRejected or DiscordErrorKind.Unauthorized)
        {
            // Refresh token revogado/expirado: a sessão não é mais reutilizável. Falhas de rede não chegam aqui.
            _logger.LogInformation("O Discord recusou a renovação do token ({ErrorCode}); sessão encerrada.", ex.ErrorCode);
            await ClearAsync().ConfigureAwait(false);
            throw new DiscordException(DiscordErrorKind.Unauthorized, "A sessão expirou e não pôde ser renovada.", ex);
        }
    }

    private async Task<TokenSession?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            _session = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            _loaded = true;
        }

        return _session;
    }

    private async Task StoreAsync(TokenSession session, CancellationToken cancellationToken)
    {
        _session = session;
        _loaded = true;
        await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearAsync()
    {
        _session = null;
        _loaded = true;
        await _store.ClearAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static DiscordException NoSession() =>
        new(DiscordErrorKind.Unauthorized, "Não há sessão ativa.");
}
