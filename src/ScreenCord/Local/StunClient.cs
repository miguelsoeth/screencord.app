using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ScreenCord.Local;

/// <summary>Descobre o IP público deste computador, para o convite das salas locais.</summary>
public interface IPublicAddressResolver
{
    /// <summary>O IPv4 público, ou null se não deu para descobrir (sem internet, UDP bloqueado).</summary>
    Task<IPAddress?> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Consulta STUN mínima (RFC 5389, só o "Binding Request") por UDP, sem a API e sem dependência nova: o servidor
/// responde com o endereço de onde o pacote chegou, que é o IP público do roteador. Pergunta a alguns servidores
/// públicos ao mesmo tempo e fica com a primeira resposta válida. Só roda quando quem hospeda gera um convite.
/// </summary>
public sealed class StunClient : IPublicAddressResolver
{
    public static readonly IReadOnlyList<string> DefaultServers =
    [
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun.cloudflare.com:3478",
    ];

    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort MappedAddress = 0x0001;
    private const ushort XorMappedAddress = 0x0020;
    private const uint MagicCookie = 0x2112A442;
    private const int HeaderSize = 20;

    private readonly IReadOnlyList<string> _servers;
    private readonly TimeSpan _timeout;

    public StunClient(IReadOnlyList<string>? servers = null, TimeSpan? timeout = null)
    {
        _servers = servers ?? DefaultServers;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<IPAddress?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            // Uma transação por servidor: a resposta só vale se vier de um deles, com o id que foi para ele.
            var transactions = new Dictionary<IPEndPoint, byte[]>();
            foreach (var server in _servers)
            {
                if (await ResolveServerAsync(server, deadline.Token).ConfigureAwait(false) is not { } endpoint || transactions.ContainsKey(endpoint))
                {
                    continue;
                }

                var transaction = RandomNumberGenerator.GetBytes(12);
                transactions[endpoint] = transaction;
                try
                {
                    await udp.SendAsync(BuildRequest(transaction), endpoint, deadline.Token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                }
            }

            while (transactions.Count > 0)
            {
                var received = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (transactions.TryGetValue(received.RemoteEndPoint, out var transaction) &&
                    ParseResponse(received.Buffer, transaction) is { } address && IsPublic(address))
                {
                    return address;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        return null;
    }

    /// <summary>Endereço que serve para o convite: IPv4 fora das redes locais, VPNs e faixas reservadas.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || LocalNetwork.IsLocal(address))
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] is not (0 or >= 224) && !(b[0] == 169 && b[1] == 254);
    }

    public static byte[] BuildRequest(ReadOnlySpan<byte> transaction)
    {
        var packet = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(packet, BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        transaction.CopyTo(packet.AsSpan(8));
        return packet;
    }

    /// <summary>O endereço da resposta de sucesso da transação, ou null para qualquer outra coisa.</summary>
    public static IPAddress? ParseResponse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> transaction)
    {
        if (data.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt16BigEndian(data) != BindingSuccess ||
            BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != MagicCookie ||
            !data.Slice(8, 12).SequenceEqual(transaction))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (HeaderSize + length > data.Length)
        {
            return null;
        }

        var attributes = data.Slice(HeaderSize, length);
        IPAddress? mapped = null;
        while (attributes.Length >= 4)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(attributes);
            var size = BinaryPrimitives.ReadUInt16BigEndian(attributes[2..]);
            if (4 + size > attributes.Length)
            {
                return null;
            }

            var value = attributes.Slice(4, size);
            // Família 0x01 (IPv4): [0, família, porta (2), endereço (4)].
            if (size == 8 && value[1] == 0x01)
            {
                if (type == XorMappedAddress)
                {
                    var xored = BinaryPrimitives.ReadUInt32BigEndian(value[4..]) ^ MagicCookie;
                    var bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, xored);
                    return new IPAddress(bytes);
                }

                if (type == MappedAddress)
                {
                    mapped ??= new IPAddress(value[4..8]);
                }
            }

            // Os atributos ocupam múltiplos de 4 bytes.
            var padded = 4 + ((size + 3) & ~3);
            attributes = padded >= attributes.Length ? [] : attributes[padded..];
        }

        return mapped;
    }

    private static async Task<IPEndPoint?> ResolveServerAsync(string server, CancellationToken cancellationToken)
    {
        var colon = server.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(server.AsSpan(colon + 1), out var port))
        {
            return null;
        }

        var host = server[..colon];
        if (IPAddress.TryParse(host, out var literal))
        {
            return new IPEndPoint(literal, port);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0 ? new IPEndPoint(addresses[0], port) : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
