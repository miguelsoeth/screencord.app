namespace ScreenCord.Infrastructure;

/// <summary>Endereço da API central do ScreenCord (seção "Api" do appsettings).</summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Valida e retorna o endereço base terminado em "/". HTTP sem TLS só é aceito para loopback
    /// (desenvolvimento), porque o token do Discord trafega até a API no login.
    /// </summary>
    public Uri GetBaseUri()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new DiscordException(
                DiscordErrorKind.Configuration,
                "O endereço da API do ScreenCord não está configurado corretamente. Defina \"Api:BaseUrl\" com uma URL https (http só é aceito para localhost).")
            {
                Source = ErrorSource.CentralApi,
            };
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
