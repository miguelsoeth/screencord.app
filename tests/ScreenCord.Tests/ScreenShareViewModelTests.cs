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
    public void Padrao_720p30fpsMedioComAudio()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);

        var request = picker.BuildRequest()!;

        Assert.Equal(FakeScreenShareUi.Screen, request.Source);
        Assert.Equal(new ShareSettings(ShareResolution.P720, ShareFrameRate.Fps30, 4000, true), request.Settings);
        Assert.Equal(4000, request.Settings.BitrateKbps);
        Assert.True(picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P720).IsSelected);
    }

    [Theory]
    [InlineData(2000, 2000)]
    [InlineData(4600, 4500)]
    [InlineData(4750, 5000)]
    [InlineData(7000, 7000)]
    [InlineData(500, 2000)]
    [InlineData(9000, 7000)]
    public void Slide_DeQuinhentosEmQuinhentosEntreDoisESeteMil(double value, int kbps)
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);

        picker.BitrateKbps = (int)value;

        Assert.Equal(kbps, picker.BitrateKbps);
        Assert.Equal($"{kbps} kbps", picker.BitrateText);
        Assert.Equal(kbps, picker.BuildRequest()!.Settings.BitrateKbps);
        Assert.True(ScreenShareLimits.IsValidBitrate(kbps));
    }

    [Fact]
    public void Slide_TemDezPassos()
    {
        var valid = Enumerable.Range(0, 20_000).Where(ScreenShareLimits.IsValidBitrate).ToList();

        Assert.Equal(11, valid.Count);
        Assert.Equal(2000, valid[0]);
        Assert.Equal(7000, valid[^1]);
        Assert.All(valid.Zip(valid.Skip(1)), pair => Assert.Equal(500, pair.Second - pair.First));
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
        picker.Resolutions.Options.Single(o => o.Value == ShareResolution.P1080).IsSelected = true;
        picker.FrameRates.Options.Single(o => o.Value == ShareFrameRate.Fps60).IsSelected = true;
        picker.BitrateKbps = 6500;
        picker.ShareAudio = false;

        Assert.Equal(new ShareRequest(window, new ShareSettings(ShareResolution.P1080, ShareFrameRate.Fps60, 6500, false)), picker.BuildRequest());
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
        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings with { Resolution = ShareResolution.P1080, FrameRate = ShareFrameRate.Fps60 });

        await vm.StartShareCommand.ExecuteAsync();

        Assert.Equal(ShareSettings.Default, _ui.Picks[0]);
        Assert.Equal([$"start:{group.Id}:1080p60:4000"], _share.Calls);
        Assert.True(vm.IsSharing);
        Assert.False(vm.CanStartShare);
        Assert.Equal("Tela 1 (principal)", vm.ShareSourceText);
        Assert.Equal("1920 × 1080 · 60 fps · 4000 kbps · com áudio", vm.ShareDetailText);

        await vm.StopShareCommand.ExecuteAsync();

        Assert.False(vm.IsSharing);
        Assert.True(_share.Started[0].IsStopped);

        await vm.StartShareCommand.ExecuteAsync();
        Assert.Equal(ShareResolution.P1080, _ui.Picks[1].Resolution);
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

        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings with { Resolution = ShareResolution.P1080, BitrateKbps = 5000 });
        await vm.ChangeShareCommand.ExecuteAsync();

        // A escolha abre com a configuração e a origem atuais.
        Assert.Equal(ShareSettings.Default, _ui.Picks[1]);
        Assert.Equal(FakeScreenShareUi.Screen, _ui.PickCurrents[1]);
        Assert.Equal($"change:{group.Id}:1080p30:5000", _share.Calls[1]);
        Assert.True(first.IsReplaced);
        Assert.True(vm.IsSharing);
        Assert.Null(vm.ShareMessage);
        Assert.Equal("1920 × 1080 · 30 fps · 5000 kbps · com áudio", vm.ShareDetailText);

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

        _ui.OnPick = settings => new ShareRequest(FakeScreenShareUi.Screen, settings with { FrameRate = ShareFrameRate.Fps60 });
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
