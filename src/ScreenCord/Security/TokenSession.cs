namespace ScreenCord.Security;

/// <summary>Tokens da sessão atual. Nunca deve ser registrado em log nem gravado sem proteção.</summary>
public sealed record TokenSession(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc, string? Scope)
{
    /// <summary>Margem para renovar antes do vencimento real.</summary>
    public static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc - ExpirySkew;

    // Evita vazamento acidental via ToString() gerado pelo record (logs, depurador, interpolação).
    public override string ToString() => $"TokenSession {{ ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}
