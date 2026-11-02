using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Local;
using ScreenCord.Media;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using ScreenCord.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScreenCord.Tests;

/// <summary>Formato do link de convite: ida e volta e entradas inválidas ou adulteradas.</summary>
public class RoomInviteFormatTests
{
    private static readonly string Fingerprint = new('a', LocalCertificate.FingerprintLength);

    private static RoomInvite Sample(string name = "Jogatina") =>
        new(Guid.NewGuid(), name, ["203.0.113.7:47830", "192.168.0.10:47830", "25.1.2.3:47830"], Fingerprint, RoomInvite.NewToken());

    [Fact]
    public void IdaEVolta_MantemTodosOsCampos()
    {
        var invite = Sample("Jogatina de sábado");

        var link = invite.ToLink();
        var parsed = RoomInvite.Parse(link);

        Assert.StartsWith("screencord://join?v=1&", link);
        Assert.Equal(invite.RoomId, parsed.RoomId);
        Assert.Equal("Jogatina de sábado", parsed.Name);
        Assert.Equal(invite.Addresses, parsed.Addresses);
        Assert.Equal(invite.Fingerprint, parsed.Fingerprint);
        Assert.Equal(invite.Token, parsed.Token);
        Assert.Equal(LocalMessage.CurrentProtocol, parsed.Protocol);
    }

    [Fact]
    public void IdaEVolta_AceitaABarraQueAlgunsNavegadoresAcrescentam()
    {
        var invite = Sample();
        var link = invite.ToLink().Replace("join?", "join/?");

        Assert.Equal(invite.Token, RoomInvite.Parse("  " + link + "  ").Token);
    }

    [Fact]
    public void Token_TemPeloMenos128BitsEMudaACadaVez()
    {
        var token = RoomInvite.NewToken();

        Assert.Equal(43, token.Length); // 32 bytes em base64url
        Assert.True(RoomInvite.IsValidToken(token));
        Assert.NotEqual(token, RoomInvite.NewToken());
        Assert.False(RoomInvite.IsValidToken(new string('a', RoomInvite.MinTokenLength - 1)));
        Assert.False(RoomInvite.IsValidToken("token com espaço e /barra/ 0000000000"));
    }

    [Fact]
    public void Nome_SemCaracteresDeControleNemDeDirecao_ENoTamanhoMaximo()
    {
        Assert.Equal("Sala da Ana", RoomInvite.SanitizeName("Sala\u0000 da\u202E  Ana\n"));
        Assert.Equal(GroupsHubContract.MaxNameLength, RoomInvite.SanitizeName(new string('x', 500)).Length);

        var link = Sample().ToLink().Replace("n=Jogatina", "n=" + Uri.EscapeDataString("Jo\u202Egatina\r\n"));
        Assert.Equal("Jogatina", RoomInvite.Parse(link).Name);
    }

    public static TheoryData<string> Malformados() => new()
    {
        "",
        "screencord:",
        "https://join?v=1",
        "screencord://outra?v=1",
        "screencord://join",
        "screencord://join?",
        "screencord://join?v=1",
        "screencord://join?v=um",
        "screencord://join?v=1&v=1",
        "screencord://" + new string('a', RoomInvite.MaxLinkLength),
    };

    [Theory]
    [MemberData(nameof(Malformados))]
    public void Malformado_DizParaPedirUmConviteNovo(string link)
    {
        var error = Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(link));

        Assert.Equal(LocalFailure.InvalidInvite, error.Failure);
        Assert.Contains("Peça um convite novo", error.Message);
    }

    [Theory]
    [InlineData("a", "1.2.3:47830")]
    [InlineData("a", "1.2.3.4")]
    [InlineData("a", "1.2.3.4:0")]
    [InlineData("a", "1.2.3.4:70000")]
    [InlineData("a", "0x7f.0.0.1:47830")]
    [InlineData("a", "1:47830")]
    [InlineData("a", "256.1.1.1:47830")]
    [InlineData("a", "0.0.0.0:47830")]
    [InlineData("a", "224.0.0.1:47830")]
    [InlineData("a", "255.255.255.255:47830")]
    [InlineData("a", "pc-da-ana:47830")]
    [InlineData("a", "[::1]:47830")]
    [InlineData("a", "1.1.1.1:1,1.1.1.2:1,1.1.1.3:1,1.1.1.4:1,1.1.1.5:1,1.1.1.6:1,1.1.1.7:1,1.1.1.8:1,1.1.1.9:1")]
    [InlineData("f", "abc")]
    [InlineData("f", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("t", "curto")]
    [InlineData("t", "com+mais/que=base64url0000000")]
    [InlineData("r", "nao-e-guid")]
    [InlineData("r", "00000000000000000000000000000000")]
    [InlineData("n", "%20%0A")]
    [InlineData("p", "-1")]
    public void CampoAdulterado_EhRecusado(string field, string value)
    {
        var link = Replace(Sample().ToLink(), field, value);

        var error = Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(link));

        Assert.Equal(LocalFailure.InvalidInvite, error.Failure);
    }

    [Fact]
    public void CampoRepetido_EhRecusado_ECampoDesconhecidoEhIgnorado()
    {
        var link = Sample().ToLink();

        Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(link + "&t=" + RoomInvite.NewToken()));
        Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(link + "&T=1"));
        Assert.Equal(RoomInvite.Parse(link).Token, RoomInvite.Parse(link + "&extra=1").Token);
    }

    [Fact]
    public void FormatoMaisNovo_PedeParaAtualizar()
    {
        var link = Replace(Sample().ToLink(), "v", "2");

        var error = Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(link));

        Assert.Contains("versão mais nova", error.Message);
        Assert.Contains("Procurar atualizações", error.Message);
        Assert.Equal(LocalFailure.InvalidInvite, Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(Replace(link, "v", "0"))).Failure);
    }

    [Fact]
    public void ProtocoloDiferente_PedeParaAtualizarOAppCerto()
    {
        var link = Sample().ToLink();

        var newer = Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(Replace(link, "p", (LocalMessage.CurrentProtocol + 1).ToString())));
        var older = Assert.Throws<LocalNetworkException>(() => RoomInvite.Parse(Replace(link, "p", (LocalMessage.CurrentProtocol - 1).ToString())));

        Assert.Equal(LocalFailure.ProtocolMismatch, newer.Failure);
        Assert.Contains("Atualize o app", newer.Message);
        Assert.Equal(LocalFailure.ProtocolMismatch, older.Failure);
        Assert.Contains("Peça para atualizar", older.Message);
    }

    [Fact]
    public void Link_ReconheceSoOEsquemaDoScreenCord()
    {
        Assert.True(RoomInvite.IsInviteLink(" SCREENCORD://join?v=1"));
        Assert.False(RoomInvite.IsInviteLink("https://screencord.miguelsoeth.online"));
        Assert.False(RoomInvite.IsInviteLink(null));
        Assert.Equal("screencord://join?v=1", UrlProtocol.FindLink(["--x", "screencord://join?v=1"]));
        Assert.Null(UrlProtocol.FindLink(["--configure-firewall"]));
    }

    [Fact]
    public void Protocolo_LeOExeDaLinhaDeComandoRegistrada()
    {
        Assert.Equal(@"C:\Apps\ScreenCord.exe", UrlProtocol.RegisteredExe(UrlProtocol.CommandFor(@"C:\Apps\ScreenCord.exe")));
        Assert.Equal(@"C:\x.exe", UrlProtocol.RegisteredExe(@"C:\x.exe %1"));
        Assert.Equal("\"C:\\Apps\\ScreenCord.exe\" \"%1\"", UrlProtocol.CommandFor(@"C:\Apps\ScreenCord.exe"));
    }

    [Fact]
    public void Firewall_ConvidadosDeForaSoComAOpcaoExplicita()
    {
        Assert.Null(FirewallRule.InternetChoice([FirewallRule.CommandLineFlag]));
        Assert.True(FirewallRule.InternetChoice([FirewallRule.CommandLineFlag, FirewallRule.AllowInternetFlag]));
        Assert.False(FirewallRule.InternetChoice([FirewallRule.CommandLineFlag, FirewallRule.LocalOnlyFlag]));
    }

    [Fact]
    public void Enderecos_SemAdaptadoresVirtuais_EARedeDeCasaPrimeiro()
    {
        var none = IPAddress.None;
        (IPAddress, IPAddress, bool) Nic(string address, bool gateway) => (IPAddress.Parse(address), none, gateway);

        // Hyper-V/WSL (172.x) e VirtualBox (192.168.56.x) não têm gateway; Radmin e Tailscale entram sempre.
        var addresses = LocalNetwork.RoomAddresses(
        [
            Nic("26.25.112.14", false), Nic("100.110.145.106", false), Nic("172.31.16.1", false),
            Nic("192.168.0.113", true), Nic("192.168.56.1", false),
        ]);

        Assert.Equal(["192.168.0.113", "26.25.112.14", "100.110.145.106"], addresses.Select(a => a.ToString()));

        // Sem nenhum gateway na rede de casa (cabo direto entre dois computadores), nada some.
        Assert.Equal(2, LocalNetwork.RoomAddresses([Nic("192.168.1.2", false), Nic("10.0.0.2", false)]).Count);
    }

    /// <summary>Troca o valor de um campo do link (sem escapar: o teste escolhe o texto exato).</summary>
    internal static string Replace(string link, string field, string value)
    {
        var query = link.IndexOf('?');
        var parts = link[(query + 1)..].Split('&').Select(p => p.StartsWith(field + "=", StringComparison.Ordinal) ? $"{field}={value}" : p);
        return link[..(query + 1)] + string.Join('&', parts);
    }
}

/// <summary>IP público por STUN: a resposta do servidor e a consulta de verdade, contra um servidor no localhost.</summary>
public class StunClientTests
{
    private static readonly byte[] Transaction = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private static byte[] Response(ushort type, ushort attribute, IPAddress address, byte[]? transaction = null)
    {
        var packet = new byte[20 + 12];
        BinaryPrimitives.WriteUInt16BigEndian(packet, type);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x2112A442);
        (transaction ?? Transaction).CopyTo(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), attribute);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 8);
        packet[25] = 0x01;
        var value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28), attribute == 0x0020 ? value ^ 0x2112A442 : value);
        return packet;
    }

    [Fact]
    public void Resposta_LeOEnderecoDoXorMappedEDoMapped()
    {
        var ip = IPAddress.Parse("203.0.113.7");

        Assert.Equal(ip, StunClient.ParseResponse(Response(0x0101, 0x0020, ip), Transaction));
        Assert.Equal(ip, StunClient.ParseResponse(Response(0x0101, 0x0001, ip), Transaction));
    }

    [Fact]
    public void Resposta_DeOutraTransacaoErroOuCortada_EhIgnorada()
    {
        var ip = IPAddress.Parse("203.0.113.7");

        Assert.Null(StunClient.ParseResponse(Response(0x0101, 0x0020, ip, new byte[12]), Transaction));
        Assert.Null(StunClient.ParseResponse(Response(0x0111, 0x0020, ip), Transaction));
        Assert.Null(StunClient.ParseResponse(Response(0x0101, 0x0020, ip)[..26], Transaction));
        Assert.Null(StunClient.ParseResponse([], Transaction));
    }

    [Fact]
    public void Pedido_TemOCabecalhoDoBindingRequest()
    {
        var request = StunClient.BuildRequest(Transaction);

        Assert.Equal(20, request.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request));
        Assert.Equal(0x2112A442u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4)));
        Assert.Equal(Transaction, request[8..]);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("203.0.113.7", true)]
    [InlineData("192.168.0.10", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    public void EnderecoPublico(string address, bool expected)
    {
        Assert.Equal(expected, StunClient.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Consulta_FicaComARespostaDoServidor()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var answering = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync();
            await server.SendAsync(Response(0x0101, 0x0020, IPAddress.Parse("203.0.113.7"), request.Buffer[8..20]), request.RemoteEndPoint);
        });

        var address = await new StunClient([$"127.0.0.1:{port}"], TimeSpan.FromSeconds(5)).ResolveAsync();

        Assert.Equal(IPAddress.Parse("203.0.113.7"), address);
        await answering;
    }

    [Fact]
    public async Task Consulta_SemResposta_DesisteNoTempoLimite()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var started = DateTime.UtcNow;

        var address = await new StunClient([$"127.0.0.1:{port}", "nao-existe.invalid:3478"], TimeSpan.FromMilliseconds(500)).ResolveAsync();

        Assert.Null(address);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}

/// <summary>Bloqueio de quem erra o token ou a chave várias vezes.</summary>
public class ConnectionGuardTests
{
    private static readonly IPAddress Address = IPAddress.Parse("203.0.113.9");

    [Fact]
    public void CincoTentativasErradas_BloqueiamPorUmTempo()
    {
        var time = new FakeTimeProvider();
        var guard = new ConnectionGuard(time);
        for (var i = 1; i < ConnectionGuard.MaxFailures; i++)
        {
            Assert.False(guard.RecordFailure(Address));
        }

        Assert.False(guard.IsBlocked(Address));
        Assert.True(guard.RecordFailure(Address));
        Assert.True(guard.IsBlocked(Address));
        Assert.False(guard.IsBlocked(IPAddress.Parse("203.0.113.10")));

        time.Now += ConnectionGuard.BlockDuration + TimeSpan.FromSeconds(1);
        Assert.False(guard.IsBlocked(Address));
    }

    [Fact]
    public void TentativasEspacadas_NaoBloqueiam()
    {
        var time = new FakeTimeProvider();
        var guard = new ConnectionGuard(time);
        for (var i = 0; i < ConnectionGuard.MaxFailures * 2; i++)
        {
            Assert.False(guard.RecordFailure(Address));
            time.Now += ConnectionGuard.FailureWindow / 2 + TimeSpan.FromSeconds(1);
            if (i % 2 == 1)
            {
                time.Now += ConnectionGuard.FailureWindow;
            }
        }

        Assert.False(guard.IsBlocked(Address));
    }
}

/// <summary>Convite de verdade entre duas instâncias no próprio computador; "de fora da rede" é simulado.</summary>
public sealed class RoomInviteTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Func<IPAddress, bool> Outside = _ => false;

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

    /// <param name="isTrusted">Como esta instância vê quem conecta nela; <see cref="Outside"/> trata todos como vindos da internet.</param>
    private LocalRooms Create(string name, Func<IPAddress, bool>? isTrusted = null, IPAddress? publicAddress = null, Func<int, IReadOnlyList<string>>? endpoints = null)
    {
        var rooms = new LocalRooms(
            new LocalIdentityStore(Path.Combine(_folder, $"{name}.json")),
            NullLogger<LocalRooms>.Instance,
            new RoomDiscovery(NullLogger.Instance, port: 0),
            port: 0,
            endpoints: endpoints ?? (port => [$"127.0.0.1:{port}"]),
            publicAddress: new FakePublicAddress(publicAddress),
            isTrusted: isTrusted);
        rooms.SetIdentity(name);
        _rooms.Add(rooms);
        return rooms;
    }

    /// <summary>Ana hospeda (vendo todos como de fora da rede) e já está na sala.</summary>
    private async Task<(LocalRooms Ana, GroupDto Room)> HostAsync(Func<IPAddress, bool>? isTrusted = null)
    {
        var ana = Create("Ana", isTrusted ?? Outside);
        var room = await ana.CreateRoomAsync("Jogatina");
        await ana.JoinAsync(room.Id);
        return (ana, room);
    }

    [Fact]
    public async Task Convite_LevaOIpPublicoPrimeiro_EOsEnderecosDaRede()
    {
        var ana = Create("Ana", Outside, IPAddress.Parse("203.0.113.7"));
        var room = await ana.CreateRoomAsync("Jogatina");

        var invite = await ana.CreateInviteAsync(room.Id);

        Assert.True(invite.HasPublicAddress);
        Assert.Equal([$"203.0.113.7:{ana.ListeningPort}", $"127.0.0.1:{ana.ListeningPort}"], invite.Invite.Addresses);
        Assert.Equal(ana.Fingerprint, invite.Invite.Fingerprint);
        Assert.Equal("Jogatina", invite.Invite.Name);
        Assert.Equal(room.Id, RoomInvite.Parse(invite.Link).RoomId);
    }

    [Fact]
    public async Task Convite_SemIpPublico_SaiSoComOsEnderecosDaRede()
    {
        var (ana, room) = await HostAsync();

        var invite = await ana.CreateInviteAsync(room.Id);

        Assert.False(invite.HasPublicAddress);
        Assert.Equal([$"127.0.0.1:{ana.ListeningPort}"], invite.Invite.Addresses);
    }

    [Fact]
    public async Task Convite_SoQuemHospeda()
    {
        var (ana, room) = await HostAsync();
        var bruno = Create("Bruno");
        await bruno.FindInviteAsync((await ana.CreateInviteAsync(room.Id)).Invite);
        await bruno.JoinAsync(room.Id);

        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.CreateInviteAsync(room.Id));

        Assert.Contains("Só quem hospeda", error.Message);
    }

    [Fact]
    public async Task DeFora_ComOConvite_Entra()
    {
        var (ana, room) = await HostAsync();
        var bruno = Create("Bruno");
        var invite = await ana.CreateInviteAsync(room.Id);

        var found = await bruno.FindInviteAsync(RoomInvite.Parse(invite.Link));
        var presence = await bruno.JoinAsync(found.Id);

        Assert.Equal(room.Id, found.Id);
        Assert.Equal(["Ana", "Bruno"], presence.ConnectedUsers.Select(u => u.Username).Order());
    }

    [Fact]
    public async Task DeFora_PeloIpSemToken_EhRecusado_EDizParaPedirOConvite()
    {
        var (ana, _) = await HostAsync();
        var bruno = Create("Bruno");

        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.FindAsync($"127.0.0.1:{ana.ListeningPort}"));

        Assert.Equal(LocalFailure.TokenRejected, error.Failure);
        Assert.Contains("só se entra pelo link de convite", error.Message);
    }

    [Fact]
    public async Task NaRedeLocal_DispensaOToken()
    {
        var (ana, room) = await HostAsync(isTrusted: _ => true);
        var bruno = Create("Bruno");
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite;

        // Pelo IP, sem token nenhum, e pelo convite com um token que não é o da sala.
        Assert.Equal(room.Id, (await bruno.FindAsync($"127.0.0.1:{ana.ListeningPort}")).Id);
        var carla = Create("Carla");
        Assert.Equal(room.Id, (await carla.FindInviteAsync(invite with { Token = RoomInvite.NewToken() })).Id);
    }

    [Fact]
    public async Task GerarNovoConvite_RevogaOAnterior()
    {
        var (ana, room) = await HostAsync();
        var old = (await ana.CreateInviteAsync(room.Id)).Invite;

        var renewed = (await ana.CreateInviteAsync(room.Id, renew: true)).Invite;
        var bruno = Create("Bruno");
        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.FindInviteAsync(old));

        Assert.NotEqual(old.Token, renewed.Token);
        Assert.Equal(LocalFailure.TokenRejected, error.Failure);
        Assert.Contains("gerou um convite novo", error.Message);
        Assert.Equal(room.Id, (await bruno.FindInviteAsync(renewed)).Id);
        // Copiar de novo sem trocar mantém o mesmo link.
        Assert.Equal(renewed.Token, (await ana.CreateInviteAsync(room.Id)).Invite.Token);
    }

    [Fact]
    public async Task SalaEncerrada_OConviteDizQueAcabou()
    {
        var (ana, room) = await HostAsync();
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite;
        await ana.LeaveAsync();

        // O servidor continua aberto (Ana pode criar outra sala), mas a sala do convite não existe mais.
        var next = await ana.CreateRoomAsync("Outra");
        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => Create("Bruno").FindInviteAsync(invite));

        Assert.NotEqual(room.Id, next.Id);
        Assert.Equal(LocalFailure.RoomClosed, error.Failure);
        Assert.Contains("foi encerrada", error.Message);
    }

    [Fact]
    public async Task SemResposta_SugereAPortaNoRoteadorOFirewallEOCgnat()
    {
        var invite = new RoomInvite(Guid.NewGuid(), "Jogatina", ["127.0.0.1:1"], new string('a', 64), RoomInvite.NewToken());

        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => Create("Bruno").FindInviteAsync(invite));

        Assert.Equal(LocalFailure.Unreachable, error.Failure);
        Assert.Contains($"porta TCP {LocalNetwork.RoomPort}", error.Message);
        Assert.Contains("firewall", error.Message);
        Assert.Contains("CGNAT", error.Message);
        Assert.Contains("127.0.0.1:1", error.Message);
    }

    [Fact]
    public async Task ImpressaoDigitalDiferente_NaoEntra()
    {
        var (ana, room) = await HostAsync();
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite;
        var bruno = Create("Bruno");

        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.FindInviteAsync(invite with { Fingerprint = new string('b', 64) }));

        Assert.Equal(LocalFailure.CertificateMismatch, error.Failure);
        Assert.Contains("certificado não confere", error.Message);
        Assert.Null(bruno.CurrentRoomId);
        Assert.DoesNotContain(bruno.Rooms, r => r.Id == room.Id);
    }

    [Fact]
    public async Task VariosEnderecos_EntraPeloQueResponde_EOOutroErroNaoAtrapalha()
    {
        var (ana, room) = await HostAsync();
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite;
        var bruno = Create("Bruno");

        var found = await bruno.FindInviteAsync(invite with { Addresses = ["127.0.0.1:1", .. invite.Addresses] });
        var presence = await bruno.JoinAsync(found.Id);

        Assert.Equal(2, presence.ConnectedUsers.Count);
    }

    [Fact]
    public async Task ConviteDaPropriaSala_Avisa()
    {
        var (ana, room) = await HostAsync();
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite;

        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => ana.FindInviteAsync(invite));

        Assert.Equal(LocalFailure.OwnRoom, error.Failure);
        Assert.Contains("você está hospedando", error.Message);
    }

    [Fact]
    public async Task TokenErradoVariasVezes_BloqueiaOEndereco()
    {
        var (ana, room) = await HostAsync();
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite with { Token = RoomInvite.NewToken() };
        var bruno = Create("Bruno");
        for (var i = 0; i < ConnectionGuard.MaxFailures; i++)
        {
            Assert.Equal(LocalFailure.TokenRejected, (await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.FindInviteAsync(invite))).Failure);
        }

        // Bloqueado, nem o convite certo entra por um tempo.
        var correct = (await ana.CreateInviteAsync(room.Id)).Invite;
        var error = await Assert.ThrowsAsync<LocalNetworkException>(() => bruno.FindInviteAsync(correct));

        Assert.Equal(LocalFailure.Blocked, error.Failure);
        Assert.Contains("alguns minutos", error.Message);
    }

    [Fact]
    public async Task ConvidadoDeFora_AssisteAQuemHospeda_PeloEnderecoDoConvite()
    {
        // Os endereços de rede de Ana não são alcançáveis por Bruno; só o do convite (aqui, o localhost).
        var ana = Create("Ana", Outside, endpoints: port => [$"10.255.255.1:{port}"]);
        var room = await ana.CreateRoomAsync("Jogatina");
        await ana.JoinAsync(room.Id);
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite with { Addresses = [$"127.0.0.1:{ana.ListeningPort}"] };
        var bruno = Create("Bruno");
        await bruno.FindInviteAsync(invite);
        var sawShare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bruno.PresenceChanged += p =>
        {
            if (p.ScreenShares is { Count: 1 })
            {
                sawShare.TrySetResult();
            }
        };
        await bruno.JoinAsync(room.Id);

        await ana.StartShareAsync(new ScreenShareStart(1280, 720, 30, 3000, false));
        await sawShare.Task.WaitAsync(Timeout);
        await using var viewer = await bruno.WatchAsync(room.Id, ana.Me.Id, ShareKind.Screen);

        Assert.NotNull(viewer);
    }

    [Fact]
    public async Task Midia_DeFora_SemAChaveNaoEntregaNada_EComAChaveErradaBloqueia()
    {
        var (ana, room) = await HostAsync();
        await ana.StartShareAsync(new ScreenShareStart(1280, 720, 30, 3000, false));
        var share = Assert.Single(ana.CurrentRoomState!.Shares);
        var port = ana.ListeningPort!.Value;

        Assert.StartsWith("HTTP/1.1 403", await RawUpgradeAsync(port, $"/media/{share.MediaId}"));
        Assert.StartsWith("HTTP/1.1 403", await RawUpgradeAsync(port, $"/media/{share.MediaId}", $"{LocalMediaHost.KeyHeader}: {new string('0', share.Key.Length)}"));
        Assert.StartsWith("HTTP/1.1 404", await RawUpgradeAsync(port, $"/media/{Guid.NewGuid()}", $"{LocalMediaHost.KeyHeader}: {share.Key}"));

        // A chave certa num servidor com outro certificado: quem assiste recusa.
        await Assert.ThrowsAsync<LocalNetworkException>(() =>
            MediaSocket.ConnectDirectAsync([$"127.0.0.1:{port}"], share.MediaId, share.Key, new string('b', 64), 3000, "Ana", CancellationToken.None));

        // Mais três chaves erradas (cinco no total) e o endereço fica bloqueado, mesmo com a chave certa.
        for (var i = 0; i < ConnectionGuard.MaxFailures - 2; i++)
        {
            await RawUpgradeAsync(port, $"/media/{share.MediaId}", $"{LocalMediaHost.KeyHeader}: errada");
        }

        Assert.StartsWith("HTTP/1.1 429", await RawUpgradeAsync(port, $"/media/{share.MediaId}", $"{LocalMediaHost.KeyHeader}: {share.Key}"));
    }

    [Fact]
    public async Task Hello_GrandeDemais_EhRecusadoAntesDeEntrar()
    {
        var (ana, _) = await HostAsync(isTrusted: _ => true);
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{ana.ListeningPort}/room"), CancellationToken.None);

        await socket.SendAsync(Encoding.UTF8.GetBytes(new string('x', LocalMessage.MaxHelloBytes + 1)), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1024];
        try
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        catch (WebSocketException)
        {
        }

        Assert.Single(ana.CurrentRoomState!.Members);
    }

    [Fact]
    public async Task GruposPorConvite_PedeConfirmacao_EntraNaSala()
    {
        var (ana, room) = await HostAsync();
        var link = (await ana.CreateInviteAsync(room.Id)).Link;
        var bruno = Create("Bruno");
        bruno.IsLocalOnly = true;
        var vm = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(new FakeGroupsRealtime()), bruno), new FakeScreenShareService(), new FakeScreenShareUi(), bruno);
        await vm.LoadAsync();

        vm.OfferInvite(link);

        // Nada conecta antes de confirmar.
        Assert.True(vm.HasPendingInvite);
        Assert.Equal($"Entrar na sala Jogatina em 127.0.0.1:{ana.ListeningPort}?", vm.InviteQuestion);
        Assert.Empty(vm.Groups);

        await vm.AcceptInviteCommand.ExecuteAsync();

        Assert.False(vm.HasPendingInvite);
        Assert.Equal(room.Id, vm.CurrentGroup?.Id);
        Assert.Equal(room.Id, bruno.CurrentRoomId);
    }

    [Fact]
    public async Task GruposPorConvite_RecusarNaoConecta_ELinkInvalidoMostraOErro()
    {
        var (ana, room) = await HostAsync();
        var link = (await ana.CreateInviteAsync(room.Id)).Link;
        var bruno = Create("Bruno");
        bruno.IsLocalOnly = true;
        var vm = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(new FakeGroupsRealtime()), bruno), new FakeScreenShareService(), new FakeScreenShareUi(), bruno);
        await vm.LoadAsync();

        vm.OfferInvite(link);
        vm.DeclineInviteCommand.Execute(null);
        Assert.False(vm.HasPendingInvite);
        Assert.Null(bruno.CurrentRoomId);

        vm.OfferInvite(link[..link.IndexOf("&t=", StringComparison.Ordinal)]);
        Assert.False(vm.HasPendingInvite);
        Assert.Contains("Peça um convite novo", vm.ErrorMessage);

        // Colado no "Entrar pelo IP", o link também pede a confirmação.
        vm.JoinAddress = link;
        await vm.JoinByAddressCommand.ExecuteAsync();
        Assert.True(vm.HasPendingInvite);
        Assert.Equal("", vm.JoinAddress);
    }

    [Fact]
    public async Task GruposPorConvite_FalhaAoEntrar_MostraOMotivo()
    {
        var (ana, room) = await HostAsync();
        var invite = (await ana.CreateInviteAsync(room.Id)).Invite;
        await ana.CreateInviteAsync(room.Id, renew: true);
        var bruno = Create("Bruno");
        bruno.IsLocalOnly = true;
        var vm = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(new FakeGroupsRealtime()), bruno), new FakeScreenShareService(), new FakeScreenShareUi(), bruno);
        await vm.LoadAsync();

        vm.OfferInvite(invite.ToLink());
        await vm.AcceptInviteCommand.ExecuteAsync();

        Assert.False(vm.HasPendingInvite);
        Assert.Contains("gerou um convite novo", vm.ErrorMessage);
        Assert.Null(vm.CurrentGroup);
    }

    [Fact]
    public async Task GruposCopiarConvite_VaiParaAAreaDeTransferenciaComOAviso()
    {
        var clipboard = new FakeClipboard();
        var ana = Create("Ana", Outside, IPAddress.Parse("203.0.113.7"));
        ana.IsLocalOnly = true;
        var vm = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(new FakeGroupsRealtime()), ana), new FakeScreenShareService(), new FakeScreenShareUi(), ana, clipboard: clipboard);
        await vm.LoadAsync();
        vm.NewGroupName = "Jogatina";
        await vm.CreateCommand.ExecuteAsync();
        var item = Assert.Single(vm.Groups);

        Assert.True(item.IsLocalHost);
        Assert.Contains($"porta TCP {LocalNetwork.RoomPort}", item.PortForwardHint);
        Assert.Contains($"UDP {LocalNetwork.DiscoveryPort}", item.InviteDetails);
        Assert.Contains("Tailscale", item.InviteDetails);
        Assert.Contains("CGNAT", item.PortForwardHint);

        await item.CopyInviteCommand.ExecuteAsync();
        var first = RoomInvite.Parse(clipboard.Text);
        Assert.True(clipboard.Sensitive);
        Assert.Equal(item.Id, first.RoomId);
        Assert.Contains("IP público", item.InviteNotice);

        await item.RenewInviteCommand.ExecuteAsync();
        Assert.NotEqual(first.Token, RoomInvite.Parse(clipboard.Text).Token);
        Assert.Contains("O anterior não funciona mais", item.InviteNotice);

        clipboard.Fails = true;
        await item.CopyInviteCommand.ExecuteAsync();
        Assert.Contains("área de transferência", item.InviteNotice);
    }

    [Fact]
    public async Task GruposCopiarConvite_SemIpPublico_Avisa()
    {
        var clipboard = new FakeClipboard();
        var ana = Create("Ana");
        ana.IsLocalOnly = true;
        var vm = new GroupsViewModel(new FakeGroupsService(), new RoutedGroupsRealtime(new ApiRealtime(new FakeGroupsRealtime()), ana), new FakeScreenShareService(), new FakeScreenShareUi(), ana, clipboard: clipboard);
        await vm.LoadAsync();
        vm.NewGroupName = "Jogatina";
        await vm.CreateCommand.ExecuteAsync();
        var item = Assert.Single(vm.Groups);

        await item.CopyInviteCommand.ExecuteAsync();

        Assert.Contains("sem o IP público", item.InviteNotice);
        Assert.NotNull(clipboard.Text);
    }

    /// <summary>Upgrade WebSocket cru por TLS (sem conferir o certificado): a primeira linha da resposta.</summary>
    private static async Task<string> RawUpgradeAsync(int port, string path, string? header = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("localhost");
        var request = $"GET {path} HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                      "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" +
                      (header is null ? "" : header + "\r\n") + "\r\n";
        await tls.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[256];
        var read = await tls.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}

/// <summary>Limites do servidor antes de a conexão ser aceita.</summary>
public sealed class LocalServerLimitsTests : IAsyncLifetime
{
    private readonly LocalCertificate _certificate = LocalCertificate.Create();
    private readonly List<LocalServer> _servers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }

        _certificate.Dispose();
    }

    private LocalServer Start(bool trusted, TimeSpan? handshakeTimeout = null)
    {
        var server = new LocalServer(
            _ => LocalRoute.Accept((_, _) => Task.CompletedTask), _certificate.Certificate, NullLogger.Instance, _ => trusted, handshakeTimeout: handshakeTimeout);
        server.Start(0);
        _servers.Add(server);
        return server;
    }

    /// <summary>A conexão foi fechada pelo servidor dentro do prazo (a leitura termina com 0 ou com erro).</summary>
    private static async Task<bool> ClosedWithinAsync(TcpClient client, TimeSpan within)
    {
        var buffer = new byte[16];
        try
        {
            return await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(within) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    [Fact]
    public async Task SemTerminarOHandshake_FechaNoTempoLimite()
    {
        var server = Start(trusted: false, TimeSpan.FromMilliseconds(300));
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, server.Port);

        Assert.True(await ClosedWithinAsync(idle, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DeFora_ConexoesDemaisEsperando_SaoFechadasNaHora()
    {
        var server = Start(trusted: false);
        var waiting = new List<TcpClient>();
        for (var i = 0; i < LocalServer.MaxPendingPerAddress; i++)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            waiting.Add(client);
        }

        await Task.Delay(200);
        using var extra = new TcpClient();
        await extra.ConnectAsync(IPAddress.Loopback, server.Port);

        Assert.True(await ClosedWithinAsync(extra, TimeSpan.FromSeconds(2)));
        Assert.False(await ClosedWithinAsync(waiting[0], TimeSpan.FromMilliseconds(200)));
        waiting.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task NaRedeLocal_OLimitePorEnderecoNaoVale()
    {
        var server = Start(trusted: true);
        var waiting = new List<TcpClient>();
        for (var i = 0; i < LocalServer.MaxPendingPerAddress; i++)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            waiting.Add(client);
        }

        await Task.Delay(200);
        using var extra = new TcpClient();
        await extra.ConnectAsync(IPAddress.Loopback, server.Port);

        Assert.False(await ClosedWithinAsync(extra, TimeSpan.FromMilliseconds(500)));
        waiting.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task CabecalhoGrandeDemais_EhRecusado()
    {
        var server = Start(trusted: false);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("localhost");

        await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /room HTTP/1.1\r\nX-Grande: " + new string('a', LocalServer.MaxHeaderBytes)));
        var buffer = new byte[64];
        var read = await tls.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith("HTTP/1.1 400", Encoding.ASCII.GetString(buffer, 0, read));
    }
}

internal sealed class FakePublicAddress(IPAddress? address) : IPublicAddressResolver
{
    public Task<IPAddress?> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(address);
}

internal sealed class FakeClipboard : IClipboard
{
    public string? Text { get; private set; }

    public bool Sensitive { get; private set; }

    public bool Fails { get; set; }

    public bool TrySetText(string text, bool sensitive = false)
    {
        if (Fails)
        {
            return false;
        }

        Text = text;
        Sensitive = sensitive;
        return true;
    }
}
