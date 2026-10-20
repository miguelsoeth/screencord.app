namespace ScreenCord.Infrastructure;

/// <summary>Ponto único que traduz exceções em mensagens exibidas ao usuário.</summary>
public static class ErrorMessages
{
    public static string For(Exception exception) => exception switch
    {
        DiscordException discord => For(discord),
        Local.LocalNetworkException local => local.Message,
        OperationCanceledException => "Operação cancelada.",
        _ => "Ocorreu um erro inesperado. Tente novamente.",
    };

    private static string For(DiscordException exception) =>
        exception.Source == ErrorSource.CentralApi ? ForCentralApi(exception) : ForDiscord(exception);

    private static string ForCentralApi(DiscordException exception) => exception.Kind switch
    {
        DiscordErrorKind.Configuration or DiscordErrorKind.Rejected or DiscordErrorKind.ClientOutdated => exception.Message,
        DiscordErrorKind.Network => "Não foi possível conectar ao servidor do ScreenCord. Verifique se a API está em execução e a sua conexão.",
        DiscordErrorKind.Timeout => "O servidor do ScreenCord demorou demais para responder. Tente novamente.",
        DiscordErrorKind.Unauthorized => "O servidor do ScreenCord não aceitou o seu login. Desconecte e conecte sua conta novamente.",
        DiscordErrorKind.RateLimited => "Muitas requisições ao servidor do ScreenCord. Aguarde um instante e tente novamente.",
        DiscordErrorKind.ServiceUnavailable => "O servidor do ScreenCord está indisponível no momento. Tente novamente mais tarde.",
        _ => "O servidor do ScreenCord retornou uma resposta inesperada. Tente novamente.",
    };

    private static string ForDiscord(DiscordException exception) => exception.Kind switch
    {
        DiscordErrorKind.Configuration => exception.Message,
        DiscordErrorKind.Network => "Não foi possível conectar ao Discord. Verifique sua conexão com a internet e tente novamente.",
        DiscordErrorKind.Timeout => "O Discord demorou demais para responder. Tente novamente.",
        DiscordErrorKind.AuthorizationDenied => "A autorização foi negada no Discord. Nenhum dado foi acessado.",
        DiscordErrorKind.InvalidState => "A resposta de autorização não pôde ser validada e foi descartada por segurança. Tente conectar novamente.",
        DiscordErrorKind.OAuthRejected => exception.ErrorCode == "invalid_client"
            ? "O Discord rejeitou o aplicativo (invalid_client). Verifique o Client ID e se a opção \"Public Client\" está ativada no Developer Portal."
            : $"O Discord rejeitou a autenticação{FormatCode(exception.ErrorCode)}. Tente conectar novamente.",
        DiscordErrorKind.Unauthorized => "Sua sessão expirou ou foi revogada. Conecte sua conta novamente.",
        DiscordErrorKind.RateLimited => exception.RetryAfter is { } retry
            ? $"Muitas requisições. Aguarde {Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds))} s e tente novamente."
            : "Muitas requisições. Aguarde um instante e tente novamente.",
        DiscordErrorKind.ServiceUnavailable => "A API do Discord está indisponível no momento. Tente novamente mais tarde.",
        DiscordErrorKind.InvalidResponse => "O Discord retornou uma resposta inesperada. Tente novamente.",
        _ => "Ocorreu um erro inesperado ao falar com o Discord. Tente novamente.",
    };

    private static string FormatCode(string? code) => string.IsNullOrEmpty(code) ? "" : $" ({code})";
}
