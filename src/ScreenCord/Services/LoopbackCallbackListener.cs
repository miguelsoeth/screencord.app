using ScreenCord.Localization;
using System.Net;
using System.Text;
using ScreenCord.Infrastructure;

namespace ScreenCord.Services;

/// <summary>Parâmetros recebidos no redirect do Discord. Não deve ser registrado em log.</summary>
public sealed record AuthorizationCallback(string? Code, string? State, string? Error)
{
    public override string ToString() => $"AuthorizationCallback {{ HasCode = {Code is not null}, Error = {Error} }}";
}

public interface IAuthorizationCallbackListener
{
    /// <summary>
    /// Escuta o redirect URI local e retorna o primeiro callback de autorização recebido.
    /// <paramref name="onListening"/> é chamado quando o servidor já aceita conexões (momento de abrir o navegador).
    /// </summary>
    Task<AuthorizationCallback> ListenAsync(Uri redirectUri, Action onListening, CancellationToken cancellationToken);
}

/// <summary>
/// Servidor HTTP temporário restrito à interface de loopback (127.0.0.1), ativo apenas durante o login.
/// </summary>
public sealed class LoopbackCallbackListener : IAuthorizationCallbackListener
{
    public async Task<AuthorizationCallback> ListenAsync(Uri redirectUri, Action onListening, CancellationToken cancellationToken)
    {
        if (redirectUri.Scheme != Uri.UriSchemeHttp || !redirectUri.IsLoopback)
        {
            throw new DiscordException(DiscordErrorKind.Configuration, "O redirect URI precisa ser um endereço http de loopback (127.0.0.1).");
        }

        using var listener = new HttpListener();
        // O prefixo usa o IP literal: o servidor não fica acessível a partir de outras máquinas.
        listener.Prefixes.Add($"http://{redirectUri.Host}:{redirectUri.Port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new DiscordException(
                DiscordErrorKind.Configuration,
                $"Não foi possível abrir a porta local {redirectUri.Port} para receber a autorização. Feche o programa que a está usando ou altere \"Discord:RedirectPort\" (e o redirect URI no portal).",
                ex);
        }

        using var registration = cancellationToken.Register(static state =>
        {
            try
            {
                ((HttpListener)state!).Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }, listener);

        onListening();

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new DiscordException(DiscordErrorKind.Unexpected, "O servidor local de autorização foi interrompido.", ex);
            }

            var request = context.Request;
            if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != redirectUri.AbsolutePath)
            {
                Respond(context.Response, HttpStatusCode.NotFound, Strings.BrowserNotFoundTitle, Strings.BrowserNotFound);
                continue;
            }

            var code = request.QueryString["code"];
            var error = request.QueryString["error"];
            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error))
            {
                Respond(context.Response, HttpStatusCode.BadRequest, Strings.BrowserBadRequestTitle, Strings.BrowserBadRequest);
                continue;
            }

            if (string.IsNullOrEmpty(error))
            {
                Respond(context.Response, HttpStatusCode.OK, Strings.BrowserDoneTitle, Strings.BrowserDone);
            }
            else
            {
                Respond(context.Response, HttpStatusCode.OK, Strings.BrowserNotCompletedTitle, Strings.BrowserNotCompleted);
            }

            return new AuthorizationCallback(
                string.IsNullOrEmpty(code) ? null : code,
                request.QueryString["state"],
                string.IsNullOrEmpty(error) ? null : error);
        }
    }

    // A página é estática: nenhum parâmetro da requisição é refletido no HTML.
    private static void Respond(HttpListenerResponse response, HttpStatusCode status, string title, string message)
    {
        var html = $$"""
            <!DOCTYPE html>
            <html lang="{{Loc.Culture.Name}}">
            <head>
            <meta charset="utf-8">
            <title>ScreenCord</title>
            <style>
            body { background:#1e1f22; color:#f2f3f5; font-family:"Segoe UI",sans-serif; display:flex; align-items:center; justify-content:center; height:100vh; margin:0; }
            main { background:#2b2d31; padding:32px 40px; border-radius:12px; text-align:center; max-width:420px; }
            p { color:#b5bac1; }
            </style>
            </head>
            <body><main><h1>{{title}}</h1><p>{{message}}</p></main></body>
            </html>
            """;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            response.StatusCode = (int)status;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // O navegador fechou a conexão; o callback já foi lido, então não há o que fazer.
        }
    }
}
