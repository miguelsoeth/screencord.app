using ScreenCord.Contracts;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;

namespace ScreenCord.Tests;

/// <summary>Câmera (webcam) como origem de transmissão e a configuração do grupo que diz se ele a aceita.</summary>
public class CameraTests
{
    private static readonly CaptureSource Webcam = new(CaptureSourceKind.Camera, IntPtr.Zero, "Webcam HD", "", 0, 0, 0, "\\\\?\\usb#vid_046d&pid_0825");

    private readonly FakeGroupsService _groups = new();
    private readonly FakeGroupsRealtime _realtime = new();
    private readonly FakeScreenShareService _share = new();
    private readonly FakeScreenShareUi _ui = new();
    private readonly Person _bruno = FakeFriendsService.Create("bruno", "Bruno", RelationshipStatus.Friend);

    private async Task<(GroupsViewModel Vm, GroupItemViewModel Group)> InRoomAsync(GroupSettings? settings = null)
    {
        var id = _groups.Add("Amigos", _bruno);
        if (settings is not null)
        {
            _groups.Settings[id] = settings;
        }

        var vm = new GroupsViewModel(_groups, _realtime, _share, _ui);
        await vm.LoadAsync();
        _realtime.SetState(RealtimeState.Connected);
        var group = vm.Groups.Single(g => g.Id == id);
        await vm.JoinCommand.ExecuteAsync(group);
        return (vm, group);
    }

    /// <summary>A prévia sem câmera de verdade: a captura entrega um quadro NV12 cinza e conta quantas foram abertas e fechadas.</summary>
    private sealed class FakePreviewCapture : IScreenCapture
    {
        private bool _fresh = true;

        public event EventHandler? Closed;

        public string Name => "prévia simulada";

        public int Width => 4;

        public int Height => 2;

        public bool IsDisposed { get; private set; }

        public bool Update()
        {
            var fresh = _fresh;
            _fresh = false;
            return fresh;
        }

        public bool ReadNv12(Span<byte> destination)
        {
            destination.Fill(128);
            return true;
        }

        public void Disconnect() => Closed?.Invoke(this, EventArgs.Empty);

        public void Dispose() => IsDisposed = true;
    }

    private static CameraPreviewViewModel Preview(
        StreamLimits? limits = null, IReadOnlyList<CaptureSource>? cameras = null, Func<CaptureSource, IScreenCapture>? open = null) =>
        new(limits ?? StreamLimits.Relay, () => cameras ?? [Webcam], open ?? (_ => new FakePreviewCapture()));

    // --- escolha do modo da câmera

    private static CameraMode Mode(int index, int width, int height, double fps, bool compressed = false) => new(index, width, height, fps, compressed);

    [Fact]
    public void Modo_EscolheAMaiorAlturaQueCabe()
    {
        var modes = new[] { Mode(0, 640, 480, 30), Mode(1, 1280, 720, 30), Mode(2, 1920, 1080, 30) };

        Assert.Equal(1, CameraCapture.ChooseMode(modes, 720, 30)!.Value.Index);
        Assert.Equal(0, CameraCapture.ChooseMode(modes, 540, 30)!.Value.Index);
        Assert.Equal(2, CameraCapture.ChooseMode(modes, 1080, 30)!.Value.Index);
    }

    [Fact]
    public void Modo_TodosMaioresQueOPedido_UsaOMenor()
    {
        var modes = new[] { Mode(0, 1280, 720, 30), Mode(1, 1920, 1080, 30) };

        Assert.Equal(0, CameraCapture.ChooseMode(modes, 540, 30)!.Value.Index);
    }

    [Fact]
    public void Modo_PrefereOQueAtingeOsFps_EntreOsDeMesmaAltura()
    {
        // YUY2 em HD costuma ficar em 10 fps no USB; o MJPEG chega a 30.
        var modes = new[] { Mode(0, 1280, 720, 10), Mode(1, 1280, 720, 30, compressed: true) };

        Assert.Equal(1, CameraCapture.ChooseMode(modes, 720, 30)!.Value.Index);
    }

    [Fact]
    public void Modo_MesmosFps_PrefereOFormatoSemCompressao()
    {
        var modes = new[] { Mode(0, 1280, 720, 30, compressed: true), Mode(1, 1280, 720, 30) };

        Assert.Equal(1, CameraCapture.ChooseMode(modes, 720, 30)!.Value.Index);
    }

    [Fact]
    public void Modo_NenhumFpsServe_UsaOMaisRapido()
    {
        var modes = new[] { Mode(0, 1280, 720, 5), Mode(1, 1280, 720, 15) };

        Assert.Equal(1, CameraCapture.ChooseMode(modes, 720, 60)!.Value.Index);
    }

    [Fact]
    public void Modo_SemModos_RetornaNulo() => Assert.Null(CameraCapture.ChooseMode([], 720, 30));

    [Fact]
    public void ReducaoNv12_MantemOsPlanosNoTamanhoDeSaida()
    {
        // 4x4 → 2x2: o plano Y tem 4 amostras e o UV, 1 par.
        byte[] source = [10, 11, 12, 13, 20, 21, 22, 23, 30, 31, 32, 33, 40, 41, 42, 43, 100, 101, 102, 103, 110, 111, 112, 113];
        var destination = new byte[2 * 2 * 3 / 2];

        CameraCapture.ScaleNv12(source, 4, 4, destination, 2, 2);

        Assert.Equal([10, 12, 30, 32], destination[..4]);
        Assert.Equal([100, 101], destination[4..]);
    }

    // --- seletor de tela e janela (a câmera não está mais nele)

    [Fact]
    public void Seletor_OferecePenasTelaEJanela()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => []);

        Assert.True(picker.IsScreenKind);
        Assert.Equal([FakeScreenShareUi.Screen], picker.Sources);
        Assert.DoesNotContain(picker.Sources, s => s.IsCamera);
    }

    // --- prévia da câmera

    [Fact]
    public async Task Previa_AbreAPrimeiraCameraEPermiteAtivar()
    {
        using var preview = Preview();

        await preview.LoadAsync();
        await WaitForAsync(() => preview.HasPreview);

        Assert.Equal(Webcam, preview.SelectedCamera);
        Assert.True(preview.CanActivate);
        Assert.False(preview.HasChoice);
    }

    [Fact]
    public async Task Previa_Ativar_PedeACameraSemAudioNaQualidadeDoCaminho()
    {
        using var preview = Preview(StreamLimits.Relay);
        await preview.LoadAsync();
        await WaitForAsync(() => preview.HasPreview);

        var request = preview.BuildRequest()!;

        Assert.Equal(Webcam, request.Source);
        Assert.Equal(ShareKind.Camera, request.Source.ShareKind);
        Assert.False(request.Settings.ShareAudio);
        Assert.Equal(ShareResolution.P720, request.Settings.Resolution);
        Assert.Equal(ShareFrameRate.Fps30, request.Settings.FrameRate);
        Assert.Equal(ShareSettings.CameraBitrateKbps, request.Settings.BitrateKbps);
    }

    [Fact]
    public void Camera_QualidadeRespeitaOCaminhoDaMidia()
    {
        // Um servidor que só libera 540p: a câmera desce com ele; o teto de taxa também.
        var limits = StreamLimits.FromProfiles([new(540, 30)], 800);

        var settings = ShareSettings.ForCamera(limits);

        Assert.Equal(ShareResolution.P540, settings.Resolution);
        Assert.Equal(800, settings.BitrateKbps);
        Assert.Equal(ShareResolution.P720, ShareSettings.ForCamera(StreamLimits.Direct).Resolution);
    }

    [Fact]
    public async Task Previa_SemCameras_NaoDeixaAtivar()
    {
        using var preview = Preview(cameras: []);

        await preview.LoadAsync();

        Assert.True(preview.HasNoCameras);
        Assert.False(preview.CanActivate);
        Assert.Null(preview.BuildRequest());
    }

    [Fact]
    public async Task Previa_CameraQueNaoAbre_MostraOErroENaoDeixaAtivar()
    {
        using var preview = Preview(open: _ => throw new InvalidOperationException("em uso"));

        await preview.LoadAsync();
        await WaitForAsync(() => preview.HasError);

        Assert.False(preview.CanActivate);
        Assert.Contains("em uso por outro programa", preview.ErrorText);
    }

    [Fact]
    public async Task Previa_VariasCameras_TrocarFechaAAnteriorEAbreANova()
    {
        var other = Webcam with { Title = "Webcam 2", DeviceId = "outra" };
        var opened = new List<FakePreviewCapture>();
        using var preview = Preview(cameras: [Webcam, other], open: _ =>
        {
            var capture = new FakePreviewCapture();
            lock (opened)
            {
                opened.Add(capture);
            }

            return capture;
        });
        await preview.LoadAsync();
        await WaitForAsync(() => preview.HasPreview);

        preview.SelectedCamera = other;
        await WaitForAsync(() => opened.Count == 2 && preview.HasPreview);

        Assert.True(preview.HasChoice);
        Assert.True(opened[0].IsDisposed);
        Assert.False(opened[1].IsDisposed);
        Assert.Equal(other, preview.BuildRequest()!.Source);
    }

    [Fact]
    public async Task Previa_Descartar_FechaACameraParaATransmissaoPoderAbri_La()
    {
        FakePreviewCapture? capture = null;
        var preview = Preview(open: _ => capture = new FakePreviewCapture());
        await preview.LoadAsync();
        await WaitForAsync(() => preview.HasPreview);

        preview.Dispose();

        Assert.True(capture!.IsDisposed);
    }

    [Fact]
    public async Task Previa_CameraDesconectada_AvisaEDesligaAAtivacao()
    {
        FakePreviewCapture? capture = null;
        using var preview = Preview(open: _ => capture = new FakePreviewCapture());
        await preview.LoadAsync();
        await WaitForAsync(() => preview.HasPreview);

        capture!.Disconnect();
        await WaitForAsync(() => preview.HasError);

        Assert.False(preview.CanActivate);
        Assert.True(capture.IsDisposed);
    }

    [Fact]
    public async Task Previa_EntregaOQuadroEmBgra()
    {
        using var preview = Preview();
        await preview.LoadAsync();
        await WaitForAsync(() => preview.HasPreview);
        byte[]? bgra = null;

        Assert.True(preview.TryReadFrame(ref bgra, out var width, out var height));

        Assert.Equal((4, 2), (width, height));
        Assert.Equal(4 * 2 * 4, bgra!.Length);
        Assert.Equal(255, bgra[3]);
        // Nenhum quadro novo desde a última leitura.
        Assert.False(preview.TryReadFrame(ref bgra, out _, out _));
    }

    [Fact]
    public void Nv12ParaBgra_PretoBrancoECinza()
    {
        // 2x2: luminância 16 (preto), 235 (branco), 126 (cinza médio) e 16; croma neutro (128).
        byte[] nv12 = [16, 235, 126, 16, 128, 128];
        var bgra = new byte[2 * 2 * 4];

        Nv12ToBgra.Convert(nv12, 2, 2, bgra);

        Assert.Equal([0, 0, 0, 255], bgra[0..4]);
        Assert.Equal([255, 255, 255, 255], bgra[4..8]);
        Assert.InRange(bgra[8], 125, 131);
        Assert.Equal(bgra[8], bgra[9]);
        Assert.Equal(bgra[8], bgra[10]);
        Assert.Equal([0, 0, 0, 255], bgra[12..16]);
    }

    [Fact]
    public void Nv12ParaBgra_CromaVermelhoDaVermelho()
    {
        // Y = 81, U = 90, V = 240 é o vermelho puro em BT.601.
        byte[] nv12 = [81, 81, 81, 81, 90, 240];
        var bgra = new byte[2 * 2 * 4];

        Nv12ToBgra.Convert(nv12, 2, 2, bgra);

        Assert.InRange(bgra[2], 240, 255); // R
        Assert.InRange(bgra[1], 0, 20);    // G
        Assert.InRange(bgra[0], 0, 20);    // B
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "A condição não ficou verdadeira a tempo.");
            await Task.Delay(10);
        }
    }

    // --- botão da câmera na sala

    [Fact]
    public async Task Botao_PrevisaoCancelada_NadaComeca()
    {
        var (vm, _) = await InRoomAsync();
        _ui.OnPickCamera = _ => null;

        await vm.ToggleCameraCommand.ExecuteAsync();

        Assert.Single(_ui.CameraPicks);
        Assert.Empty(_share.Started);
        Assert.False(vm.IsCameraOn);
    }

    [Fact]
    public async Task Botao_Ativar_ComecaACameraComOsLimitesDoCaminho()
    {
        var (vm, group) = await InRoomAsync();

        await vm.ToggleCameraCommand.ExecuteAsync();

        var active = Assert.Single(_share.Started);
        Assert.Equal(ShareKind.Camera, active.Request.Source.ShareKind);
        Assert.Equal(vm.LimitsFor(group.Id), Assert.Single(_ui.CameraPicks));
        Assert.True(vm.IsCameraOn);
        Assert.False(vm.IsSharing);
        Assert.Contains($"camera:{group.Id}:720p30:{ShareSettings.CameraBitrateKbps}", _share.Calls);
    }

    [Fact]
    public async Task Botao_Desligar_ParaACamera()
    {
        var (vm, _) = await InRoomAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();

        await vm.ToggleCameraCommand.ExecuteAsync();

        Assert.False(vm.IsCameraOn);
        Assert.True(_share.Started[0].IsStopped);
        Assert.Single(_ui.CameraPicks); // desligar não abre a prévia de novo
    }

    [Fact]
    public async Task TelaECameraJuntas_PararATelaMantemACamera()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();
        Assert.True(vm.IsSharing);
        Assert.True(vm.IsCameraOn);
        var screen = _share.Started.Single(s => !s.Request.Source.IsCamera);
        var camera = _share.Started.Single(s => s.Request.Source.IsCamera);

        await vm.StopShareCommand.ExecuteAsync();

        Assert.False(vm.IsSharing);
        Assert.True(vm.IsCameraOn);
        Assert.True(screen.IsStopped);
        Assert.False(camera.IsStopped);
    }

    [Fact]
    public async Task TelaECameraJuntas_PararACameraMantemATela()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();
        var screen = _share.Started.Single(s => !s.Request.Source.IsCamera);

        await vm.ToggleCameraCommand.ExecuteAsync();

        Assert.True(vm.IsSharing);
        Assert.False(vm.IsCameraOn);
        Assert.False(screen.IsStopped);
    }

    [Fact]
    public async Task Camera_QuandoTerminaSozinha_AvisaESegueComATela()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();
        var camera = _share.Started.Single(s => s.Request.Source.IsCamera);

        camera.End("A câmera foi desconectada.");

        Assert.False(vm.IsCameraOn);
        Assert.True(vm.IsSharing);
        Assert.Equal("A câmera foi desconectada.", vm.ShareMessage);
    }

    [Fact]
    public async Task SairDaSala_EncerraTelaECamera()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();

        await vm.LeaveCurrentCommand.ExecuteAsync();

        Assert.False(vm.IsSharing);
        Assert.False(vm.IsCameraOn);
        Assert.All(_share.Started, s => Assert.True(s.IsStopped));
    }

    [Fact]
    public async Task ConexaoCaiu_EncerraTelaECameraComOAviso()
    {
        var (vm, _) = await InRoomAsync();
        await vm.StartShareCommand.ExecuteAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();

        _realtime.SetState(RealtimeState.Reconnecting);
        await Task.Delay(50);

        Assert.False(vm.IsSharing);
        Assert.False(vm.IsCameraOn);
        Assert.All(_share.Started, s => Assert.True(s.IsStopped));
    }

    [Fact]
    public async Task Botao_GrupoSemCamera_FicaDesligado_MasDesligarSempreVale()
    {
        var (vm, group) = await InRoomAsync();
        await vm.ToggleCameraCommand.ExecuteAsync();
        Assert.True(vm.CanToggleCamera);

        // O grupo deixa de aceitar câmera com ela ligada: o botão ainda deixa desligar.
        _realtime.RaiseGroupChanged(_groups.Dto(group.Id) with { Settings = new GroupSettings(true, false) });
        Assert.True(vm.CanToggleCamera);
        await vm.ToggleCameraCommand.ExecuteAsync();
        Assert.False(vm.IsCameraOn);

        // E desligada, não deixa ligar.
        Assert.False(vm.CanToggleCamera);
        Assert.False(vm.ToggleCameraCommand.CanExecute(null));
        Assert.True(vm.CanStartShare);
    }

    [Fact]
    public async Task Botao_GrupoSemTela_TelaDesligadaMasCameraLiga()
    {
        var (vm, _) = await InRoomAsync(new GroupSettings(AllowScreenShare: false, AllowCamera: true));

        Assert.False(vm.CanStartShare);
        Assert.True(vm.CanToggleCamera);
        Assert.False(vm.ShowSharingDisabled);
    }

    [Fact]
    public async Task Botao_SemSalaOuSemConexao_NaoLiga()
    {
        var id = _groups.Add("Amigos");
        var vm = new GroupsViewModel(_groups, _realtime, _share, _ui);
        await vm.LoadAsync();

        Assert.False(vm.CanToggleCamera);

        _realtime.SetState(RealtimeState.Connected);
        await vm.JoinCommand.ExecuteAsync(vm.Groups.Single(g => g.Id == id));
        Assert.True(vm.CanToggleCamera);

        _realtime.SetState(RealtimeState.Reconnecting);
        Assert.False(vm.CanToggleCamera);
    }

    [Fact]
    public async Task Botao_FalhaAoLigar_MostraOErroEPermiteTentarDeNovo()
    {
        var (vm, _) = await InRoomAsync();
        _share.StartFailure = new InvalidOperationException("câmera ocupada");

        await vm.ToggleCameraCommand.ExecuteAsync();

        Assert.False(vm.IsCameraOn);
        Assert.NotNull(vm.ShareMessage);
        Assert.True(vm.CanToggleCamera);
    }

    // --- assistir

    private static ScreenShareDto ShareOf(Person person, ShareKind kind) =>
        new(person.UserId, kind == ShareKind.Camera ? 640 : 1280, kind == ShareKind.Camera ? 360 : 720, 30, 1500, kind == ShareKind.Screen, kind);

    [Fact]
    public async Task Assistir_TelaECameraDeAlguem_SaoDuasTransmissoesEmDoisLugares()
    {
        var (vm, group) = await InRoomAsync();
        var bruno = _bruno;
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id, [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(bruno)], 200,
            [ShareOf(bruno, ShareKind.Screen), ShareOf(bruno, ShareKind.Camera)]));

        Assert.Equal([bruno.UserId], group.SharingUserIds);
        Assert.Equal([bruno.UserId], group.CameraUserIds);
        Assert.Equal(2, group.Shares.Count);

        await group.WatchCommand.ExecuteAsync(bruno);
        await group.WatchCameraCommand.ExecuteAsync(bruno);

        Assert.Equal([ShareKind.Screen, ShareKind.Camera], _ui.Open.Select(v => v.Kind));
        Assert.Contains($"watch:{group.Id}:{bruno.UserId}", _share.Calls);
        Assert.Contains($"watch:{group.Id}:{bruno.UserId}:camera", _share.Calls);
    }

    [Fact]
    public async Task Assistir_ClicarDeNovoNaCamera_TrazAJanelaJaAbertaEmVezDeAbrirOutra()
    {
        var (vm, group) = await InRoomAsync();
        var bruno = _bruno;
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id, [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(bruno)], 200,
            [ShareOf(bruno, ShareKind.Camera)]));
        await group.WatchCameraCommand.ExecuteAsync(bruno);

        await group.WatchCameraCommand.ExecuteAsync(bruno);

        Assert.Single(_ui.Open);
        Assert.Single(_ui.Activated);
        Assert.Empty(group.SharingUserIds);
        Assert.Equal([bruno.UserId], group.CameraUserIds);
    }

    [Fact]
    public async Task Assistir_CameraDesligada_FechaSoAJanelaDaCamera()
    {
        var (vm, group) = await InRoomAsync();
        var bruno = _bruno;
        var people = new[] { FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(bruno) };
        _realtime.RaisePresence(new GroupPresenceDto(group.Id, people, 200, [ShareOf(bruno, ShareKind.Screen), ShareOf(bruno, ShareKind.Camera)]));
        await group.WatchCommand.ExecuteAsync(bruno);
        await group.WatchCameraCommand.ExecuteAsync(bruno);

        _realtime.RaisePresence(new GroupPresenceDto(group.Id, people, 201, [ShareOf(bruno, ShareKind.Screen)]));

        var remaining = Assert.Single(_ui.Open);
        Assert.Equal(ShareKind.Screen, remaining.Kind);
    }

    [Fact]
    public async Task Assistir_QuemSoTemCamera_NaoApareceComoAoVivo()
    {
        var (vm, group) = await InRoomAsync();
        var bruno = _bruno;
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id, [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(bruno)], 200,
            [ShareOf(bruno, ShareKind.Camera)]));

        Assert.Empty(group.SharingUserIds);
        Assert.Equal([bruno.UserId], group.CameraUserIds);
    }

    [Fact]
    public async Task Assistir_ListaDeUmServidorSemTipo_ValeTela()
    {
        // Servidor de antes: a transmissão chega sem "kind" e conta como tela.
        var (vm, group) = await InRoomAsync();
        var bruno = _bruno;
        var old = System.Text.Json.JsonSerializer.Deserialize<ScreenShareDto>(
            $$"""{"userId":"{{bruno.UserId}}","width":1280,"height":720,"frameRate":30,"bitrateKbps":1500,"hasAudio":true}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        _realtime.RaisePresence(new GroupPresenceDto(
            group.Id, [FakeGroupsService.ToDto(FakeGroupsService.Me), FakeGroupsService.ToDto(bruno)], 200, [old]));

        Assert.Equal([bruno.UserId], group.SharingUserIds);
        Assert.Empty(group.CameraUserIds);
    }

    // --- contrato da mídia

    [Fact]
    public void Midia_Tela_UsaOEnderecoDeAntes_ECameraPedeOTipo()
    {
        var baseUri = new Uri("https://api.exemplo.com/");
        var group = Guid.NewGuid();
        var sharer = Guid.NewGuid();

        var screen = MediaContract.BuildUri(baseUri, group, sharer);
        var camera = MediaContract.BuildUri(baseUri, group, sharer, ShareKind.Camera);

        Assert.Equal($"wss://api.exemplo.com/media/{group}/{sharer}", screen.ToString());
        Assert.Equal($"wss://api.exemplo.com/media/{group}/{sharer}?kind=camera", camera.ToString());
        Assert.Equal(ShareKind.Camera, MediaContract.ParseKind("camera"));
        Assert.Equal(ShareKind.Camera, MediaContract.ParseKind("Camera"));
        Assert.Equal(ShareKind.Screen, MediaContract.ParseKind(null));
        Assert.Equal(ShareKind.Screen, MediaContract.ParseKind("lixo"));
    }

    // --- configuração do grupo na tela principal

    [Fact]
    public async Task Compartilhar_GrupoQueNaoAceitaNada_BotaoDesligadoEAvisoVisivel()
    {
        var (vm, group) = await InRoomAsync(new GroupSettings(false, false));

        Assert.False(group.AllowsSharing);
        Assert.False(vm.CanStartShare);
        Assert.False(vm.StartShareCommand.CanExecute(null));
        Assert.True(vm.ShowSharingDisabled);
    }

    [Fact]
    public async Task Configuracao_MudancaVindaDoServidor_ReligaOBotao()
    {
        var (vm, group) = await InRoomAsync(new GroupSettings(false, false));
        Assert.False(vm.CanStartShare);

        var changed = _groups.Dto(group.Id) with { Settings = new GroupSettings(true, false) };
        _realtime.RaiseGroupChanged(changed);

        Assert.True(vm.CanStartShare);
        Assert.False(vm.ShowSharingDisabled);
        Assert.True(group.AllowScreenShare);
        Assert.False(group.AllowCamera);
    }

    [Fact]
    public async Task Configuracao_DonoMarcaECaixaVaiAoServidor()
    {
        _groups.CanEdit = true;
        var (vm, group) = await InRoomAsync();
        group.IsMembersOpen = true;
        await Task.Delay(100);
        Assert.True(group.CanEditSettings);

        group.AllowCamera = false;
        await Task.Delay(50);

        Assert.Contains("settings:True:False", _groups.Calls);
        Assert.False(group.AllowCamera);
        Assert.True(group.AllowScreenShare);
        Assert.Equal(new GroupSettings(true, false), _groups.Settings[group.Id]);
    }

    [Fact]
    public async Task Configuracao_QuemNaoCriouOGrupo_NaoMuda()
    {
        _groups.CanEdit = false;
        var (vm, group) = await InRoomAsync();
        group.IsMembersOpen = true;
        await Task.Delay(100);

        group.AllowCamera = false;
        await Task.Delay(50);

        Assert.False(group.CanEditSettings);
        Assert.DoesNotContain(_groups.Calls, c => c.StartsWith("settings:"));
        Assert.True(group.Settings.AllowCamera);
    }

    [Fact]
    public async Task Configuracao_FalhaDoServidor_DesfazAMarcaEMostraOErro()
    {
        _groups.CanEdit = true;
        var (vm, group) = await InRoomAsync();
        group.IsMembersOpen = true;
        await Task.Delay(100);
        _groups.Failure = new InvalidOperationException("sem rede");

        group.AllowScreenShare = false;
        await Task.Delay(50);

        Assert.True(group.AllowScreenShare);
        Assert.NotNull(group.MembersError);
    }

    [Fact]
    public void ContratoAntigo_SemOCampoDeConfiguracao_ValeTudoLiberado()
    {
        const string json = """{"id":"3f2b1d86-5b1b-4c53-8d4a-0d7f0c5b9c11","name":"A","memberCount":1,"connectedUsers":[],"presenceVersion":1}""";

        var group = System.Text.Json.JsonSerializer.Deserialize<GroupDto>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Null(group.Settings);
        Assert.True((group.Settings ?? GroupSettings.Default).Allows(ShareKind.Camera));
        Assert.True((group.Settings ?? GroupSettings.Default).Allows(ShareKind.Screen));
    }

    [Fact]
    public void Contrato_TransmissaoSemTipo_ECompartilhamentoDeTela()
    {
        var start = System.Text.Json.JsonSerializer.Deserialize<ScreenShareStart>(
            """{"width":1280,"height":720,"frameRate":30,"bitrateKbps":2000,"hasAudio":false}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Equal(ShareKind.Screen, start.Kind);
        Assert.Equal("""{"width":1280,"height":720,"frameRate":30,"bitrateKbps":2000,"hasAudio":false,"kind":"Camera"}""",
            System.Text.Json.JsonSerializer.Serialize(start with { Kind = ShareKind.Camera }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    // --- câmera de verdade (só roda onde há uma webcam; o CI não tem)

    [Fact]
    public void CameraReal_SeExistir_EntregaQuadrosNv12NoTamanhoAnunciado()
    {
        var cameras = CaptureSources.Cameras();
        if (cameras.Count == 0)
        {
            return;
        }

        // Algumas câmeras (ex.: o sensor infravermelho do Windows Hello) não entregam vídeo comum: vale a primeira que abrir.
        IScreenCapture? opened = null;
        var failures = new List<string>();
        foreach (var camera in cameras)
        {
            try
            {
                opened = ScreenCapture.Create(camera, 540, 30);
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"{camera.Title}: {ex.Message}");
            }
        }

        Assert.True(opened is not null, "Nenhuma câmera abriu: " + string.Join(" | ", failures));
        using var capture = opened;
        var frame = new byte[capture.Width * capture.Height * 3 / 2];
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var got = false;
        while (!got && DateTime.UtcNow < deadline)
        {
            got = capture.Update() && capture.ReadNv12(frame);
            Thread.Sleep(30);
        }

        Assert.True(got, $"A câmera '{cameras[0].Title}' não entregou nenhum quadro.");
        Assert.True(capture.Height <= 540 || capture.Height > 0);
        Assert.Equal(0, capture.Width % 2);
        Assert.Equal(0, capture.Height % 2);
        Assert.Contains(frame, b => b != 0);

        // O quadro cabe no encoder H.264 com o tamanho anunciado.
        using var encoder = H264Encoder.Create(capture.Width, capture.Height, 30, 1500);
        encoder.Encode(frame, 0);
    }
}
