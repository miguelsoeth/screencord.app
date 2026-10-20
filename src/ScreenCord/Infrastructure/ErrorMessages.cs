using ScreenCord.Localization;

namespace ScreenCord.Infrastructure;

/// <summary>Ponto único que traduz exceções em mensagens exibidas ao usuário.</summary>
public static class ErrorMessages
{
    public static string For(Exception exception) => exception switch
    {
        DiscordException discord => For(discord),
        Local.LocalNetworkException local => local.Message,
        OperationCanceledException => Strings.OperationCanceled,
        _ => Strings.UnexpectedError,
    };

    private static string For(DiscordException exception) =>
        exception.Source == ErrorSource.CentralApi ? ForCentralApi(exception) : ForDiscord(exception);

    private static string ForCentralApi(DiscordException exception) => exception.Kind switch
    {
        DiscordErrorKind.Configuration or DiscordErrorKind.Rejected or DiscordErrorKind.ClientOutdated => exception.Message,
        DiscordErrorKind.Network => Strings.ApiNetwork,
        DiscordErrorKind.Timeout => Strings.ApiTimeout,
        DiscordErrorKind.Unauthorized => Strings.ApiUnauthorized,
        DiscordErrorKind.RateLimited => Strings.ApiRateLimited,
        DiscordErrorKind.ServiceUnavailable => Strings.ApiUnavailable,
        _ => Strings.ApiUnexpected,
    };

    private static string ForDiscord(DiscordException exception) => exception.Kind switch
    {
        DiscordErrorKind.Configuration => exception.Message,
        DiscordErrorKind.Network => Strings.DiscordNetwork,
        DiscordErrorKind.Timeout => Strings.DiscordTimeout,
        DiscordErrorKind.AuthorizationDenied => Strings.DiscordDenied,
        DiscordErrorKind.InvalidState => Strings.DiscordInvalidState,
        DiscordErrorKind.OAuthRejected => exception.ErrorCode == "invalid_client"
            ? Strings.DiscordInvalidClient
            : string.Format(Loc.Culture, Strings.DiscordRejectedFormat, FormatCode(exception.ErrorCode)),
        DiscordErrorKind.Unauthorized => Strings.DiscordUnauthorized,
        DiscordErrorKind.RateLimited => exception.RetryAfter is { } retry
            ? string.Format(Loc.Culture, Strings.DiscordRateLimitedFormat, Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)))
            : Strings.DiscordRateLimited,
        DiscordErrorKind.ServiceUnavailable => Strings.DiscordUnavailable,
        DiscordErrorKind.InvalidResponse => Strings.DiscordInvalidResponse,
        _ => Strings.DiscordUnexpected,
    };

    private static string FormatCode(string? code) => string.IsNullOrEmpty(code) ? "" : $" ({code})";
}
