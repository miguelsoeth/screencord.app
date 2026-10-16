using ScreenCord.Contracts;
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
    public void Padrao_720p30fpsComAudioETaxaAutomatica()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);

        var request = picker.BuildRequest()!;

        Assert.Equal(FakeScreenShareUi.Screen, request.Source);
        Assert.Equal(new ShareSettings(ShareResolution.P720, ShareFrameRate.Fps30, true), request.Settings);
        Assert.Equal(3000, request.Settings.BitrateKbps);
        Assert.True(picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P720).IsSelected);
    }

    [Fact]
    public void Resolucao_1080pApareceDesligadoNoGratuito()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);

        Assert.Equal(["540p", "720p", "1080p"], picker.Resolutions.Options.Select(o => o.Label));
        Assert.Equal([true, true, false], picker.Resolutions.Options.Select(o => o.IsAvailable));

        var fullHd = picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P1080);
        Assert.Equal("Disponível no ScreenCord Pro.", fullHd.Detail);
        fullHd.IsSelected = true;

        Assert.False(fullHd.IsSelected);
        Assert.Equal(ShareResolution.P720, picker.BuildRequest()!.Settings.Resolution);
    }

    [Fact]
    public void Fps_540pPermite30Ou60_720pSo30()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);
        var fps60 = picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps60);

        // 720p: 60 fps desligado.
        Assert.False(fps60.IsAvailable);
        Assert.Equal("60 fps em 720p: Disponível no ScreenCord Pro.", fps60.Detail);
        fps60.IsSelected = true;
        Assert.Equal(ShareFrameRate.Fps30, picker.FrameRates.Selected);

        // 540p: os dois.
        picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P540).IsSelected = true;
        Assert.True(fps60.IsAvailable);
        Assert.Null(fps60.Detail);
        fps60.IsSelected = true;
        Assert.Equal(new ShareSettings(ShareResolution.P540, ShareFrameRate.Fps60, true), picker.BuildRequest()!.Settings);

        // De volta a 720p, o 60 fps marcado passa para 30.
        picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P720).IsSelected = true;
        Assert.False(fps60.IsAvailable);
        Assert.True(picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps30).IsSelected);
        Assert.Equal(new ShareSettings(ShareResolution.P720, ShareFrameRate.Fps30, true), picker.BuildRequest()!.Settings);
    }

    [Theory]
    [InlineData(ShareResolution.P1080, ShareFrameRate.Fps30)]
    [InlineData(ShareResolution.P720, ShareFrameRate.Fps60)]
    public void ConfiguracaoForaDosLimites_VoltaAoPadrao(ShareResolution resolution, ShareFrameRate frameRate)
    {
        var picker = new SharePickerViewModel(new ShareSettings(resolution, frameRate), () => [FakeScreenShareUi.Screen], () => []);

        Assert.Equal(ShareSettings.Default, picker.BuildRequest()!.Settings);
    }

    [Theory]
    [InlineData(ShareResolution.P540, ShareFrameRate.Fps30, true)]
    [InlineData(ShareResolution.P540, ShareFrameRate.Fps60, true)]
    [InlineData(ShareResolution.P720, ShareFrameRate.Fps30, true)]
    [InlineData(ShareResolution.P720, ShareFrameRate.Fps60, false)]
    [InlineData(ShareResolution.P1080, ShareFrameRate.Fps30, false)]
    [InlineData(ShareResolution.P1080, ShareFrameRate.Fps60, false)]
    public void Combinacoes_IguaisAosLimitesDaApi(ShareResolution resolution, ShareFrameRate frameRate, bool available)
    {
        var settings = new ShareSettings(resolution, frameRate);

        Assert.Equal(available, ShareSettings.IsAvailable(resolution, frameRate));
        Assert.Equal(available, ScreenShareLimits.IsValid(new ScreenShareStart(settings.Height * 16 / 9, settings.Height, settings.Fps, settings.BitrateKbps, true)));
    }

    [Fact]
    public void Limites_SemSetecentosEVinteA60fpsNemAcimaDeTresMbps()
    {
        Assert.False(ScreenShareLimits.IsValidQuality(720, 60));
        Assert.True(ScreenShareLimits.IsValidQuality(540, 30));
        Assert.True(ScreenShareLimits.IsValidQuality(406, 60)); // janela pequena em 540p
        Assert.False(ScreenShareLimits.IsValid(new ScreenShareStart(1280, 720, 30, 3500, true)));
        Assert.True(ScreenShareLimits.IsValid(new ScreenShareStart(1280, 720, 30, 3000, true)));
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
        picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P540).IsSelected = true;
        picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps60).IsSelected = true;
        picker.ShareAudio = false;

        Assert.Equal(new ShareRequest(window, new ShareSettings(ShareResolution.P540, ShareFrameRate.Fps60, false)), picker.BuildRequest());
        Assert.False(picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P720).IsSelected);
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
    public async Task Assistir_PropriaTela_NaoFazNada()
    {
        var (vm, group) = await InRoomAsync();
        RaisePresence(group, 200, ShareOf(FakeGroupsService.Me));

        await group.WatchCommand.ExecuteAsync(FakeGroupsService.Me);

        Assert.Empty(_ui.Open);
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
