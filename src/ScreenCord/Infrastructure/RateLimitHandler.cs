using System.Net;
using System.Net.Http;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Respeita respostas HTTP 429: aguarda o tempo indicado em <c>Retry-After</c> e repete a requisição uma vez.
/// Esperas longas não bloqueiam a interface: a resposta 429 segue adiante e vira um erro tratado.
/// </summary>
public sealed class RateLimitHandler : DelegatingHandler
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 1;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RateLimitHandler()
        : this(Task.Delay)
    {
    }

    public RateLimitHandler(Func<TimeSpan, CancellationToken, Task> delay)
    {
        _delay = delay;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRetries)
            {
                return response;
            }

            var wait = GetRetryAfter(response);
            if (wait is null || wait > MaxWait)
            {
                return response;
            }

            response.Dispose();
            await _delay(wait.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    public static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
    }
}
