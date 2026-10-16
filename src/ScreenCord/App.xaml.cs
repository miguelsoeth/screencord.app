using System.Windows;
using System.Windows.Threading;
using ScreenCord.Infrastructure;
using ScreenCord.Mockup;
using ScreenCord.Services;
using ScreenCord.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScreenCord;

public partial class App : Application
{
    private IHost? _host;
    private TrayIcon? _tray;
    private bool _updating;
    private DateTimeOffset _lastOutdatedCheck = DateTimeOffset.MinValue;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        // A linha de comando volta a valer por último, acima do appsettings.Local.json (o screencord.ps1 escolhe a API assim).
        builder.Configuration.AddCommandLine(e.Args);
        builder.Services.AddScreenCord(builder.Configuration);
        // Na versão instalada não há console: o arquivo é o que dá para pedir a quem relata um problema.
        builder.Logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultFolder));
        // Ambiente de mockup (DOTNET_ENVIRONMENT=Mockup, escolhido no screencord.ps1): dados falsos, sem Discord e sem API.
        var mockup = builder.Environment.IsEnvironment("Mockup");
        if (mockup)
        {
            builder.Services.AddScreenCordMockup();
        }

        _host = builder.Build();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _host.Services.GetRequiredService<ILogger<App>>().LogInformation(
            "ScreenCord {Version} iniciado ({Environment}, {OS}).", AppVersion.Current, builder.Environment.EnvironmentName, Environment.OSVersion.VersionString);

        // Sempre na versão mais recente: se houver uma nova, instala e reabre antes de mostrar a janela.
        var updater = _host.Services.GetRequiredService<IAppUpdater>();
        if (!mockup && updater.IsEnabled)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Reaberto depois de uma instalação que falhou: não tenta a mesma versão de novo agora, senão entra em loop.
            var failed = AppVersion.TryParse(builder.Configuration["UpdateFailed"], out var failedVersion) ? failedVersion : null;
            if (await FindUpdateAsync(updater, TimeSpan.FromSeconds(5)) is { } update)
            {
                if (update.Version == failed)
                {
                    _host.Services.GetRequiredService<ILogger<App>>().LogWarning(
                        "A instalação da versão {Version} falhou; seguindo na {Current}.", update.Version.ToString(3), AppVersion.Current);
                }
                else if (await InstallAsync(updater, update))
                {
                    Shutdown();
                    return;
                }
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        // A API deixou de aceitar esta versão com o app aberto (ela subiu a versão mínima).
        _host.Services.GetRequiredService<CentralApiClient>().ClientOutdated +=
            (_, message) => Dispatcher.BeginInvoke(() => OnClientOutdated(updater, message));

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;

        // Fechar a janela deixa o app na bandeja; ele só sai pelo "Sair" do menu do ícone.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        mainWindow.HidesOnClose = true;
        _tray = new TrayIcon(mainWindow.BringToFront, ExitApp);

        mainWindow.Show();
    }

    /// <summary>Fecha o app de verdade (a janela principal, senão, só iria para a bandeja).</summary>
    private void ExitApp()
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.HidesOnClose = false;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }

    private async void OnClientOutdated(IAppUpdater updater, string message)
    {
        // Várias requisições recebem 426 ao mesmo tempo: uma tentativa por vez, e no máximo a cada 10 minutos.
        var now = DateTimeOffset.UtcNow;
        if (_updating || now - _lastOutdatedCheck < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _updating = true;
        _lastOutdatedCheck = now;
        try
        {
            if (updater.IsEnabled && await FindUpdateAsync(updater, TimeSpan.FromSeconds(20)) is { } update && await InstallAsync(updater, update))
            {
                ExitApp();
                return;
            }

            // Sem versão nova ainda (a release do app sai logo depois da API) ou sem acesso ao GitHub.
            MessageBox.Show(MainWindow, message, "ScreenCord", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _updating = false;
        }
    }

    private static async Task<AppUpdate?> FindUpdateAsync(IAppUpdater updater, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            return await updater.FindUpdateAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Baixa e agenda a instalação mostrando o progresso; false se falhar (o app segue na versão atual).</summary>
    private async Task<bool> InstallAsync(IAppUpdater updater, AppUpdate update)
    {
        var version = update.Version.ToString(3);
        var window = new UpdateWindow();
        window.SetStatus($"Baixando a versão {version}…", 0);
        window.Show();
        try
        {
            var progress = new Progress<double>(value => window.SetStatus($"Baixando a versão {version}… {value:P0}", value));
            await updater.DownloadAndScheduleInstallAsync(update, progress);
            window.SetStatus($"Instalando a versão {version}. O ScreenCord abre de novo em seguida.");
            return true;
        }
        catch (Exception ex)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogError(ex, "Falha ao atualizar para a versão {Version}.", version);
            window.Close();
            return false;
        }
    }

    // Última barreira: erros não tratados pelos ViewModels são registrados e mostrados sem derrubar o aplicativo.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Exceção não tratada na interface.");
        MessageBox.Show(
            ErrorMessages.For(e.Exception),
            "ScreenCord",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
