using System.Net.Http;
using System.Windows;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Localization;
using ScreenCord.Services;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Views;

/// <summary>
/// A atualização com as janelas: ao abrir, quando a API recusa esta versão (426) e no "Procurar atualizações" do
/// Sobre. É opcional enquanto a API aceitar esta versão (<see cref="UpdatePolicy"/>); quando não aceita mais, é
/// atualizar ou sair. Quando o app precisa fechar (para instalar, ou porque a pessoa preferiu sair), avisa por
/// <see cref="ExitRequested"/>.
/// </summary>
public sealed class UpdateFlow : IUpdateFlow
{
    private readonly IAppUpdater _updater;
    private readonly IApiHealthCheck _health;
    private readonly ILogger<UpdateFlow> _logger;
    private Version? _failed;
    private bool _busy;
    private DateTimeOffset _lastOutdatedCheck = DateTimeOffset.MinValue;

    public UpdateFlow(IAppUpdater updater, IApiHealthCheck health, ILogger<UpdateFlow> logger)
    {
        _updater = updater;
        _health = health;
        _logger = logger;
    }

    /// <summary>O app precisa fechar: a versão nova vai ser instalada ou a pessoa preferiu sair.</summary>
    public event EventHandler? ExitRequested;

    public bool IsEnabled => _updater.IsEnabled;

    /// <summary>
    /// Ao abrir, antes da janela principal: consulta a versão nova e a mínima da API ao mesmo tempo (até 5 s; sem
    /// rede segue normalmente). True se o app vai fechar.
    /// </summary>
    /// <param name="failed">A versão cuja instalação falhou (o app foi reaberto com <see cref="AppUpdater.FailedArgument"/>).</param>
    public async Task<bool> RunAtStartupAsync(Version? failed)
    {
        _failed = failed;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var updateTask = FindUpdateAsync(timeout.Token);
        var requiredTask = IsRequiredAsync(timeout.Token);
        var update = await updateTask;
        var required = await requiredTask;

        var action = UpdatePolicy.Decide(update, required, UpdatePreference.AutoUpdate, UpdatePreference.SkippedVersion, failed);
        if (update is not null && update.Version == failed)
        {
            _logger.LogWarning("A instalação da versão {Version} falhou; seguindo na {Current}.", update.Version.ToString(3), AppVersion.Current);
        }

        return action switch
        {
            UpdateAction.Install => await InstallOrExitAsync(update!, required),
            UpdateAction.Ask => await AskAsync(update!, required: false) == UpdateCheckResult.Exiting,
            UpdateAction.Require => await AskAsync(update!, required: true) == UpdateCheckResult.Exiting,
            UpdateAction.RequiredButFailed => ExitAfterFailure(update!),
            _ => false,
        };
    }

    /// <summary>
    /// A API deixou de aceitar esta versão com o app aberto: atualizar ou sair. Sem versão nova publicada ainda
    /// (a release do app sai logo depois da API), mostra o texto da API e o app continua (as salas locais funcionam).
    /// </summary>
    public async Task OnClientOutdatedAsync(string message, Window? owner)
    {
        // Várias requisições recebem 426 ao mesmo tempo: uma tentativa por vez, e no máximo a cada 10 minutos.
        var now = DateTimeOffset.UtcNow;
        if (_busy || now - _lastOutdatedCheck < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _busy = true;
        _lastOutdatedCheck = now;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var update = await FindUpdateAsync(timeout.Token);
            switch (UpdatePolicy.Decide(update, required: true, UpdatePreference.AutoUpdate, skipped: null, _failed))
            {
                case UpdateAction.Install:
                    await InstallOrExitAsync(update!, required: true);
                    return;
                case UpdateAction.Require:
                    await AskAsync(update!, required: true);
                    return;
                case UpdateAction.RequiredButFailed:
                    ExitAfterFailure(update!);
                    return;
                default:
                    ShowMessage(message, owner);
                    return;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        if (!IsEnabled || _busy)
        {
            return UpdateCheckResult.NoUpdate;
        }

        _busy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var updateTask = FindUpdateAsync(timeout.Token);
            var requiredTask = IsRequiredAsync(timeout.Token);
            var update = await updateTask;
            var required = await requiredTask;
            return update is null ? UpdateCheckResult.NoUpdate : await AskAsync(update, required);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<UpdateCheckResult> AskAsync(AppUpdate update, bool required)
    {
        var version = update.Version.ToString(3);
        var text = required
            ? string.Format(Loc.Culture, Strings.UpdateRequiredFormat, version, AppVersion.Current)
            : string.Format(Loc.Culture, Strings.UpdateAvailableFormat, version, AppVersion.Current);

        switch (UpdateWindow.Ask(text, required))
        {
            case UpdateChoice.Install:
                return await InstallOrExitAsync(update, required) ? UpdateCheckResult.Exiting : UpdateCheckResult.Declined;
            case UpdateChoice.Skip:
                _logger.LogInformation("Versão {Version} pulada.", version);
                UpdatePreference.Skip(update.Version);
                return UpdateCheckResult.Declined;
            case UpdateChoice.Exit:
                _logger.LogInformation("A versão {Version} é obrigatória e a atualização foi recusada; fechando.", version);
                RequestExit();
                return UpdateCheckResult.Exiting;
            default:
                return UpdateCheckResult.Declined;
        }
    }

    /// <summary>Instala e fecha o app. Se falhar e a versão for obrigatória, avisa e fecha do mesmo jeito.</summary>
    private async Task<bool> InstallOrExitAsync(AppUpdate update, bool required)
    {
        if (await InstallAsync(update))
        {
            RequestExit();
            return true;
        }

        return required && ExitAfterFailure(update);
    }

    /// <summary>A versão nova é obrigatória e não instalou: aponta a página de download e fecha.</summary>
    private bool ExitAfterFailure(AppUpdate update)
    {
        ShowMessage(string.Format(Loc.Culture, Strings.UpdateRequiredFailedFormat, update.Version.ToString(3), ClientVersionContract.DownloadPage), null);
        RequestExit();
        return true;
    }

    /// <summary>Baixa e agenda a instalação mostrando o progresso; false se falhar (o app segue na versão atual).</summary>
    private async Task<bool> InstallAsync(AppUpdate update)
    {
        var version = update.Version.ToString(3);
        var window = new UpdateWindow();
        window.SetStatus(string.Format(Loc.Culture, Strings.DownloadingVersionFormat, version), 0);
        window.Show();
        try
        {
            var progress = new Progress<double>(value => window.SetStatus(string.Format(Loc.Culture, Strings.DownloadingVersionFormat, version) + " " + value.ToString("P0", Loc.Culture), value));
            await _updater.DownloadAndScheduleInstallAsync(update, progress);
            window.SetStatus(string.Format(Loc.Culture, Strings.InstallingVersionFormat, version));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao atualizar para a versão {Version}.", version);
            window.Close();
            return false;
        }
    }

    private async Task<AppUpdate?> FindUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _updater.FindUpdateAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Se a API não aceita mais esta versão (o <c>/health</c> não passa pela trava de versão e diz a mínima).</summary>
    private async Task<bool> IsRequiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            var health = await _health.CheckAsync(cancellationToken);
            return UpdatePolicy.IsRequired(health.MinClientVersion, AppVersion.Current);
        }
        catch (Exception ex) when (ex is DiscordException or OperationCanceledException or HttpRequestException)
        {
            // Sem resposta da API: trata como opcional; se ela recusar depois, o 426 cuida disso.
            return false;
        }
    }

    private void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private static void ShowMessage(string message, Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            MessageBox.Show(owner, message, "ScreenCord", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(message, "ScreenCord", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
