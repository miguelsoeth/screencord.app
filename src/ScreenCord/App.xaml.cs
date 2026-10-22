using ScreenCord.Localization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ScreenCord.Infrastructure;
using ScreenCord.Local;
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
    private SingleInstance? _instance;
    private ErrorReporter? _reporter;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Iniciado elevado (pelo instalador ou pelo "Liberar no firewall") só para criar a regra do firewall.
        if (e.Args.Contains(FirewallRule.CommandLineFlag) && Environment.ProcessPath is { } exe)
        {
            Shutdown(FirewallRule.Apply(exe));
            return;
        }

        // Iniciado pelo instalador ao desinstalar, para apagar a regra do firewall (só pede permissão se ela existir).
        if (e.Args.Contains(FirewallRule.RemoveFlag) && Environment.ProcessPath is { } installedExe)
        {
            Shutdown(FirewallRule.Remove(installedExe, e.Args.Contains(FirewallRule.ElevatedFlag)));
            return;
        }

        // Escolhido no instalador ou no app (HKCU\Software\ScreenCord\Language); sem escolha, o idioma do Windows.
        Loc.SetLanguage(LanguagePreference.Load());

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

        // Já aberto (talvez só na bandeja): a outra instância aparece e esta fecha.
        _instance = SingleInstance.Acquire(builder.Environment.EnvironmentName);
        if (!_instance.IsFirst)
        {
            Shutdown();
            return;
        }

        if (mockup)
        {
            builder.Services.AddScreenCordMockup();
        }
        else if (CreateErrorReporter(builder.Configuration, builder.Environment.EnvironmentName) is { } reporter)
        {
            // Avisos e erros também vão para o painel da API, com a pilha e o trace da requisição que falhou.
            // Só com a permissão da pessoa (menu de privacidade; ligado por padrão, como dizem os termos).
            _reporter = reporter;
            reporter.Enabled = TelemetryPreference.Enabled;
            TelemetryPreference.Changed += enabled => reporter.Enabled = enabled;
            builder.Logging.AddProvider(new ErrorReporterLoggerProvider(reporter));
        }

        _host = builder.Build();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
            _host?.Services.GetService<ILogger<App>>()?.LogError(args.Exception, "Exceção não observada numa tarefa.");
        _host.Services.GetRequiredService<ILogger<App>>().LogInformation(
            "ScreenCord {Version} iniciado ({Environment}, {OS}).", AppVersion.Current, builder.Environment.EnvironmentName, Environment.OSVersion.VersionString);

        // Versão nova: instala sozinho ou pergunta (preferência "Atualizar automaticamente"). Opcional enquanto a API
        // aceitar esta versão; quando não aceita mais, é atualizar ou sair.
        var updates = _host.Services.GetRequiredService<UpdateFlow>();
        updates.ExitRequested += (_, _) => ExitApp();
        if (!mockup && updates.IsEnabled)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Reaberto depois de uma instalação que falhou: não tenta a mesma versão de novo agora, senão entra em loop.
            var failed = AppVersion.TryParse(builder.Configuration["UpdateFailed"], out var failedVersion) ? failedVersion : null;
            if (await updates.RunAtStartupAsync(failed))
            {
                return;
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        // A API deixou de aceitar esta versão com o app aberto (ela subiu a versão mínima).
        _host.Services.GetRequiredService<CentralApiClient>().ClientOutdated +=
            (_, message) => Dispatcher.BeginInvoke(async () => await updates.OnClientOutdatedAsync(message, MainWindow));

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;

        // Fechar a janela deixa o app na bandeja; ele só sai pelo "Sair" do menu do ícone.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        mainWindow.HidesOnClose = true;
        _tray = new TrayIcon(mainWindow.BringToFront, ExitApp);
        _instance.OnActivationRequested(() => Dispatcher.BeginInvoke(mainWindow.BringToFront));

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
        _reporter?.Flush(TimeSpan.FromSeconds(2));
        _reporter?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static ErrorReporter? CreateErrorReporter(IConfiguration configuration, string environment)
    {
        if (!configuration.GetValue("Telemetry:Enabled", true))
        {
            return null;
        }

        try
        {
            var api = configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
            return new ErrorReporter(api.GetBaseUri(), environment, ErrorReporter.LoadInstallId(Path.GetDirectoryName(FileLoggerProvider.DefaultFolder)!));
        }
        catch (DiscordException)
        {
            // Endereço da API inválido: o erro aparece na tela quando o app tentar usá-la.
            return null;
        }
    }

    // O aplicativo vai fechar: registra e tenta mandar o relatório antes.
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogCritical(e.ExceptionObject as Exception, "Exceção não tratada; o aplicativo vai fechar.");
        _reporter?.Flush(TimeSpan.FromSeconds(3));
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
