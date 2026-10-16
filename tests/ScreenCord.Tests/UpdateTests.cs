using System.Net;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ScreenCord.Tests;

public class AppUpdaterTests
{
    private const string Download = "https://github.com/miguelsoeth/screencord/releases/download";

    private static GitHubRelease Release(string tag, string? assetName = null, string? url = null, long size = 1000) => new()
    {
        TagName = tag,
        Assets =
        [
            new GitHubReleaseAsset
            {
                Name = assetName ?? $"ScreenCord-Setup-{tag.TrimStart('v')}-win64.msi",
                Size = size,
                BrowserDownloadUrl = url ?? $"{Download}/{tag}/ScreenCord-Setup-{tag.TrimStart('v')}-win64.msi",
            },
        ],
    };

    [Theory]
    [InlineData("v0.2.0", "0.1.0")]
    [InlineData("v0.1.1", "0.1.0")]
    [InlineData("v1.0.0", "0.9.9")]
    public void ReleaseMaisNova_EEscolhida(string tag, string current)
    {
        var update = AppUpdater.SelectUpdate(Release(tag), current);

        Assert.NotNull(update);
        Assert.Equal(Version.Parse(tag.TrimStart('v')), update.Version);
        Assert.Equal(1000, update.Size);
    }

    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("v0.1.0", "0.2.0")]
    [InlineData("nao-e-versao", "0.1.0")]
    public void ReleaseIgualOuMaisAntiga_NaoAtualiza(string tag, string current)
    {
        Assert.Null(AppUpdater.SelectUpdate(Release(tag), current));
    }

    [Fact]
    public void RascunhoOuPreRelease_NaoAtualiza()
    {
        var draft = Release("v0.2.0");
        draft.Draft = true;
        var prerelease = Release("v0.2.0");
        prerelease.Prerelease = true;

        Assert.Null(AppUpdater.SelectUpdate(draft, "0.1.0"));
        Assert.Null(AppUpdater.SelectUpdate(prerelease, "0.1.0"));
    }

    [Theory]
    [InlineData("Outro-0.2.0.msi", $"{Download}/v0.2.0/Outro-0.2.0.msi")]
    [InlineData(null, "https://example.com/miguelsoeth/screencord/releases/download/v0.2.0/ScreenCord-Setup-0.2.0-win64.msi")]
    [InlineData(null, "http://github.com/miguelsoeth/screencord/releases/download/v0.2.0/ScreenCord-Setup-0.2.0-win64.msi")]
    [InlineData(null, "https://github.com/outra-pessoa/screencord/releases/download/v0.2.0/ScreenCord-Setup-0.2.0-win64.msi")]
    public void InstaladorForaDoPadraoOuDeOutraOrigem_ERecusado(string? assetName, string url)
    {
        Assert.Null(AppUpdater.SelectUpdate(Release("v0.2.0", assetName, url), "0.1.0"));
    }

    [Fact]
    public void InstaladorVazio_ERecusado()
    {
        Assert.Null(AppUpdater.SelectUpdate(Release("v0.2.0", size: 0), "0.1.0"));
    }

    [Fact]
    public async Task ConsultaAUltimaReleaseDoRepoPublico()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, $$"""
            {"tag_name":"v0.3.0","draft":false,"prerelease":false,"assets":[{"name":"ScreenCord-Setup-0.3.0-win64.msi","size":5000,"browser_download_url":"{{Download}}/v0.3.0/ScreenCord-Setup-0.3.0-win64.msi"}]}
            """);
        var updater = new AppUpdater(new StubHttpClientFactory(handler), NullLogger<AppUpdater>.Instance, "0.2.0");

        var update = await updater.FindUpdateAsync();

        Assert.Equal(AppUpdater.LatestReleaseUri, Assert.Single(handler.Requests).Uri);
        Assert.Equal(new Version(0, 3, 0), update?.Version);
    }

    [Fact]
    public async Task GitHubForaDoAr_SegueSemAtualizar()
    {
        var handler = new StubHttpHandler().EnqueueException(new HttpRequestException("sem rede"));
        var updater = new AppUpdater(new StubHttpClientFactory(handler), NullLogger<AppUpdater>.Instance, "0.2.0");

        Assert.Null(await updater.FindUpdateAsync());
    }

    [Fact]
    public async Task BuildDeDesenvolvimento_NaoConsultaNemAtualiza()
    {
        var handler = new StubHttpHandler();
        var updater = new AppUpdater(new StubHttpClientFactory(handler), NullLogger<AppUpdater>.Instance, "0.0.0-dev");

        Assert.False(updater.IsEnabled);
        Assert.Null(await updater.FindUpdateAsync());
        Assert.Empty(handler.Requests);
    }
}

public class ClientVersionTests
{
    private readonly StubHttpHandler _handler = new();
    private readonly FakeSessionManager _session = new() { HasSession = true, AccessToken = "discord-access" };

    private CentralApiClient Create() =>
        new(new StubHttpClientFactory(_handler), _session, Options.Create(new ApiOptions { BaseUrl = "http://localhost:5080" }), NullLogger<CentralApiClient>.Instance);

    [Fact]
    public async Task TodaRequisicao_InformaAVersaoDoApp()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"token":"t","expiresAtUtc":"2026-11-12T12:00:00+00:00","user":null}""")
                .Enqueue(HttpStatusCode.OK, "[]");

        await Create().SendAsync<List<UserDto>>(HttpMethod.Get, "friends", null, CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, request => Assert.Equal(AppVersion.Current, request.Headers[ClientVersionContract.Header]));
    }

    [Fact]
    public async Task Resposta426_AvisaQueOAppPrecisaAtualizar()
    {
        const string message = "Esta versão do ScreenCord (0.1.0) não é mais aceita pelo servidor.";
        _handler.Enqueue(HttpStatusCode.UpgradeRequired, $$"""{"code":"{{ClientVersionContract.OutdatedCode}}","message":"{{message}}"}""");
        var api = Create();
        string? reported = null;
        api.ClientOutdated += (_, text) => reported = text;

        var error = await Assert.ThrowsAsync<DiscordException>(() => api.GetApiTokenAsync());

        Assert.Equal(DiscordErrorKind.ClientOutdated, error.Kind);
        Assert.Equal(message, reported);
        Assert.Equal(message, ErrorMessages.For(error));
    }

    [Theory]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData("v1.3.4", 1, 3, 4)]
    [InlineData("0.0.0-dev", 0, 0, 0)]
    public void InterpretaVersoes(string text, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Fact]
    public void TestesRodamComoBuildDeDesenvolvimento()
    {
        Assert.Equal("0.0.0-dev", AppVersion.Current);
        Assert.True(AppVersion.IsDevBuild);
    }
}
