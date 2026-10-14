using System.Net;
using System.Net.Http;
using System.Text.Json;
using ScreenCord.Infrastructure;
using ScreenCord.Models;

namespace ScreenCord.Services;

/// <summary>Tratamento centralizado de envio, erros HTTP e leitura de JSON para os serviços do Discord.</summary>
public static class DiscordHttp
{
    public const string ClientName = "Discord";
    public static readonly Uri BaseAddress = new("https://discord.com/api/");
    public const string ApiVersionPrefix = "v10/";

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient sinaliza o próprio timeout com TaskCanceledException.
            throw new DiscordException(DiscordErrorKind.Timeout, "Tempo limite excedido ao contatar o Discord.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DiscordException(DiscordErrorKind.Network, "Falha de rede ao contatar o Discord.", ex);
        }
    }

    /// <summary>Converte uma resposta sem sucesso em <see cref="DiscordException"/>. O corpo nunca é incluído na mensagem.</summary>
    public static async Task<DiscordException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;

        if (status == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = RateLimitHandler.GetRetryAfter(response);
            if (retryAfter is null)
            {
                var body = await TryReadJsonAsync<RateLimitResponse>(response, cancellationToken).ConfigureAwait(false);
                if (body?.RetryAfter is > 0 and < 86_400)
                {
                    retryAfter = TimeSpan.FromSeconds(body.RetryAfter.Value);
                }
            }

            return new DiscordException(DiscordErrorKind.RateLimited, "Limite de requisições do Discord atingido.")
            {
                StatusCode = status,
                RetryAfter = retryAfter,
            };
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            return new DiscordException(DiscordErrorKind.Unauthorized, "O Discord recusou o token de acesso.") { StatusCode = status };
        }

        if ((int)status >= 500)
        {
            return new DiscordException(DiscordErrorKind.ServiceUnavailable, $"A API do Discord está indisponível (HTTP {(int)status}).") { StatusCode = status };
        }

        return new DiscordException(DiscordErrorKind.Unexpected, $"Resposta inesperada do Discord (HTTP {(int)status}).") { StatusCode = status };
    }

    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        return await TryReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false)
            ?? throw new DiscordException(DiscordErrorKind.InvalidResponse, "O Discord retornou um corpo que não é um JSON válido.")
            {
                StatusCode = response.StatusCode,
            };
    }

    public static async Task<T?> TryReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or System.IO.IOException)
        {
            return null;
        }
    }
}
