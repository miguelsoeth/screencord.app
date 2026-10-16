using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenCord.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Services;

/// <summary>Versão nova publicada nas GitHub Releases do repositório público.</summary>
public sealed record AppUpdate(Version Version, Uri InstallerUrl, long Size);

public interface IAppUpdater
{
    /// <summary>False em builds de desenvolvimento: só o app instalado se atualiza.</summary>
    bool IsEnabled { get; }

    /// <summary>A release mais recente, se for mais nova que este build; null se não houver ou em caso de falha.</summary>
    Task<AppUpdate?> FindUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Baixa o instalador e agenda a instalação para quando este processo terminar; depois dela o app
    /// abre de novo. Quem chama deve encerrar o aplicativo em seguida.
    /// </summary>
    Task DownloadAndScheduleInstallAsync(AppUpdate update, IProgress<double>? progress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Atualização automática pelas GitHub Releases de <see cref="Repository"/> (o mesmo MSI do download manual).
/// O MSI é por usuário (sem administrador) e um de versão maior substitui o instalado, mantendo a sessão.
/// </summary>
public sealed class AppUpdater : IAppUpdater
{
    public const string ClientName = "Updates";
    public const string Repository = "miguelsoeth/screencord";

    public static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>
    /// Argumento com que o app é reaberto quando a instalação de uma versão falha: ele não tenta a mesma versão de novo.
    /// </summary>
    public const string FailedArgument = "--UpdateFailed";

    /// <summary>
    /// Nome do MSI na release. Até a 0.3.0 era "ScreenCord-Setup-X.Y.Z-win64.msi", e o script de instalação daquelas
    /// versões passava o caminho errado ao msiexec e reabria o app em loop. Por isso o nome mudou: elas não acham o
    /// instalador novo e seguem funcionando sem se atualizar (quem está nelas instala a próxima à mão uma vez).
    /// </summary>
    public static string InstallerName(Version version) => $"ScreenCord-Setup-{version.ToString(3)}-x64.msi";

    private const long MaxInstallerSize = 300L * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppUpdater> _logger;
    private readonly string _currentVersion;

    public AppUpdater(IHttpClientFactory httpClientFactory, ILogger<AppUpdater> logger)
        : this(httpClientFactory, logger, AppVersion.Current)
    {
    }

    public AppUpdater(IHttpClientFactory httpClientFactory, ILogger<AppUpdater> logger, string currentVersion)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _currentVersion = currentVersion;
    }

    public bool IsEnabled => AppVersion.TryParse(_currentVersion, out var version) && version != new Version(0, 0, 0);

    public async Task<AppUpdate?> FindUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        try
        {
            using var response = await _httpClientFactory.CreateClient(ClientName)
                .GetAsync(LatestReleaseUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Não foi possível consultar a versão mais recente (HTTP {Status}).", (int)response.StatusCode);
                return null;
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken).ConfigureAwait(false);
            var update = SelectUpdate(release, _currentVersion);
            if (update is not null)
            {
                _logger.LogInformation("Versão nova disponível: {Version} (atual {Current}).", update.Version.ToString(3), _currentVersion);
            }

            return update;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or OperationCanceledException)
        {
            _logger.LogWarning("Não foi possível consultar a versão mais recente ({ExceptionType}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Escolhe o MSI da release se ela for mais nova. Só aceita o arquivo com o nome que o release.yml gera,
    /// baixado do próprio repositório pelo HTTPS do GitHub.
    /// </summary>
    public static AppUpdate? SelectUpdate(GitHubRelease? release, string currentVersion)
    {
        if (release is null || release.Draft || release.Prerelease ||
            !AppVersion.TryParse(release.TagName, out var latest) ||
            !AppVersion.TryParse(currentVersion, out var current) || latest <= current)
        {
            return null;
        }

        var expectedName = InstallerName(latest);
        var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || asset.Size <= 0 || asset.Size > MaxInstallerSize ||
            !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new AppUpdate(latest, url, asset.Size);
    }

    public async Task DownloadAndScheduleInstallAsync(AppUpdate update, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ScreenCord", "Updates");
        Directory.CreateDirectory(folder);
        var installer = Path.Combine(folder, InstallerName(update.Version));

        await DownloadAsync(update, installer, progress, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Instalando a versão {Version} ao fechar o aplicativo.", update.Version.ToString(3));
        StartInstaller(installer, update.Version);
    }

    private async Task DownloadAsync(AppUpdate update, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var partial = path + ".partial";
        try
        {
            using var response = await _httpClientFactory.CreateClient(ClientName)
                .GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > update.Size)
                    {
                        throw new InvalidDataException("O instalador baixado é maior que o anunciado.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)total / update.Size);
                }

                if (total != update.Size)
                {
                    throw new InvalidDataException("O download do instalador ficou incompleto.");
                }
            }

            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Um PowerShell oculto espera este processo sair (o exe não pode estar em uso), roda o MSI com barra de
    /// progresso e sem perguntas, e abre o ScreenCord de novo: a versão nova, ou a antiga com <see cref="FailedArgument"/>
    /// se a instalação falhar (o log do msiexec fica em <see cref="FileLoggerProvider.DefaultFolder"/>).
    /// </summary>
    private void StartInstaller(string installer, Version version)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ScreenCord", "ScreenCord.exe");
        if (!File.Exists(exe))
        {
            exe = Environment.ProcessPath ?? exe;
        }

        Directory.CreateDirectory(FileLoggerProvider.DefaultFolder);
        var log = Path.Combine(FileLoggerProvider.DefaultFolder, $"msiexec-{version.ToString(3)}.log");
        var script = BuildInstallScript(Environment.ProcessId, installer, log, exe, version);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
        _logger.LogInformation("Log da instalação: {Log}.", log);
    }

    /// <summary>
    /// O script de instalação. Os argumentos do msiexec vão numa string só, montada aqui com as aspas certas:
    /// a lista do Start-Process do Windows PowerShell não põe aspas, e o "+" dentro de @(...) perde para a vírgula.
    /// </summary>
    public static string BuildInstallScript(int processId, string installer, string log, string exe, Version version, string msiexec = "msiexec.exe")
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        var msiArguments = $"/i \"{installer}\" /passive /norestart /l*v \"{log}\"";
        var failedArguments = $"{FailedArgument}={version.ToString(3)}";

        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{processId}} -Timeout 60
            $msi = Start-Process -FilePath {{Quote(msiexec)}} -ArgumentList {{Quote(msiArguments)}} -Wait -PassThru
            Remove-Item -LiteralPath {{Quote(installer)}} -Force
            $exe = {{Quote(exe)}}
            if ($msi -and ($msi.ExitCode -eq 0 -or $msi.ExitCode -eq 3010)) {
                Start-Process -FilePath $exe
            } else {
                Start-Process -FilePath $exe -ArgumentList {{Quote(failedArguments)}}
            }
            """;
    }
}

/// <summary>Parte da resposta de <c>GET /repos/{owner}/{repo}/releases/latest</c> usada na atualização.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset>? Assets { get; set; }
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}
