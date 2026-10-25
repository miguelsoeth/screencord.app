using ScreenCord.Contracts;
using ScreenCord.Localization;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;

namespace ScreenCord.Tests;

public class ScreenShareViewModelTests
{
    private readonly FakeGroupsService _groups = new();
    private readonly FakeGroupsRealtime _realtime = new();
    private readonly FakeFriendsService _friends = new();
    private readonly FakeScreenShareService _share = new();
    private readonly FakeScreenShareUi _ui = new();
    private readonly Person _bruno = FakeFriendsService.Create("bruno", "Bruno", RelationshipStatus.Friend);

    private async Task<(GroupsViewModel Vm, GroupItemViewModel Group)> InRoomAsync()
    {
        var id = _groups.Add("Amigos", _bruno);
        var vm = new GroupsViewModel(_groups, _realtime, _share, _ui);
        await vm.LoadAsync();
        _realtime.SetState(RealtimeState.Connected);
        var group = vm.Groups.Single(g => g.Id == id);
        await vm.JoinCommand.ExecuteAsync(group);
        return (vm, group);
    }

    private static ScreenShareDto ShareOf(Person person) => new(person.UserId, 1280, 720, 30, 4000, true);

    private void RaisePresence(GroupItemViewModel group, long version, params ScreenShareDto[] shares) =>
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id,
            [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(_bruno)],
            version,
            shares));

    [Fact]
    public void PeloServidor_Padrao720p30fpsComAudioAte3000kbps()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);

        var request = picker.BuildRequest()!;

        Assert.Equal(FakeScreenShareUi.Screen, request.Source);
        Assert.Equal(new ShareSettings(ShareResolution.P720, ShareFrameRate.Fps30, true, 3000), request.Settings);
        Assert.False(picker.IsDirect);
        Assert.Equal("Qualidade automática, até 3 Mbps · pelo servidor", picker.BitrateText);
    }

    [Fact]
    public void PeloServidor_720pA60fpsE1080pA30fps_1080pA60fpsApareceDesligado()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);
        var fps60 = picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps60);

        Assert.Equal(["540p", "720p", "1080p"], picker.Resolutions.Options.Select(o => o.Label));
        Assert.All(picker.Resolutions.Options, o => Assert.True(o.IsAvailable));
        Assert.True(fps60.IsAvailable);

        picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P1080).IsSelected = true;

        Assert.False(fps60.IsAvailable);
        Assert.Equal("Só nas salas locais, com a mídia direto entre os computadores.", fps60.Detail);
        fps60.IsSelected = true;
        Assert.Equal(new ShareSettings(ShareResolution.P1080, ShareFrameRate.Fps30, true, 3000), picker.BuildRequest()!.Settings);

        picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P720).IsSelected = true;
        fps60.IsSelected = true;

        Assert.Equal(new ShareSettings(ShareResolution.P720, ShareFrameRate.Fps60, true, 3000), picker.BuildRequest()!.Settings);
    }

    [Fact]
    public void PeloServidor_ComLimitesMaisBaixosDaApi_AcimaApareceDesligado()
    {
        var limits = new StreamLimits(720, 30, 2500);
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => [], limits: limits);

        Assert.Equal([true, true, false], picker.Resolutions.Options.Select(o => o.IsAvailable));
        var fullHd = picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P1080);
        fullHd.IsSelected = true;
        Assert.False(fullHd.IsSelected);

        var fps60 = picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps60);
        Assert.False(fps60.IsAvailable);
        fps60.IsSelected = true;
        Assert.Equal(ShareSettings.Default with { BitrateKbps = 2500 }, picker.BuildRequest()!.Settings);
        Assert.False(picker.IsDirect);
        Assert.Equal("Qualidade automática, até 2,5 Mbps · pelo servidor", picker.BitrateText);
    }

    [Fact]
    public void PeloServidor_LimitesSemCombinacoes_DeUmaApiAntiga_ValemComoRetangulo()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => [], limits: new StreamLimits(1080, 60, 2500));

        Assert.All(picker.Resolutions.Options, o => Assert.True(o.IsAvailable));
        Assert.All(picker.FrameRates.Options, o => Assert.True(o.IsAvailable));
        Assert.False(picker.IsDirect);
    }

    [Fact]
    public void SalaLocal_Libera1080pA60fpsAte5Mbps()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => [], limits: StreamLimits.Direct);

        Assert.All(picker.Resolutions.Options, o => Assert.True(o.IsAvailable));
        picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P1080).IsSelected = true;
        picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps60).IsSelected = true;

        Assert.Equal(new ShareSettings(ShareResolution.P1080, ShareFrameRate.Fps60, true, 5000), picker.BuildRequest()!.Settings);
        Assert.True(picker.IsDirect);
        Assert.Equal("Qualidade automática, até 5 Mbps · direto entre os computadores", picker.BitrateText);
    }

    [Theory]
    [InlineData(ShareResolution.P1080, ShareFrameRate.Fps30)]
    [InlineData(ShareResolution.P720, ShareFrameRate.Fps60)]
    [InlineData(ShareResolution.P540, ShareFrameRate.Fps60)]
    public void PeloServidor_ConfiguracaoForaDosLimites_VoltaAoPadrao(ShareResolution resolution, ShareFrameRate frameRate)
    {
        var limits = new StreamLimits(720, 30, 2500);
        var picker = new SharePickerViewModel(new ShareSettings(resolution, frameRate, true, 5000), () => [FakeScreenShareUi.Screen], () => [], limits: limits);

        Assert.Equal(ShareSettings.DefaultFor(limits), picker.BuildRequest()!.Settings);
    }

    [Fact]
    public void PadraoDeCadaCaminho_MaiorResolucaoA30fps()
    {
        Assert.Equal(new ShareSettings(ShareResolution.P1080, ShareFrameRate.Fps30, true, 3000), ShareSettings.DefaultFor(StreamLimits.Relay));
        Assert.Equal(new ShareSettings(ShareResolution.P1080, ShareFrameRate.Fps30, true, 5000), ShareSettings.DefaultFor(StreamLimits.Direct));
        Assert.Equal(new ShareSettings(ShareResolution.P720, ShareFrameRate.Fps30, true, 2500), ShareSettings.DefaultFor(new StreamLimits(720, 30, 2500)));
    }

    [Theory]
    [InlineData(ShareResolution.P540, ShareFrameRate.Fps30, true, true)]
    [InlineData(ShareResolution.P540, ShareFrameRate.Fps60, true, true)]
    [InlineData(ShareResolution.P720, ShareFrameRate.Fps30, true, true)]
    [InlineData(ShareResolution.P720, ShareFrameRate.Fps60, true, true)]
    [InlineData(ShareResolution.P1080, ShareFrameRate.Fps30, true, true)]
    [InlineData(ShareResolution.P1080, ShareFrameRate.Fps60, false, true)]
    public void Combinacoes_IguaisAosLimitesDeCadaCaminho(ShareResolution resolution, ShareFrameRate frameRate, bool relay, bool direct)
    {
        foreach (var (limits, expected) in new[] { (StreamLimits.Relay, relay), (StreamLimits.Direct, direct) })
        {
            var settings = new ShareSettings(resolution, frameRate, true, limits.MaxBitrateKbps);

            Assert.Equal(expected, ShareSettings.IsAvailable(resolution, frameRate, limits));
            Assert.Equal(expected, ScreenShareLimits.IsValid(new ScreenShareStart(settings.Height * 16 / 9, settings.Height, settings.Fps, settings.BitrateKbps, true), limits));
        }
    }

    [Fact]
    public void Limites_PeloServidorAte3000kbps_DiretoAte5000()
    {
        Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(1280, 720, 30, 3000, true)));
        Assert.False(ScreenShareLimits.IsValid(new ScreenShareStart(1280, 720, 30, 3100, true)));
        Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(1920, 1080, 30, 3000, true)));
        Assert.False(ScreenShareLimits.IsValid(new ScreenShareStart(1920, 1080, 60, 3000, true)));
        Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(960, 540, 30, 3000, true))); // janela pequena em 720p
        Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(1920, 1080, 60, 5000, true), StreamLimits.Direct));
        Assert.False(ScreenShareLimits.IsValid(new ScreenShareStart(1920, 1080, 60, 6000, true), StreamLimits.Direct));
    }

    [Fact]
    public void LimitesDaApi_NoJson_ComECombinacoes_OuNoFormatoAntigo()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var current = System.Text.Json.JsonSerializer.Deserialize<ServerLimitsDto>(
            """{"relay":{"maxHeight":1080,"maxFrameRate":60,"maxBitrateKbps":3000,"profiles":[{"height":720,"frameRate":60},{"height":1080,"frameRate":30}]}}""", web)!;
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ServerLimitsDto>(
            """{"relay":{"maxHeight":720,"maxFrameRate":30,"maxBitrateKbps":2500}}""", web)!;

        Assert.Equal(StreamLimits.Relay, current.Relay);
        Assert.True(current.Relay.IsSane);
        Assert.False(current.Relay.AllowsQuality(1080, 60));
        Assert.Null(legacy.Relay.Profiles);
        Assert.Equal(new StreamLimits(720, 30, 2500), legacy.Relay);
        Assert.True(legacy.Relay.IsSane);
    }

    [Fact]
    public void Seletor_TrocarParaJanela_ListaJanelasSemSelecaoAutomatica()
    {
        var window = new CaptureSource(CaptureSourceKind.Window, 42, "Jogo", "jogo", 800, 600, 1234);
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => [window]);

        picker.IsWindowKind = true;

        Assert.Equal([window], picker.Sources);
        Assert.Null(picker.SelectedSource);
        Assert.False(picker.CanShare);

        picker.SelectedSource = window;
        picker.ShareAudio = false;

        Assert.Equal(new ShareRequest(window, new ShareSettings(ShareResolution.P720, ShareFrameRate.Fps30, false)), picker.BuildRequest());
        Assert.False(picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P1080).IsSelected);
    }

    [Fact]
    public async Task Compartilhar_SoNaSalaEConectado()
    {
        var vm = new GroupsViewModel(_groups, _realtime, _share, _ui);
        _groups.Add("Amigos");
        await vm.LoadAsync();
        _realtime.SetState(RealtimeState.Connected);

        Assert.False(vm.CanStartShare);
        Assert.False(vm.StartShareCommand.CanExecute(null));

        await vm.JoinCommand.ExecuteAsync(vm.Groups[0]);
        Assert.True(vm.CanStartShare);

        _realtime.SetState(RealtimeState.Reconnecting);
        Assert.False(vm.CanStartShare);
    }

    [Fact]
    public async Task Compartilhar_ComeçaComAEscolhaEGuardaAConfiguracaoParaAProximaVez()
    {
        var (vm, group) = await InRoomAsync();
        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings with { Resolution = ShareResolution.P540, FrameRate = ShareFrameRate.Fps60 });

        await vm.StartShareCommand.ExecuteAsync();

        Assert.Equal(ShareSettings.Default, _ui.Picks[0]);
        Assert.Equal([$"start:{group.Id}:540p60:3000"], _share.Calls);
        Assert.True(vm.IsSharing);
        Assert.False(vm.CanStartShare);
        Assert.Equal("Tela 1 (principal)", vm.ShareSourceText);
        Assert.Equal("960 × 540 · 60 fps · 3000 kbps · com áudio", vm.ShareDetailText);

        await vm.StopShareCommand.ExecuteAsync();

        Assert.False(vm.IsSharing);
        Assert.True(_share.Started[0].IsStopped);

        await vm.StartShareCommand.ExecuteAsync();
        Assert.Equal((ShareResolution.P540, ShareFrameRate.Fps60), (_ui.Picks[1].Resolution, _ui.Picks[1].FrameRate));
    }

    [Fact]
    public async Task Compartilhar_SemCapturaDeAudio_SegueSemSomEAvisa()
    {
        var (vm, _) = await InRoomAsync();
        _share.AudioFailure = "Não foi possível capturar o áudio: o compartilhamento segue sem som.";
        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings);

        await vm.StartShareCommand.ExecuteAsync();

        Assert.True(vm.IsSharing);
        Assert.DoesNotContain("com áudio", vm.ShareDetailText);
        Assert.Equal(_share.AudioFailure, vm.ShareMessage);

        // Sem pedir áudio na troca de configurações, o aviso some.
        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings with { ShareAudio = false });
        await vm.ChangeShareCommand.ExecuteAsync();

        Assert.Null(vm.ShareMessage);
    }

    [Fact]
    public async Task Compartilhar_Cancelado_NaoComeca()
    {
        var (vm, _) = await InRoomAsync();
        _ui.OnPick = _ => null;

        await vm.StartShareCommand.ExecuteAsync();

        Assert.Empty(_share.Calls);
        Assert.False(vm.IsSharing);
    }

    [Fact]
    public async Task Compartilhar_Falha_MostraAviso()
    {
        var (vm, _) = await InRoomAsync();
        _share.StartFailure = new InvalidOperationException("A janela escolhida foi fechada.");

        await vm.StartShareCommand.ExecuteAsync();

        Assert.False(vm.IsSharing);
        Assert.True(vm.HasShareMessage);
    }

    [Fact]
    public async Task AlterarConfiguracoes_TrocaOCompartilhamentoSemEncerrar()
    {
        var (vm, group) = await InRoomAsync();
        Assert.False(vm.CanChangeShare);
        await vm.StartShareCommand.ExecuteAsync();
        Assert.True(vm.CanChangeShare);
        var first = _share.Started[0];

        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings with { Resolution = ShareResolution.P540, FrameRate = ShareFrameRate.Fps60 });
        await vm.ChangeShareCommand.ExecuteAsync();

        // A escolha abre com a configuração e a origem atuais.
        Assert.Equal(ShareSettings.Default, _ui.Picks[1]);
        Assert.Equal(FakeScreenShareUi.Screen, _ui.PickCurrents[1]);
        Assert.Equal($"change:{group.Id}:540p60:3000", _share.Calls[1]);
        Assert.True(first.IsReplaced);
        Assert.True(vm.IsSharing);
        Assert.Null(vm.ShareMessage);
        Assert.Equal("960 × 540 · 60 fps · 3000 kbps · com áudio", vm.ShareDetailText);

        // O novo é o compartilhamento da vez: parar encerra ele.
        await vm.StopShareCommand.ExecuteAsync();
        Assert.True(_share.Started[1].IsStopped);
        Assert.False(vm.IsSharing);
    }

    [Fact]
    public async Task AlterarConfiguracoes_CanceladoOuComFalha_MantemOCompartilhamentoAtual()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        var first = _share.Started[0];

        _ui.OnPick = _ => null;
        await vm.ChangeShareCommand.ExecuteAsync();
        Assert.Single(_share.Calls);

        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings with { Resolution = ShareResolution.P540, FrameRate = ShareFrameRate.Fps60 });
        _share.StartFailure = new InvalidOperationException("Nenhum encoder H.264 aceitou a configuração.");
        await vm.ChangeShareCommand.ExecuteAsync();

        Assert.True(vm.IsSharing);
        Assert.False(first.IsStopped);
        Assert.Equal("Tela 1 (principal)", vm.ShareSourceText);
        Assert.True(vm.HasShareMessage);
    }

    [Fact]
    public async Task FimEspontaneo_MostraOMotivo()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();

        _share.Started[0].End("A janela compartilhada foi fechada.");

        Assert.False(vm.IsSharing);
        Assert.Equal("A janela compartilhada foi fechada.", vm.ShareMessage);
    }

    [Fact]
    public async Task SairDaSala_EncerraOCompartilhamento()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();

        await vm.LeaveCurrentCommand.ExecuteAsync();

        Assert.False(vm.IsSharing);
        Assert.True(_share.Started[0].IsStopped);
    }

    [Fact]
    public async Task ConexaoCai_EncerraOCompartilhamentoComAviso()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();

        _realtime.SetState(RealtimeState.Reconnecting);

        Assert.False(vm.IsSharing);
        Assert.True(_share.Started[0].IsStopped);
        Assert.Contains("conexão", vm.ShareMessage);
    }

    [Fact]
    public async Task Presenca_MarcaQuemCompartilha()
    {
        var (vm, group) = await InRoomAsync();

        RaisePresence(group, 200, ShareOf(_bruno));

        Assert.Equal([_bruno.UserId], group.SharingUserIds);
        Assert.True(group.HasShares);

        RaisePresence(group, 201);

        Assert.Empty(group.SharingUserIds);
    }

    [Fact]
    public async Task Assistir_AbreAJanelaEConecta()
    {
        var (vm, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));

        await group.WatchCommand.ExecuteAsync(_bruno);

        var viewer = Assert.Single(_ui.Open);
        Assert.Equal(_bruno.UserId, viewer.SharerId);
        Assert.Equal(StreamViewerState.Live, viewer.State);
        Assert.Equal("1280 × 720 · 30 fps · 4000 kbps", viewer.QualityText);
        Assert.Equal([$"watch:{group.Id}:{_bruno.UserId}"], _share.Calls);

        // Clicar de novo só traz a janela para a frente.
        await group.WatchCommand.ExecuteAsync(_bruno);
        Assert.Single(_ui.Open);
        Assert.Equal([viewer], _ui.Activated);
    }

    [Fact]
    public async Task Assistir_DuasTelasDaMesmaSala_AbreAsDuas()
    {
        var carla = FakeFriendsService.Create("carla", "Carla", RelationshipStatus.Friend);
        var (vm, group) = await InRoomAsync();
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id,
            [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(_bruno), FakeGroupsService.ToDto(carla)],
            200,
            [ShareOf(_bruno), ShareOf(carla)]));

        await group.WatchCommand.ExecuteAsync(_bruno);
        await group.WatchCommand.ExecuteAsync(carla);

        Assert.Equal([_bruno.UserId, carla.UserId], _ui.Open.Select(v => v.SharerId));
        Assert.All(_ui.Open, v => Assert.Equal(StreamViewerState.Live, v.State));

        // Fechar uma não mexe na outra.
        _ui.Open[0].RequestClose();
        Assert.Equal(carla.UserId, Assert.Single(_ui.Open).SharerId);
        Assert.Equal(carla.UserId, Assert.Single(vm.Viewers).SharerId);
    }

    [Fact]
    public async Task Fixar_AlternaEATroca_OTextoDoBotao()
    {
        var (_, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);
        var viewer = Assert.Single(_ui.Open);
        Assert.False(viewer.IsPinned);
        Assert.Equal(Strings.PinTooltip, viewer.PinTooltip);

        viewer.TogglePinCommand.Execute(null);
        Assert.True(viewer.IsPinned);
        Assert.Equal(Strings.UnpinTooltip, viewer.PinTooltip);

        viewer.TogglePinCommand.Execute(null);
        Assert.False(viewer.IsPinned);
    }

    [Fact]
    public async Task ConexaoCai_FechaTodasAsTelasAbertas()
    {
        var carla = FakeFriendsService.Create("carla", "Carla", RelationshipStatus.Friend);
        var (vm, group) = await InRoomAsync();
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id,
            [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(_bruno), FakeGroupsService.ToDto(carla)],
            200,
            [ShareOf(_bruno), ShareOf(carla)]));
        await group.WatchCommand.ExecuteAsync(_bruno);
        await group.WatchCommand.ExecuteAsync(carla);
        Assert.Equal(2, _ui.Open.Count);

        _realtime.SetState(RealtimeState.Reconnecting);

        Assert.Empty(_ui.Open);
        Assert.Empty(vm.Viewers);
    }

    [Fact]
    public async Task Assistir_PropriaTelaSemCompartilhar_NaoFazNada()
    {
        var (vm, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(FakeGroupsService.Me));

        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);

        Assert.Empty(_ui.Open);
        Assert.DoesNotContain(_share.Calls, c => c.StartsWith("watch"));
    }

    [Fact]
    public async Task Assistir_PropriaTela_AbrePreviaLocalSemConectar()
    {
        var (vm, group) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        RaisePresence(group, 200, ShareOf(FakeGroupsService.Me));

        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);

        var viewer = Assert.Single(_ui.Open);
        Assert.True(viewer.IsPreview);
        Assert.Equal(StreamViewerState.Live, viewer.State);
        Assert.False(viewer.HasAudio);
        Assert.Equal(Strings.ViewerOwnTitleFormat.Replace("{0}", group.Name), viewer.Title);
        Assert.Single(_share.Started[0].Previews);
        Assert.DoesNotContain(_share.Calls, c => c.StartsWith("watch"));

        // Clicar de novo só traz a prévia para a frente.
        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);
        Assert.Single(_ui.Open);
        Assert.Equal([viewer], _ui.Activated);
    }

    [Fact]
    public async Task Previa_NaoFechaComAPresencaSemOAnuncio_EFechaAoParar()
    {
        var (vm, group) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        RaisePresence(group, 200, ShareOf(FakeGroupsService.Me));
        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);

        // O anúncio ainda não chegou (ou sumiu) na presença: a prévia segue, pois é da captura local.
        RaisePresence(group, 201);
        Assert.Single(_ui.Open);

        await vm.StopShareCommand.ExecuteAsync();

        Assert.Empty(_ui.Open);
        Assert.Empty(vm.Viewers);
        Assert.Empty(_share.Watching);
    }

    [Fact]
    public async Task Previa_FimEspontaneoDaTransmissao_Fecha()
    {
        var (vm, group) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        RaisePresence(group, 200, ShareOf(FakeGroupsService.Me));
        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);

        _share.Started[0].End("A janela compartilhada foi fechada.");

        Assert.Empty(_ui.Open);
    }

    [Fact]
    public async Task Previa_TrocarConfiguracoes_ReabreNaNovaCaptura()
    {
        var (vm, group) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        RaisePresence(group, 200, ShareOf(FakeGroupsService.Me));
        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);

        await vm.ChangeShareCommand.ExecuteAsync();

        var viewer = Assert.Single(_ui.Open);
        Assert.True(viewer.IsPreview);
        Assert.Single(_share.Started[1].Previews);
    }

    [Fact]
    public async Task Previa_DaCamera_FicaJuntoDaPreviaDaTela()
    {
        var (vm, group) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id,
            [FakeGroupsService.ToDto(FakeGroupsService.Me)],
            200,
            [ShareOf(FakeGroupsService.Me), ShareOf(FakeGroupsService.Me) with { Kind = ShareKind.Camera }]));

        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);
        await group.WatchCameraCommand.ExecuteAsync(FakeGroupsService.Me);

        Assert.Equal([ShareKind.Screen, ShareKind.Camera], _ui.Open.Select(v => v.Kind));
        Assert.All(_ui.Open, v => Assert.True(v.IsPreview));

        await vm.ToggleCameraCommand.ExecuteAsync();

        Assert.Equal(ShareKind.Screen, Assert.Single(_ui.Open).Kind);
    }

    [Fact]
    public async Task Assistir_DeOutroGrupo_EntraNaSalaAntes()
    {
        var otherId = _groups.Add("Outro", _bruno);
        var (vm, group) = await InRoomAsync();
        var other = vm.Groups.Single(g => g.Id == otherId);
        _realtime.RaisePresence(new GroupPresenceDto(otherId, [FakeGroupsService.ToDto(_bruno)], 300, [ShareOf(_bruno)]));
        _realtime.OnJoin = id => new GroupPresenceDto(id, [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(_bruno)], 301, [ShareOf(_bruno)]);

        await other.WatchCommand.ExecuteAsync(_bruno);

        Assert.Equal(other, vm.CurrentGroup);
        Assert.Equal(otherId, Assert.Single(_ui.Open).GroupId);
    }

    [Fact]
    public async Task FimDoCompartilhamento_FechaATelaDeQuemAssistia()
    {
        var (vm, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);
        var viewer = _ui.Open[0];

        RaisePresence(group, 201);

        Assert.Equal(StreamViewerState.Ended, viewer.State);
        Assert.True(_share.Watching[0].IsDisposed);
        Assert.Empty(_ui.Open);
        Assert.Empty(vm.Viewers);
    }

    [Fact]
    public async Task CanalFechadoNormalmente_FechaATelaDeQuemAssistia()
    {
        var (_, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);

        _share.Watching[0].RaiseEnded(null);

        Assert.Empty(_ui.Open);
    }

    [Fact]
    public async Task CanalCaiu_ATelaFicaAbertaComOMotivo()
    {
        var (_, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);
        var viewer = _ui.Open[0];

        _share.Watching[0].RaiseEnded("A conexão de vídeo com a API central caiu.");

        Assert.Same(viewer, Assert.Single(_ui.Open));
        Assert.Equal(StreamViewerState.Ended, viewer.State);
        Assert.Equal("A conexão de vídeo com a API central caiu.", viewer.StatusText);
    }

    [Fact]
    public async Task CanalCaiuAntesDaPresenca_FechaATelaQuandoOCompartilhamentoTermina()
    {
        var (_, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);

        _share.Watching[0].RaiseEnded("A conexão de vídeo com a API central caiu.");
        RaisePresence(group, 201);

        Assert.Empty(_ui.Open);
    }

    [Fact]
    public async Task SairDaSala_FechaAsJanelasDeQuemAssistia()
    {
        var (vm, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);

        await vm.LeaveCurrentCommand.ExecuteAsync();

        Assert.Empty(_ui.Open);
        Assert.Empty(vm.Viewers);
        Assert.True(_share.Watching[0].IsDisposed);
    }

    [Fact]
    public async Task Espectador_PrimeiroQuadroTiraOAvisoDeAguardando()
    {
        var (vm, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);
        var viewer = _ui.Open[0];
        var frames = 0;
        viewer.FrameReady += (_, _, _, _) => frames++;

        Assert.True(viewer.ShowOverlay);
        _share.Watching[0].RaiseFrame();

        Assert.False(viewer.ShowOverlay);
        Assert.Equal(1, frames);
    }

    [Fact]
    public async Task Espectador_VolumeEMudoChegamAoAudio()
    {
        var (vm, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(_bruno));
        await group.WatchCommand.ExecuteAsync(_bruno);
        var viewer = _ui.Open[0];

        viewer.Volume = 0.25;
        viewer.ToggleMuteCommand.Execute(null);

        Assert.Equal(0.25f, _share.Watching[0].Volume);
        Assert.True(_share.Watching[0].IsMuted);
    }
}
