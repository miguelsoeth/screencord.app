using System.Net;

namespace ScreenCord.Infrastructure;

public enum DiscordErrorKind
{
    /// <summary>Configuração local inválida (Client ID ausente, porta ocupada etc.).</summary>
    Configuration,
    /// <summary>Sem conexão ou falha de rede/DNS/TLS.</summary>
    Network,
    /// <summary>A operação excedeu o tempo limite.</summary>
    Timeout,
    /// <summary>O usuário negou a autorização na tela do Discord.</summary>
    AuthorizationDenied,
    /// <summary>O parâmetro <c>state</c> do callback não confere (possível CSRF).</summary>
    InvalidState,
    /// <summary>O endpoint OAuth2 rejeitou a requisição (invalid_grant, invalid_client...).</summary>
    OAuthRejected,
    /// <summary>Token ausente, inválido ou expirado (HTTP 401).</summary>
    Unauthorized,
    /// <summary>Limite de requisições atingido (HTTP 429).</summary>
    RateLimited,
    /// <summary>API do Discord indisponível (HTTP 5xx).</summary>
    ServiceUnavailable,
    /// <summary>Resposta fora do formato esperado.</summary>
    InvalidResponse,
    /// <summary>A API central recusou a operação; <see cref="Exception.Message"/> é o texto para o usuário.</summary>
    Rejected,
    /// <summary>A API central não aceita mais esta versão do aplicativo (HTTP 426); o aplicativo se atualiza.</summary>
    ClientOutdated,
    Unexpected,
}

/// <summary>Serviço remoto que originou o erro; define o texto mostrado ao usuário.</summary>
public enum ErrorSource
{
    Discord,
    CentralApi,
}

/// <summary>
/// Erro único da camada de serviços. As mensagens nunca contêm tokens, códigos de autorização ou outros segredos.
/// </summary>
public sealed class DiscordException : Exception
{
    public DiscordException(DiscordErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public DiscordErrorKind Kind { get; }

    public new ErrorSource Source { get; init; } = ErrorSource.Discord;

    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>Código de erro OAuth2 (ex.: <c>invalid_grant</c>), quando houver.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Tempo de espera indicado pelo Discord em respostas 429.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Trace W3C da requisição à API central que falhou; a API registra o mesmo id no painel.</summary>
    public string? TraceId { get; set; }

    public DiscordException WithTrace(string? traceId)
    {
        TraceId ??= traceId;
        return this;
    }
}
