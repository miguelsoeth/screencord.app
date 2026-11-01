using System.Net.Http;
using ScreenCord.Contracts;

namespace ScreenCord.Services;

/// <summary>Qualidade máxima do compartilhamento pela API (<c>GET /limits</c>); até carregar, a padrão dos Contracts.</summary>
public interface IServerLimits
{
    StreamLimits Relay { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Passa a valer o que o servidor avisou pelo hub.</summary>
    void Apply(StreamLimits? relay);
}

public sealed class CentralServerLimits(CentralApiClient api) : IServerLimits
{
    public StreamLimits Relay { get; private set; } = StreamLimits.Relay;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var limits = await api.SendAsync<ServerLimitsDto>(HttpMethod.Get, "limits", null, cancellationToken).ConfigureAwait(false);
            Apply(limits.Relay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Sem resposta, vale o padrão; a API recusa de qualquer jeito o que passar do limite dela.
        }
    }

    public void Apply(StreamLimits? relay)
    {
        // Os dados vêm do servidor: limites que o app não sabe fazer são ignorados.
        if (relay is { IsSane: true })
        {
            Relay = relay;
        }
    }
}
