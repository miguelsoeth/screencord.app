using System.Net.Http;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;

namespace ScreenCord.Services;

/// <summary>
/// Plano da conta (gratuito ou Pro) e compra de períodos de Pro. Até o primeiro carregamento, e sempre que o
/// servidor não informar, vale o gratuito: a API valida tudo de novo, então o app nunca oferece mais do que ela aceita.
/// </summary>
public interface IPlanService
{
    PlanDto Current { get; }

    /// <summary>O plano mudou (carregado, pagamento confirmado ou estornado). Pode vir de qualquer thread.</summary>
    event EventHandler? Changed;

    Task<PlanDto> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre o pagamento de um período de Pro. Retorna a página de pagamento para abrir no navegador, ou null quando
    /// não há página (o Pro já foi ativado). O Pro chega depois por <see cref="Changed"/>.
    /// </summary>
    Task<Uri?> StartCheckoutAsync(string offerId, CancellationToken cancellationToken = default);

    /// <summary>Volta ao gratuito ao desconectar a conta.</summary>
    void Reset();
}

public sealed class CentralPlanService : IPlanService
{
    public static PlanDto Free { get; } = new(PlanTier.Free, null, PlanLimits.Free, []);

    private readonly CentralApiClient _api;
    private PlanDto _current = Free;

    public CentralPlanService(CentralApiClient api, IGroupsRealtime realtime)
    {
        _api = api;
        realtime.PlanChanged += (_, plan) => Set(plan);
    }

    public PlanDto Current => _current;

    public event EventHandler? Changed;

    public async Task<PlanDto> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var plan = await _api.SendAsync<PlanDto>(HttpMethod.Get, "me/plan", null, cancellationToken).ConfigureAwait(false);
        Set(plan);
        return _current;
    }

    public async Task<Uri?> StartCheckoutAsync(string offerId, CancellationToken cancellationToken = default)
    {
        var checkout = await _api.SendAsync<ProCheckoutDto>(HttpMethod.Post, "billing/checkout", new ProCheckoutCreate(offerId), cancellationToken)
            .ConfigureAwait(false);
        return IsCheckoutUrl(checkout.Url, out var url)
            ? url
            : throw CentralApiClient.Error(DiscordErrorKind.InvalidResponse, "A API central retornou uma página de pagamento inválida.");
    }

    public void Reset() => Set(Free);

    /// <summary>A página de pagamento abre no navegador: só HTTPS do AbacatePay.</summary>
    public static bool IsCheckoutUrl(string? value, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !(parsed.Host == "abacatepay.com" || parsed.Host.EndsWith(".abacatepay.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>Os dados vêm do servidor: sem limites válidos, fica o gratuito.</summary>
    public static PlanDto Sanitize(PlanDto? plan)
    {
        if (plan?.Limits is not { MaxHeight: > 0, MaxHeightAt60Fps: > 0, MaxBitrateKbps: > 0, MaxMembers: > 0, MaxCreatedGroups: > 0 })
        {
            return Free;
        }

        var offers = (plan.Offers ?? [])
            .Where(o => o is { Id.Length: > 0, Name.Length: > 0, Days: > 0, PriceCents: > 0 })
            .ToList();
        return plan with { Offers = offers };
    }

    private void Set(PlanDto? plan)
    {
        _current = Sanitize(plan);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
