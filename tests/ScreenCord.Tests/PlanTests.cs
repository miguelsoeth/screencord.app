using System.Net;
using System.Text.Json;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ScreenCord.Tests;

/// <summary>Plano da conta: limites do Pro na escolha de compartilhamento e compra pelo navegador.</summary>
public class PlanTests
{
    private const string LoginJson = """
        {"token":"api-token","expiresAtUtc":"2026-11-12T12:00:00+00:00","user":{"id":"22222222-2222-2222-2222-222222222222","discordId":"1001","username":"ana.silva","globalName":"Ana","discriminator":"0","avatarHash":null,"relationship":"None"}}
        """;

    private const string ProJson = """
        {"tier":"Pro","proUntilUtc":"2026-11-16T12:00:00+00:00","limits":{"maxHeight":1080,"maxHeightAt60Fps":1080,"maxBitrateKbps":5000,"maxMembers":20,"maxCreatedGroups":100},
         "offers":[{"id":"pro-30","name":"Pro por 30 dias","days":30,"priceCents":1490},{"id":"","name":"inválida","days":0,"priceCents":0}]}
        """;

    private readonly StubHttpHandler _handler = new();
    private readonly FakeGroupsRealtime _realtime = new();

    private CentralPlanService CreateService() =>
        new(new CentralApiClient(
                new StubHttpClientFactory(_handler),
                new FakeSessionManager { HasSession = true, AccessToken = "discord-access" },
                Options.Create(new ApiOptions { BaseUrl = "http://localhost:5080" }),
                NullLogger<CentralApiClient>.Instance),
            _realtime);

    private static PlanDto Pro(params ProOfferDto[] offers) => new(PlanTier.Pro, DateTimeOffset.UtcNow.AddDays(30), PlanLimits.Pro, offers);

    [Fact]
    public async Task Servico_CarregaOPlano_EDescartaOfertasInvalidas()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson).Enqueue(HttpStatusCode.OK, ProJson);
        var service = CreateService();
        Assert.Equal(PlanTier.Free, service.Current.Tier);

        var plan = await service.RefreshAsync();

        Assert.Equal("http://localhost:5080/me/plan", _handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal(PlanTier.Pro, plan.Tier);
        Assert.Equal(PlanLimits.Pro, plan.Limits);
        Assert.Equal("pro-30", Assert.Single(plan.Offers).Id);
    }

    [Fact]
    public async Task Servico_SemLimitesValidos_FicaNoGratuito()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson).Enqueue(HttpStatusCode.OK, """{"tier":"Pro","proUntilUtc":null,"limits":null,"offers":[]}""");
        var service = CreateService();

        Assert.Equal(CentralPlanService.Free, await service.RefreshAsync());
    }

    [Fact]
    public async Task Servico_PlanoNovoPeloHub_AtualizaEAvisa()
    {
        var service = CreateService();
        var changes = 0;
        service.Changed += (_, _) => changes++;

        _realtime.RaisePlanChanged(Pro());

        Assert.Equal(1, changes);
        Assert.Equal(PlanTier.Pro, service.Current.Tier);
        service.Reset();
        Assert.Equal(PlanTier.Free, service.Current.Tier);
    }

    [Fact]
    public async Task Servico_Checkout_PedeAOfertaEDevolveAPaginaDoAbacatePay()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson).Enqueue(HttpStatusCode.OK, """{"url":"https://app.abacatepay.com/pay/bill_1"}""");
        var service = CreateService();

        var url = await service.StartCheckoutAsync("pro-30");

        Assert.Equal("https://app.abacatepay.com/pay/bill_1", url!.AbsoluteUri);
        var request = _handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:5080/billing/checkout", request.Uri.AbsoluteUri);
        Assert.Equal("pro-30", JsonDocument.Parse(request.Body).RootElement.GetProperty("offerId").GetString());
    }

    [Theory]
    [InlineData("https://app.abacatepay.com/pay/bill_1", true)]
    [InlineData("https://abacatepay.com/pay/bill_1", true)]
    [InlineData("http://app.abacatepay.com/pay/bill_1", false)]
    [InlineData("https://abacatepay.com.golpe.example/pay", false)]
    [InlineData("https://golpeabacatepay.com/pay", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(null, false)]
    public void Checkout_SoAbreHttpsDoAbacatePay(string? url, bool accepted)
    {
        Assert.Equal(accepted, CentralPlanService.IsCheckoutUrl(url, out _));
    }

    [Fact]
    public async Task Servico_CheckoutComPaginaDeOutroSite_EhRecusado()
    {
        _handler.Enqueue(HttpStatusCode.OK, LoginJson).Enqueue(HttpStatusCode.OK, """{"url":"https://golpe.example/pay"}""");
        var service = CreateService();

        var error = await Assert.ThrowsAsync<DiscordException>(() => service.StartCheckoutAsync("pro-30"));

        Assert.Equal(DiscordErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public void Seletor_ComPro_Libera1080pE60fpsETaxaDe5Mbps()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => [], limits: PlanLimits.Pro);

        Assert.All(picker.Resolutions.Options, o => Assert.True(o.IsAvailable));
        picker.Resolutions.Selected = ShareResolution.P1080;
        Assert.All(picker.FrameRates.Options, o => Assert.True(o.IsAvailable));
        picker.FrameRates.Selected = ShareFrameRate.Fps60;

        var settings = picker.BuildRequest()!.Settings;
        Assert.Equal(new ShareSettings(ShareResolution.P1080, ShareFrameRate.Fps60, true, 5000), settings);
        Assert.Equal("Qualidade automática, até 5 Mbps", picker.BitrateText);
        Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(1920, 1080, settings.Fps, settings.BitrateKbps, true), PlanLimits.Pro));
    }

    [Fact]
    public void Seletor_SemPro_60fpsEm720pIndicaOPro()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);

        var fps60 = picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps60);

        Assert.False(fps60.IsAvailable);
        Assert.Equal("60 fps em 720p: Disponível no ScreenCord Pro.", fps60.Detail);
        Assert.Equal("Qualidade automática, até 3 Mbps", picker.BitrateText);
    }

    [Fact]
    public void Seletor_ConfiguracaoDoProDepoisQueOProVenceu_VoltaAoPadrao()
    {
        var proSettings = new ShareSettings(ShareResolution.P1080, ShareFrameRate.Fps60, true, 5000);

        var picker = new SharePickerViewModel(proSettings, () => [FakeScreenShareUi.Screen], () => []);

        Assert.Equal(ShareSettings.Default, picker.BuildRequest()!.Settings);
    }

    [Fact]
    public async Task Grupos_PassamOsLimitesDoPlanoParaAEscolha()
    {
        var groups = new FakeGroupsService();
        var ui = new FakeScreenShareUi();
        var plans = CreateService();
        _realtime.RaisePlanChanged(Pro());
        var id = groups.Add("Amigos", FakeFriendsService.Create("bruno", "Bruno", RelationshipStatus.Friend));
        var vm = new GroupsViewModel(groups, _realtime, new FakeScreenShareService(), ui, plans);
        await vm.LoadAsync();
        _realtime.SetState(RealtimeState.Connected);
        await vm.JoinCommand.ExecuteAsync(vm.Groups.Single(g => g.Id == id));

        await vm.StartShareCommand.ExecuteAsync();

        Assert.Equal(PlanLimits.Pro, Assert.Single(ui.PickLimits));
    }

    [Fact]
    public async Task Tela_ComprarAbreONavegador_EAvisaQuandoOPagamentoChega()
    {
        var plans = new FakePlanService { Url = new Uri("https://app.abacatepay.com/pay/bill_1") };
        plans.Set(CentralPlanService.Free with { Offers = [new("pro-30", "Pro por 30 dias", 30, 1490)] });
        var browser = new FakeBrowserLauncher();
        var vm = new PlanViewModel(plans, browser) { IsOpen = true };

        var offer = Assert.Single(vm.Offers);
        Assert.Equal("R$ 14,90", offer.PriceText);
        Assert.True(vm.IsFree);
        Assert.Equal("Você está no plano gratuito.", vm.StatusText);

        await vm.BuyCommand.ExecuteAsync(offer);

        Assert.Equal("pro-30", plans.CheckoutOffer);
        Assert.Equal(plans.Url, browser.OpenedUrl);
        Assert.True(vm.HasMessage);

        plans.Set(Pro());

        Assert.True(vm.IsPro);
        Assert.Equal("Pagamento confirmado. Obrigado por apoiar o ScreenCord!", vm.Message);
        Assert.StartsWith("Seu Pro vale até ", vm.StatusText);
    }

    [Fact]
    public async Task Tela_FalhaNoCheckout_MostraOErroSemAbrirONavegador()
    {
        var plans = new FakePlanService { Failure = new DiscordException(DiscordErrorKind.ServiceUnavailable, "fora") { Source = ErrorSource.CentralApi } };
        plans.Set(CentralPlanService.Free with { Offers = [new("pro-30", "Pro por 30 dias", 30, 1490)] });
        var browser = new FakeBrowserLauncher();
        var vm = new PlanViewModel(plans, browser);

        await vm.BuyCommand.ExecuteAsync(vm.Offers[0]);

        Assert.True(vm.HasError);
        Assert.Null(browser.OpenedUrl);
    }

    [Fact]
    public void Tela_AoDesconectar_VoltaAoGratuito()
    {
        var plans = new FakePlanService();
        plans.Set(Pro());
        var vm = new PlanViewModel(plans, new FakeBrowserLauncher()) { IsOpen = true };

        vm.Clear();

        Assert.False(vm.IsOpen);
        Assert.True(vm.IsFree);
    }

    private sealed class FakePlanService : IPlanService
    {
        public PlanDto Current { get; private set; } = CentralPlanService.Free;

        public Uri? Url { get; init; }

        public Exception? Failure { get; init; }

        public string? CheckoutOffer { get; private set; }

        public event EventHandler? Changed;

        public void Set(PlanDto plan)
        {
            Current = plan;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<PlanDto> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<Uri?> StartCheckoutAsync(string offerId, CancellationToken cancellationToken = default)
        {
            CheckoutOffer = offerId;
            return Failure is null ? Task.FromResult(Url) : Task.FromException<Uri?>(Failure);
        }

        public void Reset() => Set(CentralPlanService.Free);
    }
}
