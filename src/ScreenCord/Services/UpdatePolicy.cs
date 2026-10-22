using ScreenCord.Infrastructure;

namespace ScreenCord.Services;

/// <summary>O que fazer com uma versão nova encontrada ao abrir o app.</summary>
public enum UpdateAction
{
    /// <summary>Segue nesta versão sem perguntar.</summary>
    None,
    /// <summary>Instala sem perguntar (atualização automática ligada).</summary>
    Install,
    /// <summary>Pergunta: atualizar agora, agora não ou pular esta versão.</summary>
    Ask,
    /// <summary>A API não aceita mais esta versão: atualizar ou sair.</summary>
    Require,
    /// <summary>A API não aceita mais esta versão e a instalação da nova já falhou nesta abertura: avisa e sai.</summary>
    RequiredButFailed,
}

/// <summary>
/// Regras da atualização opcional. A atualização só é obrigatória quando a API não aceita mais esta versão
/// (o <c>minClientVersion</c> do <c>GET /health</c>, a mesma regra que responde 426 nas outras rotas).
/// </summary>
public static class UpdatePolicy
{
    /// <summary>
    /// Se a API exige uma versão maior que esta. Sem resposta da API, ou com versão ilegível, não é obrigatória:
    /// se a API recusar depois, o 426 cuida disso. Build de desenvolvimento nunca é recusado.
    /// </summary>
    public static bool IsRequired(string? minClientVersion, string currentVersion) =>
        AppVersion.TryParse(minClientVersion, out var minimum) &&
        AppVersion.TryParse(currentVersion, out var current) &&
        current != new Version(0, 0, 0) && current < minimum;

    /// <param name="update">A versão nova publicada; null se não há (ou não deu para consultar).</param>
    /// <param name="required">A API não aceita mais esta versão.</param>
    /// <param name="autoUpdate">A preferência "Atualizar automaticamente".</param>
    /// <param name="skipped">A versão que a pessoa mandou pular.</param>
    /// <param name="failed">A versão cuja instalação falhou antes desta abertura (<see cref="AppUpdater.FailedArgument"/>).</param>
    public static UpdateAction Decide(AppUpdate? update, bool required, bool autoUpdate, Version? skipped, Version? failed)
    {
        if (update is null)
        {
            return UpdateAction.None;
        }

        // Tentar de novo a versão que acabou de falhar faria o app reabrir em loop.
        if (update.Version == failed)
        {
            return required ? UpdateAction.RequiredButFailed : UpdateAction.None;
        }

        if (autoUpdate)
        {
            return UpdateAction.Install;
        }

        if (required)
        {
            return UpdateAction.Require;
        }

        return skipped is not null && update.Version <= skipped ? UpdateAction.None : UpdateAction.Ask;
    }
}

/// <summary>Resultado do "Procurar atualizações" da tela Sobre.</summary>
public enum UpdateCheckResult
{
    /// <summary>Nenhuma versão nova (ou não deu para consultar).</summary>
    NoUpdate,
    /// <summary>Havia uma versão nova e a pessoa não quis instalar agora.</summary>
    Declined,
    /// <summary>O app está fechando: para instalar a versão nova, ou porque ela é obrigatória e a pessoa preferiu sair.</summary>
    Exiting,
}

/// <summary>A atualização com as perguntas na tela (implementada na camada de interface).</summary>
public interface IUpdateFlow
{
    /// <summary>False em builds de desenvolvimento e no mockup.</summary>
    bool IsEnabled { get; }

    /// <summary>Procura uma versão nova e pergunta se instala, ignorando a versão pulada.</summary>
    Task<UpdateCheckResult> CheckNowAsync();
}
