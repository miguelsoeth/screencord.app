using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ScreenCord.Contracts;
using ScreenCord.Local;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScreenCord.Tests;

/// <summary>Salas locais com conexões de verdade, entre duas instâncias no próprio computador.</summary>
public sealed class LocalRoomsTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "screencord-tests", Guid.NewGuid().ToString("N"));
    private readonly List<LocalRooms> _rooms = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rooms in _rooms)
        {
            await rooms.DisposeAsync();
        }

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Uma instância do app: porta livre, anúncios desligados (porta 0) e endereço anunciado no localhost.</summary>
    private LocalRooms Create(string name, RoomDiscovery? discovery = null)
    {
        var store = new LocalIdentityStore(Path.Combine(_folder, $"{name}.json"));
        var rooms = new LocalRooms(
            store,
            NullLogger<LocalRooms>.Instance,
            discovery ?? new RoomDiscovery(NullLogger.Instance, port: 0),
            port: 0,
            endpoints: port => [$"127.0.0.1:{port}"]);
        rooms.SetIdentity(name);
        _rooms.Add(rooms);
        return rooms;
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> source) => await source.Task.WaitAsync(Timeout);

    /// <summary>Ana hospeda e entra; Bruno entra pelo endereço.</summary>
    private async Task<(LocalRooms Ana, LocalRooms Bruno, Guid RoomId)> RoomWithTwoAsync()
    {
        var ana = Create("Ana");
        var bruno = Create("Bruno");
        var room = await ana.CreateRoomAsync("Jogatina");
        await ana.JoinAsync(room.Id);

        var found = await bruno.FindAsync($"127.0.0.1:{ana.ListeningPort}");
        Assert.Equal(room.Id, found.Id);
        await bruno.JoinAsync(room.Id);
        return (ana, bruno, room.Id);
    }

    [Fact]
    public async Task EntrarPeloEndereco_OsDoisSeVeemNaSala()
    {
        var ana = Create("Ana");
        var bruno = Create("Bruno");
        var room = await ana.CreateRoomAsync("Jogatina");
        var anaPresence = await ana.JoinAsync(room.Id);
        Assert.Equal(["Ana"], anaPresence.ConnectedUsers.Select(u => u.Username));

        var anaSawBruno = new TaskCompletionSource<GroupPresenceDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        ana.PresenceChanged += p =>
        {
            if (p.ConnectedUsers.Count == 2)
            {
                anaSawBruno.TrySetResult(p);
            }
        };

        await bruno.FindAsync($"127.0.0.1:{ana.ListeningPort}");
        var presence = await bruno.JoinAsync(room.Id);

        Assert.Equal(["Ana", "Bruno"], presence.ConnectedUsers.Select(u => u.Username).Order());
        Assert.Equal(room.Id, bruno.CurrentRoomId);
        Assert.True(bruno.IsLocal(room.Id));
        Assert.Equal(2, (await WaitAsync(anaSawBruno)).ConnectedUsers.Count);

        var details = bruno.Details(room.Id);
        Assert.Equal("Jogatina", details.Name);
        Assert.True(details.Members.Single(m => m.Person.DisplayName == "Ana").IsOwner);
        Assert.False(details.CanDelete);
        Assert.True(ana.Details(room.Id).CanDelete);
    }

    [Fact]
    public async Task Enderecos_QuemEntrouVeOEnderecoDaSala_QuemHospedaVeOsDele()
    {
        var (ana, bruno, roomId) = await RoomWithTwoAsync();

        var info = bruno.Info(roomId)!;
        Assert.False(info.IsHost);
        Assert.Equal([new LocalAddress($"127.0.0.1:{ana.ListeningPort}", "Este computador")], info.Addresses);

        var host = ana.Info(roomId)!;
        Assert.True(host.IsHost);
        Assert.All(host.Addresses, a => Assert.EndsWith($":{ana.ListeningPort}", a.Address));
        Assert.Equal(
            LocalNetwork.Interfaces().Select(i => i.Address.ToString()).Distinct().Order(),
            host.Addresses.Select(a => a.Address.Split(':')[0]).Order());
    }

    [Fact]
    public async Task Grupos_SalaLocal_MostraOsEnderecosNaLinha()
    {
        var ana = Create("Ana");
        var bruno = Create("Bruno");
        var room = await ana.CreateRoomAsync("Jogatina");
        await ana.JoinAsync(room.Id);
        bruno.IsLocalOnly = true;
        var vm = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(new FakeGroupsRealtime()), bruno), new FakeScreenShareService(), new FakeScreenShareUi(), bruno);
        await vm.LoadAsync();
        vm.JoinAddress = $"127.0.0.1:{ana.ListeningPort}";

        await vm.JoinByAddressCommand.ExecuteAsync();

        var item = Assert.Single(vm.Groups);
        Assert.Equal(item, vm.CurrentGroup);
        Assert.False(item.IsLocalHost);
        Assert.Equal("Endereço da sala:", item.LocalAddressesHeader);
        Assert.Equal([$"127.0.0.1:{ana.ListeningPort}"], item.LocalAddresses.Select(a => a.Address));
    }

    [Fact]
    public async Task Compartilhar_QuemAssisteRecebeDiretoDeQuemCompartilha()
    {
        var (ana, bruno, roomId) = await RoomWithTwoAsync();
        var anaSawShare = new TaskCompletionSource<GroupPresenceDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        ana.PresenceChanged += p =>
        {
            if (p.ScreenShares is { Count: 1 })
            {
                anaSawShare.TrySetResult(p);
            }
        };

        var share = await bruno.StartShareAsync(new ScreenShareStart(1920, 1080, 60, 5000, true));
        var publisher = bruno.OpenPublisher();
        var keyframeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.KeyframeRequested += () => keyframeRequested.TrySetResult();

        Assert.Equal(new ScreenShareDto(bruno.Me.Id, 1920, 1080, 60, 5000, true), share);
        Assert.Equal(share, Assert.Single((await WaitAsync(anaSawShare)).ScreenShares!));

        // Ana conecta direto no computador do Bruno; ele recebe o pedido de quadro-chave e envia.
        var viewer = await ana.WatchAsync(roomId, bruno.Me.Id);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.PacketReceived += (type, flags, _, payload) =>
        {
            if (type == MediaContract.Video && (flags & MediaContract.KeyframeFlag) != 0)
            {
                received.TrySetResult(payload.ToArray());
            }
        };
        viewer.Start();
        await keyframeRequested.Task.WaitAsync(Timeout);

        publisher.SendVideo(new EncodedFrame([9, 9, 9], IsKeyframe: false, 0), 1);
        publisher.SendVideo(new EncodedFrame([1, 2, 3], IsKeyframe: true, 0), 2);

        Assert.Equal([1, 2, 3], await WaitAsync(received));
        await viewer.DisposeAsync();
    }

    [Fact]
    public async Task Assistir_ComAChaveErrada_EhRecusado()
    {
        var (ana, bruno, roomId) = await RoomWithTwoAsync();
        await bruno.StartShareAsync(new ScreenShareStart(960, 540, 30, 1500, false));
        await Task.Delay(200);

        var share = Assert.Single(ana.Rooms.Single(r => r.Id == roomId).ScreenShares!);
        var error = await Assert.ThrowsAsync<LocalNetworkException>(() =>
            MediaSocket.ConnectDirectAsync([$"127.0.0.1:{bruno.ListeningPort}"], Guid.NewGuid(), "chave-errada-0000000000", bruno.Fingerprint!, 1500, "Bruno", CancellationToken.None));

        Assert.Contains("Firewall do Windows", error.Message);
        Assert.Equal(1500, share.BitrateKbps);
    }

    [Fact]
    public async Task EntrarPelaLista_SoAceitaOCertificadoAnunciado()
    {
        var ana = Create("Ana");
        var discovery = new RoomDiscovery(NullLogger.Instance, port: 0);
        var bruno = Create("Bruno", discovery);
        var room = await ana.CreateRoomAsync("Jogatina");
        await ana.JoinAsync(room.Id);
        byte[] Beacon(long version, string fingerprint) =>
            RoomBeacon.Serialize(new RoomBeacon(room.Id, "Jogatina", ana.ListeningPort!.Value, version, [ana.Me], fingerprint));

        // Outro computador respondendo no endereço da sala: o certificado não é o anunciado.
        discovery.Receive(Beacon(1, new string('a', LocalCertificate.FingerprintLength)), IPAddress.Loopback);
        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.JoinAsync(room.Id));
        Assert.Contains("certificado não confere", error.Message);
        Assert.Null(bruno.CurrentRoomId);

        discovery.Receive(Beacon(2, ana.Fingerprint!), IPAddress.Loopback);
        var presence = await bruno.JoinAsync(room.Id);
        Assert.Equal(["Ana", "Bruno"], presence.ConnectedUsers.Select(u => u.Username).Order());
    }

    [Fact]
    public async Task Conexao_SemTls_NaoEhAtendida()
    {
        var ana = Create("Ana");
        var room = await ana.CreateRoomAsync("Jogatina");
        await ana.JoinAsync(room.Id);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ana.ListeningPort!.Value);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /room HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"));

        var buffer = new byte[64];
        int read;
        try
        {
            read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.DoesNotContain("HTTP/1.1", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task QuemHospedaSai_ASalaAcabaParaOsOutros()
    {
        var (ana, bruno, roomId) = await RoomWithTwoAsync();
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        bruno.RoomEnded += (_, reason) => ended.TrySetResult(reason);
        bruno.RoomRemoved += id => removed.TrySetResult(id);

        await ana.LeaveAsync();

        Assert.Equal("Quem hospedava saiu, e a sala foi encerrada.", await WaitAsync(ended));
        Assert.Equal(roomId, await WaitAsync(removed));
        Assert.Null(bruno.CurrentRoomId);
        Assert.DoesNotContain(ana.Rooms, r => r.Id == roomId);
    }

    [Fact]
    public async Task EnderecoSemSala_MensagemExplicaOQueConferir()
    {
        var bruno = Create("Bruno");

        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.FindAsync("127.0.0.1:1"));

        Assert.Contains("mesma rede", error.Message);
        Assert.Equal(error.Message, ScreenCord.Infrastructure.ErrorMessages.For(error));
        Assert.Contains("Firewall do Windows", error.Message);
        await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.FindAsync("isso não é um ip"));
    }

    [Fact]
    public async Task Conexao_RepassaSoAsSalasLocais_EContaLocalFicaSempreConectada()
    {
        var api = new FakeGroupsRealtime();
        var ana = Create("Ana");
        var routed = new RoutedGroupsRealtime(new ApiRealtime(api), ana);
        routed.Start();
        api.SetState(RealtimeState.Connected);
        var room = await ana.CreateRoomAsync("Sala");

        await routed.JoinAsync(room.Id);

        Assert.Equal(room.Id, ana.CurrentRoomId);
        Assert.Contains("leave", api.Calls);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("join:", StringComparison.Ordinal));

        // A API cai: dentro da sala local, continua tudo conectado.
        api.SetState(RealtimeState.Reconnecting);
        Assert.Equal(RealtimeState.Connected, routed.State);

        var apiGroup = Guid.NewGuid();
        await routed.JoinAsync(apiGroup);
        Assert.Null(ana.CurrentRoomId);
        Assert.Contains($"join:{apiGroup}", api.Calls);
    }

    [Fact]
    public async Task ContaLocal_NaoUsaAApi()
    {
        var api = new FakeGroupsRealtime();
        var ana = Create("Ana");
        ana.IsLocalOnly = true;
        var routed = new RoutedGroupsRealtime(new ApiRealtime(api), ana);

        routed.Start();

        Assert.Equal(0, api.StartCount);
        Assert.Equal(RealtimeState.Connected, routed.State);
        await Assert.ThrowsAsync<LocalNetworkException>(() => routed.JoinAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Grupos_CriarSalaLocal_EntraNelaEOfereceAQualidadeDireta()
    {
        var api = new FakeGroupsRealtime();
        var ana = Create("Ana");
        ana.IsLocalOnly = true;
        var ui = new FakeScreenShareUi();
        var vm = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(api), ana), new FakeScreenShareService(), ui, ana);
        await vm.LoadAsync();

        Assert.True(vm.NewGroupIsLocal);
        Assert.False(vm.CanCreateGroup);
        vm.StartCreateCommand.Execute(null);
        vm.NewGroupName = "Jogatina";
        await vm.CreateCommand.ExecuteAsync();

        var room = Assert.Single(vm.Groups);
        Assert.True(room.IsLocal);
        Assert.True(room.IsLocalHost);
        Assert.Equal(room, vm.CurrentGroup);
        Assert.Equal(StreamLimits.Direct, vm.LimitsFor(room.Id));

        await vm.StartShareCommand.ExecuteAsync();

        Assert.Equal(StreamLimits.Direct, Assert.Single(ui.PickLimits));
    }

    [Fact]
    public async Task Grupos_DaApi_UsamOsLimitesDoServidor()
    {
        var groups = new FakeGroupsService();
        var ui = new FakeScreenShareUi();
        var id = groups.Add("Amigos", FakeFriendsService.Create("bruno", "Bruno", RelationshipStatus.Friend));
        var realtime = new FakeGroupsRealtime();
        var vm = new GroupsViewModel(groups, realtime, new FakeScreenShareService(), ui, Create("Ana"));
        await vm.LoadAsync();
        realtime.SetState(RealtimeState.Connected);
        await vm.JoinCommand.ExecuteAsync(vm.Groups.Single(g => g.Id == id));

        await vm.StartShareCommand.ExecuteAsync();

        Assert.Equal(StreamLimits.Relay, Assert.Single(ui.PickLimits));
    }

    // --- o que a sala aceita transmitir (tela e câmera)

    [Fact]
    public async Task Configuracao_PadraoAceitaTelaECamera_ECameraChegaAQuemAssiste()
    {
        var (ana, bruno, roomId) = await RoomWithTwoAsync();
        var anaSawShare = new TaskCompletionSource<GroupPresenceDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        ana.PresenceChanged += p =>
        {
            if (p.ScreenShares is { Count: 1 })
            {
                anaSawShare.TrySetResult(p);
            }
        };

        var share = await bruno.StartShareAsync(new ScreenShareStart(1280, 720, 30, 1500, false, ShareKind.Camera));

        Assert.Equal(ShareKind.Camera, share.Kind);
        Assert.Equal(ShareKind.Camera, Assert.Single((await WaitAsync(anaSawShare)).ScreenShares!).Kind);
        Assert.Equal(GroupSettings.Default, ana.Details(roomId).Settings);
        Assert.True(ana.Details(roomId).CanEdit);
        Assert.False(bruno.Details(roomId).CanEdit);
    }

    [Fact]
    public async Task Configuracao_SalaSemCamera_RecusaACameraDeQuemEntrou()
    {
        var (ana, bruno, roomId) = await RoomWithTwoAsync();
        var seen = new TaskCompletionSource<GroupDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        bruno.RoomChanged += room =>
        {
            if (room.Id == roomId && room.Settings is { AllowCamera: false })
            {
                seen.TrySetResult(room);
            }
        };

        await ana.UpdateSettingsAsync(roomId, new GroupSettings(AllowScreenShare: true, AllowCamera: false));
        await WaitAsync(seen);

        var error = await Assert.ThrowsAsync<LocalNetworkException>(
            () => bruno.StartShareAsync(new ScreenShareStart(1280, 720, 30, 1500, false, ShareKind.Camera)));
        Assert.Contains("câmera", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await bruno.StartShareAsync(new ScreenShareStart(1280, 720, 30, 1500, false)));
    }

    [Fact]
    public async Task Configuracao_DesligarOTipoEmUso_EncerraATransmissaoDeQuemEntrou()
    {
        var (ana, bruno, roomId) = await RoomWithTwoAsync();
        var anaSawShare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var anaSawEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ana.PresenceChanged += p =>
        {
            if (p.ScreenShares is { Count: 1 })
            {
                anaSawShare.TrySetResult();
            }
            else if (anaSawShare.Task.IsCompleted && p.ScreenShares is not { Count: > 0 })
            {
                anaSawEnd.TrySetResult();
            }
        };
        await bruno.StartShareAsync(new ScreenShareStart(1280, 720, 30, 1500, false, ShareKind.Camera));
        var publisher = bruno.OpenPublisher();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Closed += _ => closed.TrySetResult();
        await anaSawShare.Task.WaitAsync(Timeout);

        await ana.UpdateSettingsAsync(roomId, new GroupSettings(AllowScreenShare: true, AllowCamera: false));

        await anaSawEnd.Task.WaitAsync(Timeout);
        // O computador do Bruno também para de capturar: o canal de mídia dele fecha.
        await closed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Configuracao_DesligarOTipoEmUso_EncerraATransmissaoDeQuemHospeda()
    {
        var (ana, _, roomId) = await RoomWithTwoAsync();
        await ana.StartShareAsync(new ScreenShareStart(1280, 720, 30, 1500, false, ShareKind.Camera));
        var publisher = ana.OpenPublisher();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Closed += _ => closed.TrySetResult();

        await ana.UpdateSettingsAsync(roomId, new GroupSettings(AllowScreenShare: true, AllowCamera: false));

        await closed.Task.WaitAsync(Timeout);
        Assert.Empty(ana.Rooms.Single(r => r.Id == roomId).ScreenShares ?? []);
    }

    [Fact]
    public async Task Configuracao_SoQuemHospedaMuda()
    {
        var (_, bruno, roomId) = await RoomWithTwoAsync();

        await Assert.ThrowsAsync<LocalNetworkException>(
            () => bruno.UpdateSettingsAsync(roomId, new GroupSettings(false, false)));
    }
}

/// <summary>Usar sem conta: só o nome, direto para as salas locais, sem Discord nem API.</summary>
public sealed class LocalLoginTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screencord-tests", Guid.NewGuid().ToString("N"), "local-profile.json");
    private readonly FakeSessionManager _session = new();
    private readonly FakeProfileService _profile = new();
    private readonly FakeGroupsRealtime _api = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (MainViewModel Vm, LocalRooms Rooms, LocalIdentityStore Store) Create()
    {
        var store = new LocalIdentityStore(_path);
        var rooms = new LocalRooms(store, NullLogger<LocalRooms>.Instance, new RoomDiscovery(NullLogger.Instance, port: 0), port: 0);
        var groups = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(_api), rooms), new FakeScreenShareService(), new FakeScreenShareUi(), rooms);
        var vm = new MainViewModel(_session, _profile, new FriendsViewModel(new FakeFriendsService()), groups, NullLogger<MainViewModel>.Instance, rooms, store);
        return (vm, rooms, store);
    }

    [Fact]
    public async Task UsarSemConta_EntraSoComONome_ESemUsarAApi()
    {
        var (vm, rooms, store) = Create();
        await vm.InitializeAsync();
        Assert.True(vm.ShowLogin);

        vm.StartLocalCommand.Execute(null);
        Assert.True(vm.IsLocalForm);
        Assert.False(vm.ShowConnectButton);
        vm.LocalName = "  Ana   Souza ";
        await vm.ConfirmLocalCommand.ExecuteAsync();

        Assert.True(vm.IsConnected);
        Assert.True(vm.IsLocalSession);
        Assert.Equal("Ana Souza", vm.Profile!.DisplayName);
        Assert.Equal("Sem conta · só salas locais", vm.AccountStatusText);
        Assert.True(rooms.IsLocalOnly);
        Assert.Equal("Ana Souza", rooms.Me.Name);
        Assert.Equal(0, _api.StartCount);
        Assert.Equal(0, _profile.Calls);
        Assert.Equal(new LocalProfile(rooms.Me.Id, "Ana Souza", SignedIn: true), store.Load());
        await rooms.StopAsync();
    }

    [Fact]
    public async Task UsarSemConta_SemNome_PedeONome()
    {
        var (vm, rooms, _) = Create();
        vm.StartLocalCommand.Execute(null);
        vm.LocalName = "   ";

        await vm.ConfirmLocalCommand.ExecuteAsync();

        Assert.False(vm.IsConnected);
        Assert.True(vm.HasError);
        await rooms.StopAsync();
    }

    [Fact]
    public async Task AoAbrirDeNovo_VoltaParaAsSalasLocais_EDesconectarEsquece()
    {
        var (first, firstRooms, _) = Create();
        first.StartLocalCommand.Execute(null);
        first.LocalName = "Ana";
        await first.ConfirmLocalCommand.ExecuteAsync();
        await firstRooms.StopAsync();

        var (vm, rooms, store) = Create();
        await vm.InitializeAsync();

        Assert.True(vm.IsLocalSession);
        Assert.Equal("Ana", vm.Profile!.DisplayName);
        Assert.Equal(firstRooms.Me.Id, rooms.Me.Id);

        vm.DisconnectCommand.Execute(null);
        await vm.ConfirmDisconnectCommand.ExecuteAsync();

        Assert.False(vm.IsConnected);
        Assert.False(vm.IsLocalSession);
        Assert.False(rooms.IsLocalOnly);
        Assert.False(store.Load().SignedIn);
    }
}

public class LocalNetworkTests
{
    [Theory]
    [InlineData("192.168.0.10", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("25.4.5.6", true)]    // Hamachi
    [InlineData("26.4.5.6", true)]    // Radmin
    [InlineData("100.100.1.1", true)] // Tailscale
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", true)]
    public void EnderecosLocais_RedesDomesticasEVpnsDeJogo(string address, bool local)
    {
        Assert.Equal(local, LocalNetwork.IsLocal(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("25.1.2.3", "25.1.2.3", LocalNetwork.RoomPort)]
    [InlineData(" 192.168.0.10:5000 ", "192.168.0.10", 5000)]
    [InlineData("pc-da-ana:47830", "pc-da-ana", 47830)]
    public void Endereco_ComOuSemPorta(string text, string host, int port)
    {
        Assert.True(LocalNetwork.TryParseAddress(text, out var parsedHost, out var parsedPort));
        Assert.Equal((host, port), (parsedHost, parsedPort));
    }

    [Theory]
    [InlineData("")]
    [InlineData("25.1.2.3:99999")]
    [InlineData("http://25.1.2.3")]
    [InlineData("dois nomes")]
    public void Endereco_Invalido(string text)
    {
        Assert.False(LocalNetwork.TryParseAddress(text, out _, out _));
    }

    [Theory]
    [InlineData("25.1.2.3", "Hamachi")]
    [InlineData("26.1.2.3", "Radmin")]
    [InlineData("100.101.1.2", "Tailscale")]
    [InlineData("192.168.0.10", "Rede local")]
    [InlineData("127.0.0.1", "Este computador")]
    public void NomeDaRede_PeloEndereco(string address, string network)
    {
        Assert.Equal(network, LocalNetwork.NetworkName(IPAddress.Parse(address)));
    }

    [Fact]
    public void Endereco_SemPortaQuandoEhAPadrao()
    {
        Assert.Equal("25.1.2.3", LocalNetwork.FormatAddress("25.1.2.3", LocalNetwork.RoomPort));
        Assert.Equal("25.1.2.3:5000", LocalNetwork.FormatAddress("25.1.2.3", 5000));
    }

    [Fact]
    public void Broadcast_DaSubRede()
    {
        Assert.Equal(IPAddress.Parse("192.168.0.255"), LocalNetwork.BroadcastOf(IPAddress.Parse("192.168.0.10"), 24));
        Assert.Equal(IPAddress.Parse("25.255.255.255"), LocalNetwork.BroadcastOf(IPAddress.Parse("25.1.2.3"), 8));
    }
}

public class RoomDiscoveryTests
{
    private static byte[] Beacon(Guid id, long version = 1, string name = "Jogatina") =>
        RoomBeacon.Serialize(new RoomBeacon(id, name, 47830, version, [new LocalMember(Guid.NewGuid(), "Ana")], Fingerprint));

    private static readonly string Fingerprint = new('a', LocalCertificate.FingerprintLength);

    [Fact]
    public void Anuncio_ApareceEDesapareceQuandoParaDeChegar()
    {
        var time = new FakeTimeProvider();
        using var discovery = new RoomDiscovery(NullLogger.Instance, time, port: 0);
        var seen = new List<DiscoveredRoom>();
        var lost = new List<Guid>();
        discovery.RoomSeen += seen.Add;
        discovery.RoomLost += lost.Add;
        var id = Guid.NewGuid();

        discovery.Receive(Beacon(id), IPAddress.Parse("25.1.2.3"));

        var room = Assert.Single(discovery.Rooms);
        Assert.Equal(("Jogatina", "25.1.2.3", 47830), (room.Beacon.Name, room.Host, room.Beacon.Port));
        Assert.Single(seen);

        time.Now += TimeSpan.FromSeconds(5);
        discovery.Expire();
        Assert.Single(discovery.Rooms);

        time.Now += TimeSpan.FromSeconds(5);
        discovery.Expire();
        Assert.Empty(discovery.Rooms);
        Assert.Equal([id], lost);
    }

    [Fact]
    public void Anuncio_InvalidoOuDeOutroPrograma_EhIgnorado()
    {
        using var discovery = new RoomDiscovery(NullLogger.Instance, new FakeTimeProvider(), port: 0);

        discovery.Receive(Encoding.UTF8.GetBytes("{\"kind\":\"outro-jogo\"}"), IPAddress.Loopback);
        discovery.Receive(Encoding.UTF8.GetBytes("não é json"), IPAddress.Loopback);
        discovery.Receive(Beacon(Guid.NewGuid(), name: "   "), IPAddress.Loopback);
        discovery.Receive(RoomBeacon.Serialize(new RoomBeacon(Guid.NewGuid(), "Sala", 47830, 1, [], Fingerprint, Protocol: 99)), IPAddress.Loopback);
        discovery.Receive(RoomBeacon.Serialize(new RoomBeacon(Guid.NewGuid(), "Sala", 47830, 1, [], "sem-certificado")), IPAddress.Loopback);

        Assert.Empty(discovery.Rooms);
    }

    [Fact]
    public void Anuncio_MaisAntigoNaoSobrescreveOMaisNovo()
    {
        using var discovery = new RoomDiscovery(NullLogger.Instance, new FakeTimeProvider(), port: 0);
        var id = Guid.NewGuid();

        discovery.Receive(Beacon(id, version: 5, name: "Nova"), IPAddress.Parse("25.1.2.3"));
        discovery.Receive(Beacon(id, version: 3, name: "Velha"), IPAddress.Parse("26.1.2.3"));

        var room = Assert.Single(discovery.Rooms);
        Assert.Equal(("Nova", "25.1.2.3"), (room.Beacon.Name, room.Host));
    }
}

public class LocalCertificateTests
{
    [Fact]
    public void Certificado_TemChavePrivadaEImpressaoDigitalSha256()
    {
        using var certificate = LocalCertificate.Create();

        Assert.True(certificate.Certificate.HasPrivateKey);
        Assert.True(LocalCertificate.IsValidFingerprint(certificate.Fingerprint));
        Assert.Equal(certificate.Fingerprint, LocalCertificate.FingerprintOf(certificate.Certificate));
    }

    [Fact]
    public void Validacao_ComImpressaoDigital_SoAceitaAqueleCertificado()
    {
        using var mine = LocalCertificate.Create();
        using var other = LocalCertificate.Create();
        var rejections = 0;
        var pinned = LocalCertificate.Validator(mine.Fingerprint.ToUpperInvariant(), () => rejections++);
        var any = LocalCertificate.Validator(null);

        Assert.True(pinned(this, mine.Certificate, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(pinned(this, other.Certificate, null, System.Net.Security.SslPolicyErrors.None));
        Assert.False(pinned(this, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Equal(2, rejections);
        Assert.True(any(this, other.Certificate, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
