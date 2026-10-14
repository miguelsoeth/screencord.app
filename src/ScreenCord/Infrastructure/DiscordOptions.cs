namespace ScreenCord.Infrastructure;

/// <summary>Configuração do aplicativo registrado no Discord Developer Portal (seção "Discord" do appsettings).</summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>Client ID (Application ID). É um identificador público, não um segredo.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Porta fixa do callback local. Precisa coincidir com o redirect URI cadastrado no portal.</summary>
    public int RedirectPort { get; set; } = 47821;

    public string RedirectPath { get; set; } = "/callback";

    /// <summary>Escopos separados por espaço. Somente <c>identify</c> é necessário para o perfil.</summary>
    public string Scopes { get; set; } = "identify";

    public int LoginTimeoutSeconds { get; set; } = 300;

    public string RedirectUri => $"http://127.0.0.1:{RedirectPort}{RedirectPath}";

    /// <summary>Valida a configuração e lança <see cref="DiscordException"/> com uma mensagem acionável.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || !ClientId.All(char.IsAsciiDigit))
        {
            throw new DiscordException(
                DiscordErrorKind.Configuration,
                "O Client ID do Discord não está configurado. Defina \"Discord:ClientId\" no appsettings.json (veja o README).");
        }

        if (RedirectPort is < 1024 or > 65535)
        {
            throw new DiscordException(
                DiscordErrorKind.Configuration,
                "\"Discord:RedirectPort\" deve estar entre 1024 e 65535.");
        }

        if (string.IsNullOrWhiteSpace(RedirectPath) || !RedirectPath.StartsWith('/'))
        {
            throw new DiscordException(
                DiscordErrorKind.Configuration,
                "\"Discord:RedirectPath\" deve começar com \"/\".");
        }

        if (string.IsNullOrWhiteSpace(Scopes))
        {
            throw new DiscordException(
                DiscordErrorKind.Configuration,
                "\"Discord:Scopes\" não pode ser vazio.");
        }
    }
}
