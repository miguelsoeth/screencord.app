using ScreenCord.Localization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenCord.Local;

/// <summary>
/// Endereços das salas locais: redes domésticas e as VPNs de jogo mais usadas (Hamachi, Radmin, Tailscale).
/// As salas locais escutam numa porta fixa, a mesma que a regra do Firewall do Windows libera.
/// </summary>
public static class LocalNetwork
{
    /// <summary>Porta TCP das salas locais e da mídia direta entre os computadores.</summary>
    public const int RoomPort = 47830;

    /// <summary>Porta UDP em que as salas se anunciam na rede.</summary>
    public const int DiscoveryPort = 47831;

    /// <summary>Faixas aceitas na regra do firewall (o mesmo texto vai para o <c>netsh</c>).</summary>
    public static readonly string[] Ranges =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "25.0.0.0/8",     // Hamachi
        "26.0.0.0/8",     // Radmin VPN
        "100.64.0.0/10",  // Tailscale (e outras redes CGNAT)
    ];

    /// <summary>O endereço é de rede local ou de uma das VPNs de jogo (inclui o próprio computador).</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            10 or 25 or 26 or 127 => true,
            172 => b[1] is >= 16 and <= 31,
            192 => b[1] == 168,
            100 => b[1] is >= 64 and <= 127,
            _ => false,
        };
    }

    /// <summary>Nome da rede de um endereço, para exibição: "Hamachi", "Radmin", "Tailscale" ou "Rede local".</summary>
    public static string NetworkName(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return Strings.ThisComputer;
        }

        var b = address.GetAddressBytes();
        return address.AddressFamily != AddressFamily.InterNetwork ? Strings.Network
            : b[0] == 25 ? "Hamachi"
            : b[0] == 26 ? "Radmin"
            : b[0] == 100 && b[1] is >= 64 and <= 127 ? "Tailscale"
            : IsLocal(address) ? Strings.LocalNetwork
            : "Internet";
    }

    /// <summary>"25.1.2.3" (porta padrão) ou "25.1.2.3:5000".</summary>
    public static string FormatAddress(string host, int port) => port == RoomPort ? host : $"{host}:{port}";

    /// <summary>Endereços IPv4 locais deste computador, com o broadcast da sub-rede de cada um.</summary>
    public static IReadOnlyList<(IPAddress Address, IPAddress Broadcast)> Interfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || !IsLocal(address) || IPAddress.IsLoopback(address))
                {
                    continue;
                }

                result.Add((address, BroadcastOf(address, unicast.PrefixLength)));
            }
        }

        return result;
    }

    public static IPAddress BroadcastOf(IPAddress address, int prefixLength)
    {
        var value = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(address.GetAddressBytes(), 0));
        var mask = prefixLength <= 0 ? 0u : uint.MaxValue << (32 - Math.Min(prefixLength, 32));
        var broadcast = value | ~mask;
        return new IPAddress(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)broadcast)));
    }

    /// <summary>Endereços em que este computador pode ser alcançado pelas salas locais (<c>ip:porta</c>).</summary>
    public static IReadOnlyList<string> Endpoints(int port = RoomPort) =>
        Interfaces().Select(i => $"{i.Address}:{port}").Distinct().ToList();

    /// <summary>
    /// Lê "25.1.2.3", "25.1.2.3:47830" ou "nome-do-pc:47830". Sem porta, usa a das salas locais.
    /// </summary>
    public static bool TryParseAddress(string? text, out string host, out int port)
    {
        host = "";
        port = RoomPort;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains('/') || text.Contains(' '))
        {
            return false;
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535)
            {
                return false;
            }

            text = text[..colon];
        }

        if (Uri.CheckHostName(text) is UriHostNameType.Unknown or UriHostNameType.IPv6)
        {
            return false;
        }

        host = text;
        return true;
    }
}
