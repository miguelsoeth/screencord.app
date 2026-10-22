using System.Diagnostics;
using ScreenCord.Infrastructure;
using ScreenCord.Localization;
using ScreenCord.Services;

namespace ScreenCord.ViewModels;

public enum ApiCheckState
{
    Checking,
    Online,
    /// <summary>A API respondeu 426: esta versão do app não é mais aceita.</summary>
    Outdated,
    Offline,
}

/// <summary>
/// Janela "Sobre": versão do app, links do projeto e a conexão com a API (endereço, <c>GET /health</c> com o tempo de
/// resposta e o estado do tempo real). A checagem roda a cada abertura e no "Testar de novo". Também tem o
/// "Procurar atualizações", que pergunta se instala mesmo uma versão que a pessoa tinha pulado.
/// </summary>
public sealed class AboutViewModel : ObservableObject
{
    public static readonly Uri WebsiteUrl = new("https://miguelsoeth.github.io/screencord/");
    public static readonly Uri SourceUrl = new("https://github.com/miguelsoeth/screencord.app");
    public static readonly Uri IssuesUrl = new("https://github.com/miguelsoeth/screencord.app/issues");
    public static readonly Uri ReleasesUrl = new("https://github.com/miguelsoeth/screencord/releases");

    private readonly IApiHealthCheck _health;
    private readonly IGroupsRealtime _realtime;
    private readonly IBrowserLauncher? _browser;
    private readonly IUpdateFlow? _updates;
    private readonly SynchronizationContext? _ui;

    private bool _isOpen;
    private ApiCheckState _apiState = ApiCheckState.Checking;
    private ApiHealth? _apiHealth;
    private long? _latencyMs;
    private string? _apiError;
    private CancellationTokenSource? _check;
    private bool _checkingUpdates;
    private UpdateCheckResult? _updateResult;

    public AboutViewModel(IApiHealthCheck health, IGroupsRealtime realtime, IBrowserLauncher? browser = null, IUpdateFlow? updates = null)
    {
        _health = health;
        _realtime = realtime;
        _browser = browser;
        _updates = updates;
        _ui = SynchronizationContext.Current;
        _realtime.StateChanged += (_, _) => Post(() => OnPropertyChanged(nameof(RealtimeStatusText)));

        OpenCommand = new RelayCommand(() => IsOpen = true);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => ApiState != ApiCheckState.Checking);
        OpenLinkCommand = new RelayCommand<Uri>(OpenLink);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => CanCheckUpdates);
        ReleaseNotesUrl = AppVersion.IsDevBuild ? ReleasesUrl : new Uri(ReleasesUrl, $"releases/tag/v{AppVersion.Current}");
    }

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Consulta a API de novo.</summary>
    public AsyncRelayCommand CheckCommand { get; }

    /// <summary>Procura uma versão nova e pergunta se instala.</summary>
    public AsyncRelayCommand CheckUpdatesCommand { get; }

    /// <summary>Só no app instalado (build de desenvolvimento e mockup não se atualizam).</summary>
    public bool CanCheckUpdates => _updates?.IsEnabled == true;

    /// <summary>Resultado do "Procurar atualizações"; vazio antes da primeira procura.</summary>
    public string UpdateStatusText => _checkingUpdates
        ? Strings.CheckingForUpdates
        : _updateResult == UpdateCheckResult.NoUpdate ? Strings.NoUpdateFound : string.Empty;

    /// <summary>Abre um dos links do projeto (parâmetro: a URL) no navegador.</summary>
    public RelayCommand<Uri> OpenLinkCommand { get; }

    /// <summary>Notas desta versão (no build de desenvolvimento, a lista de releases).</summary>
    public Uri ReleaseNotesUrl { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!SetProperty(ref _isOpen, value))
            {
                return;
            }

            if (value)
            {
                _ = CheckAsync();
            }
            else
            {
                _check?.Cancel();
                _updateResult = null;
                OnPropertyChanged(nameof(UpdateStatusText));
            }
        }
    }

    public string VersionText => AppVersion.IsDevBuild
        ? string.Format(Loc.Culture, Strings.VersionDevFormat, AppVersion.Current)
        : string.Format(Loc.Culture, Strings.VersionFormat, AppVersion.Current);

    /// <summary>Servidor da API, sem o esquema (ex.: <c>api.screencord.miguelsoeth.online</c>).</summary>
    public string ApiAddress => _health.BaseUri is { } uri
        ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
        : Strings.ApiNotConfigured;

    public ApiCheckState ApiState
    {
        get => _apiState;
        private set
        {
            if (SetProperty(ref _apiState, value))
            {
                OnPropertyChanged(nameof(ApiStatusText));
                CheckCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ApiStatusText => ApiState switch
    {
        ApiCheckState.Checking => Strings.ApiChecking,
        ApiCheckState.Online => string.Format(Loc.Culture, Strings.ApiOnlineFormat, _latencyMs ?? 0),
        ApiCheckState.Outdated => Strings.ApiVersionNotAccepted,
        _ => _apiError ?? Strings.ApiUnexpected,
    };

    /// <summary>Versão informada pela API; traço enquanto não houver resposta.</summary>
    public string ApiVersionText => string.IsNullOrWhiteSpace(_apiHealth?.Version) ? "—" : _apiHealth.Version;

    public string RealtimeStatusText => _realtime.State switch
    {
        RealtimeState.Connected => Strings.Connected,
        RealtimeState.Connecting => Strings.Connecting,
        RealtimeState.Reconnecting => Strings.RealtimeReconnecting,
        _ => Strings.RealtimeOff,
    };

    /// <summary>Resumo para colar num relato de problema (sempre em inglês, para quem for ler).</summary>
    public string DetailsText => string.Join(Environment.NewLine,
        $"ScreenCord {AppVersion.Current}{(AppVersion.IsDevBuild ? " (dev)" : "")}",
        $"Windows {Environment.OSVersion.Version} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}",
        $"API: {ApiAddress} ({ApiState}{(_latencyMs is { } ms && ApiState == ApiCheckState.Online ? $", {ms} ms" : "")}), version {ApiVersionText}",
        $"Realtime: {_realtime.State}",
        $"Language: {Loc.Culture.Name}");

    private async Task CheckAsync()
    {
        _check?.Cancel();
        using var check = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _check = check;
        _apiError = null;
        ApiState = ApiCheckState.Checking;

        var watch = Stopwatch.StartNew();
        try
        {
            var health = await _health.CheckAsync(check.Token);
            _apiHealth = health;
            _latencyMs = watch.ElapsedMilliseconds;
            ApiState = ApiCheckState.Online;
        }
        catch (DiscordException ex) when (ex.Kind == DiscordErrorKind.ClientOutdated)
        {
            ApiState = ApiCheckState.Outdated;
        }
        catch (OperationCanceledException) when (_check != check || !IsOpen)
        {
            // Outra checagem começou ou a janela fechou: essa não vale mais.
            return;
        }
        catch (Exception ex)
        {
            _apiError = ex is OperationCanceledException ? Strings.ApiTimeout : ErrorMessages.For(ex);
            ApiState = ApiCheckState.Offline;
        }
        finally
        {
            if (_check == check)
            {
                _check = null;
            }
        }

        // O estado pode não ter mudado (ex.: online de novo), mas o tempo de resposta e a versão sim.
        OnPropertyChanged(nameof(ApiStatusText));
        OnPropertyChanged(nameof(ApiVersionText));
    }

    private async Task CheckUpdatesAsync()
    {
        if (_updates is null)
        {
            return;
        }

        _checkingUpdates = true;
        _updateResult = null;
        OnPropertyChanged(nameof(UpdateStatusText));
        try
        {
            _updateResult = await _updates.CheckNowAsync();
        }
        finally
        {
            _checkingUpdates = false;
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    private void OpenLink(Uri url)
    {
        try
        {
            _browser?.Open(url);
        }
        catch (DiscordException)
        {
            // Sem navegador padrão: nada a fazer além de não derrubar a janela.
        }
    }

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
