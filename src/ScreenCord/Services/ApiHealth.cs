using ScreenCord.Infrastructure;

namespace ScreenCord.Services;

/// <summary>Resposta do <c>GET /health</c> da API (sem sessão). Só o app lê, por isso não está nos Contracts.</summary>
public sealed record ApiHealth(string? Status, string? Version, string? MinClientVersion);

/// <summary>Disponibilidade da API central, para a tela "Sobre".</summary>
public interface IApiHealthCheck
{
    /// <summary>Endereço da API; nulo se a configuração for inválida.</summary>
    Uri? BaseUri { get; }

    Task<ApiHealth> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class CentralApiHealthCheck(CentralApiClient api) : IApiHealthCheck
{
    public Uri? BaseUri
    {
        get
        {
            try
            {
                return api.BaseUri;
            }
            catch (DiscordException)
            {
                return null;
            }
        }
    }

    public Task<ApiHealth> CheckAsync(CancellationToken cancellationToken = default) => api.GetHealthAsync(cancellationToken);
}
