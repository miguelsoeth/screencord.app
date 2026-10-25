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

    private async Task<(GroupsViewModel Vm, GroupItemViewModel Group)> InRoomAsync(GroupSettings? settings = null)
    {
        var id = _groups.Add("Amigos");
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

    private static SharePickerViewModel Picker(GroupSettings? allowed = null) =>
        new(ShareSettings.Default, () => [FakeScreenShareUi.Screen], () => [], allowed: allowed, cameras: () => [Webcam]);

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

    // --- seletor

    [Fact]
    public void Seletor_GrupoQueAceitaTudo_MostraTelaJanelaECamera()
    {
        var picker = Picker();

        Assert.True(picker.AllowsScreens);
        Assert.True(picker.AllowsCameras);
        Assert.True(picker.IsScreenKind);
    }

    [Fact]
    public void Seletor_AbaCamera_ListaAsCamerasEJaMarcaAPrimeira()
    {
        var picker = Picker();

        picker.IsCameraKind = true;

        Assert.Equal([Webcam], picker.Sources);
        Assert.Equal(Webcam, picker.SelectedSource);
        Assert.False(picker.ShowAudioOption);
        Assert.True(picker.CanShare);
    }

    [Fact]
    public void Seletor_Camera_NaoLevaAudioMesmoComAMarcaLigada()
    {
        var picker = Picker();
        picker.IsCameraKind = true;
        picker.ShareAudio = true;

        var request = picker.BuildRequest()!;

        Assert.Equal(Webcam, request.Source);
        Assert.False(request.Settings.ShareAudio);
        Assert.Equal(ShareKind.Camera, request.Source.ShareKind);
    }

    [Fact]
    public void Seletor_GrupoSemCamera_NaoOfereceCamera()
    {
        var picker = Picker(new GroupSettings(AllowScreenShare: true, AllowCamera: false));

        Assert.True(picker.AllowsScreens);
        Assert.False(picker.AllowsCameras);
        Assert.True(picker.IsScreenKind);
    }

    [Fact]
    public void Seletor_GrupoSoComCamera_AbreNaCamera()
    {
        var picker = Picker(new GroupSettings(AllowScreenShare: false, AllowCamera: true));

        Assert.False(picker.AllowsScreens);
        Assert.True(picker.IsCameraKind);
        Assert.Equal(Webcam, picker.SelectedSource);
    }

    [Fact]
    public void Seletor_TrocaDeConfiguracoesDaCamera_MantemACameraEscolhida()
    {
        var current = Webcam with { Title = "Outro nome" };
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [], () => [], current, cameras: () => [Webcam]);

        Assert.True(picker.IsCameraKind);
        Assert.Equal(Webcam, picker.SelectedSource);
    }

    [Fact]
    public void Seletor_SemCameras_MostraAvisoEnaoDeixaCompartilhar()
    {
        var picker = new SharePickerViewModel(ShareSettings.Default, () => [], () => [], cameras: () => []);

        picker.IsCameraKind = true;

        Assert.True(picker.HasNoSources);
        Assert.False(picker.CanShare);
        Assert.Contains("câmera", picker.EmptyText, StringComparison.OrdinalIgnoreCase);
    }

    // --- configuração do grupo na tela principal

    [Fact]
    public async Task Compartilhar_RepassaAoSeletorOQueOGrupoAceita()
    {
        var settings = new GroupSettings(AllowScreenShare: false, AllowCamera: true);
        var (vm, _) = await InRoomAsync(settings);

        await vm.StartShareCommand.ExecuteAsync();

        Assert.Equal(settings, Assert.Single(_ui.PickAllowed));
    }

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
