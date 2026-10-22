using System.Net;
using ScreenCord.Infrastructure;
using ScreenCord.Localization;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ScreenCord.Tests;

[Collection(nameof(LanguageCollection))]
public class AboutViewModelTests
{
    private readonly StubHttpHandler _handler = new();
    private readonly FakeSessionManager _session = new() { HasSession = true, AccessToken = "discord-access" };
    private readonly FakeGroupsRealtime _realtime = new();

    private CentralApiClient CreateApi() => new(
        new StubHttpClientFactory(_handler),
        _session,
        Options.Create(new ApiOptions { BaseUrl = "http://localhost:5080" }),
        NullLogger<CentralApiClient>.Instance);

    private AboutViewModel Create() => new(new CentralApiHealthCheck(CreateApi()), _realtime);

    [Fact]
    public async Task Health_SemSessao_ConsultaSoOHealth()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"status":"ok","version":"1.4.0","minClientVersion":"0.3.0"}""");

        var health = await CreateApi().GetHealthAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://localhost:5080/health", request.Uri.AbsoluteUri);
        Assert.Null(request.Authorization);
        Assert.Equal("1.4.0", health.Version);
        Assert.Equal("0.3.0", health.MinClientVersion);
    }

    [Fact]
    public async Task Abrir_ApiRespondendo_MostraOnlineEAVersaoDaApi()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"status":"ok","version":"1.4.0","minClientVersion":"0.3.0"}""");
        var about = Create();

        about.IsOpen = true;
        await WaitForCheckAsync(about);

        Assert.Equal(ApiCheckState.Online, about.ApiState);
        Assert.Equal("1.4.0", about.ApiVersionText);
        Assert.Equal("localhost:5080", about.ApiAddress);
        Assert.StartsWith("Online", about.ApiStatusText);
        Assert.Contains("localhost:5080", about.DetailsText);
    }

    [Fact]
    public async Task Abrir_ApiFora_MostraOffline()
    {
        _handler.EnqueueException(new HttpRequestException("sem rede"));
        var about = Create();

        about.IsOpen = true;
        await WaitForCheckAsync(about);

        Assert.Equal(ApiCheckState.Offline, about.ApiState);
        Assert.Equal(Strings.ApiNetwork, about.ApiStatusText);
        Assert.Equal("—", about.ApiVersionText);
    }

    [Fact]
    public async Task Abrir_VersaoRecusada_MostraDesatualizado()
    {
        _handler.Enqueue(HttpStatusCode.UpgradeRequired, """{"code":"client_outdated","message":"Atualize o app."}""");
        var about = Create();

        about.IsOpen = true;
        await WaitForCheckAsync(about);

        Assert.Equal(ApiCheckState.Outdated, about.ApiState);
        Assert.Equal(Strings.ApiVersionNotAccepted, about.ApiStatusText);
    }

    [Fact]
    public async Task TempoReal_AcompanhaAConexao()
    {
        var about = Create();
        var changed = new List<string?>();
        about.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _realtime.SetState(RealtimeState.Connected);
        // O aviso chega pela thread da interface (aqui, o contexto do xUnit).
        for (var i = 0; i < 100 && !changed.Contains(nameof(AboutViewModel.RealtimeStatusText)); i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains(nameof(AboutViewModel.RealtimeStatusText), changed);
        Assert.Equal(Strings.Connected, about.RealtimeStatusText);
    }

    [Fact]
    public void Versao_BuildDeDesenvolvimento_AparecePeloNome()
    {
        var about = Create();

        Assert.True(AppVersion.IsDevBuild);
        Assert.Equal(string.Format(Loc.Culture, Strings.VersionDevFormat, AppVersion.Current), about.VersionText);
        Assert.Equal(AboutViewModel.ReleasesUrl, about.ReleaseNotesUrl);
    }

    private static async Task WaitForCheckAsync(AboutViewModel about)
    {
        for (var i = 0; i < 200 && about.ApiState == ApiCheckState.Checking; i++)
        {
            await Task.Delay(10);
        }
    }
}
