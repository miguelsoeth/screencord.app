using ScreenCord.Localization;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCord.Local;

/// <summary>
/// Convite autocontido para uma sala local, sem a API: <c>screencord://join?v=1&amp;p=...&amp;r=...&amp;n=...&amp;a=...&amp;f=...&amp;t=...</c>.
/// Leva os endereços do anfitrião (IP público, LAN e VPNs, cada um com a porta), a impressão digital do certificado
/// (quem entra só aceita esse), o token da sala (exigido de quem vem de fora das redes locais) e o nome, só para exibir.
/// O link chega por qualquer canal: ao ler, todo campo é validado e nada é executado a partir dele.
/// </summary>
/// <param name="Protocol">Versão do protocolo da sala (<see cref="LocalMessage.CurrentProtocol"/> de quem convidou).</param>
public sealed record RoomInvite(Guid RoomId, string Name, IReadOnlyList<string> Addresses, string Fingerprint, string Token, int Protocol = LocalMessage.CurrentProtocol)
{
    public const string Scheme = "screencord";

    /// <summary>Versão do formato do link (<c>v</c>). Mudança incompatível no formato: suba.</summary>
    public const int FormatVersion = 1;

    public const int MaxAddresses = 8;
    public const int MaxLinkLength = 2048;

    /// <summary>128 bits em base64url; o token gerado aqui tem 256 (<see cref="NewToken"/>).</summary>
    public const int MinTokenLength = 22;
    public const int MaxTokenLength = 128;

    private const string Prefix = Scheme + "://join";

    /// <summary>Token novo da sala: 32 bytes do gerador criptográfico, em base64url.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>O texto parece um link de convite (pode ainda ser inválido: confira com <see cref="Parse"/>).</summary>
    public static bool IsInviteLink(string? text) =>
        text is not null && text.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public string ToLink()
    {
        var builder = new StringBuilder(Prefix).Append('?');
        builder.Append("v=").Append(FormatVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append("&p=").Append(Protocol.ToString(CultureInfo.InvariantCulture));
        builder.Append("&r=").Append(RoomId.ToString("N"));
        builder.Append("&n=").Append(Uri.EscapeDataString(SanitizeName(Name)));
        builder.Append("&a=").Append(string.Join(',', Addresses));
        builder.Append("&f=").Append(Fingerprint.ToLowerInvariant());
        builder.Append("&t=").Append(Token);
        return builder.ToString();
    }

    /// <summary>Lê e valida o link. Falha com <see cref="LocalNetworkException"/> e um texto que diz o que fazer.</summary>
    public static RoomInvite Parse(string? link)
    {
        var text = link?.Trim() ?? "";
        if (text.Length is 0 or > MaxLinkLength || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid();
        }

        // Alguns navegadores acrescentam "/" antes da consulta.
        var rest = text[Prefix.Length..];
        rest = rest.StartsWith("/?", StringComparison.Ordinal) ? rest[2..] : rest.StartsWith('?') ? rest[1..] : throw Invalid();

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in rest.Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                throw Invalid();
            }

            var key = part[..equals];
            string value;
            try
            {
                value = Uri.UnescapeDataString(part[(equals + 1)..]);
            }
            catch (UriFormatException)
            {
                throw Invalid();
            }

            // Campo repetido é adulteração; campo desconhecido (de uma versão mais nova do mesmo formato) fica de fora.
            if (!key.All(char.IsAsciiLetterLower) || !fields.TryAdd(key, value))
            {
                throw Invalid();
            }
        }

        if (!fields.TryGetValue("v", out var version) || !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var formatVersion))
        {
            throw Invalid();
        }

        if (formatVersion != FormatVersion)
        {
            throw new LocalNetworkException(formatVersion > FormatVersion ? Strings.InviteNewerVersion : Strings.InviteInvalid, failure: LocalFailure.InvalidInvite);
        }

        if (!fields.TryGetValue("p", out var protocolText) || !int.TryParse(protocolText, NumberStyles.None, CultureInfo.InvariantCulture, out var protocol) ||
            !fields.TryGetValue("r", out var roomText) || !Guid.TryParseExact(roomText, "N", out var roomId) || roomId == Guid.Empty ||
            !fields.TryGetValue("n", out var rawName) || SanitizeName(rawName) is not { Length: > 0 } name ||
            !fields.TryGetValue("a", out var addressText) || ParseAddresses(addressText) is not { } addresses ||
            !fields.TryGetValue("f", out var fingerprint) || !LocalCertificate.IsValidFingerprint(fingerprint) ||
            !fields.TryGetValue("t", out var token) || !IsValidToken(token))
        {
            throw Invalid();
        }

        if (protocol != LocalMessage.CurrentProtocol)
        {
            throw new LocalNetworkException(
                protocol > LocalMessage.CurrentProtocol ? Strings.InviteNewerProtocol : Strings.InviteOlderProtocol, failure: LocalFailure.ProtocolMismatch);
        }

        return new RoomInvite(roomId, name, addresses, fingerprint.ToLowerInvariant(), token, protocol);
    }

    public static bool TryParse(string? link, out RoomInvite? invite, out string? error)
    {
        try
        {
            invite = Parse(link);
            error = null;
            return true;
        }
        catch (LocalNetworkException ex)
        {
            invite = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Base64url, entre <see cref="MinTokenLength"/> e <see cref="MaxTokenLength"/> caracteres.</summary>
    public static bool IsValidToken(string? token) =>
        token is { Length: >= MinTokenLength and <= MaxTokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Nome para exibir: sem caracteres de controle nem de direção do texto (que fariam o nome parecer outro), com os
    /// espaços juntados e no tamanho máximo do nome de grupo.
    /// </summary>
    public static string SanitizeName(string? name)
    {
        var visible = new string((name ?? "").Where(c => !char.IsControl(c) && !IsFormatting(c)).ToArray());
        var clean = string.Join(' ', visible.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > Contracts.GroupsHubContract.MaxNameLength ? clean[..Contracts.GroupsHubContract.MaxNameLength] : clean;
    }

    /// <summary>"ip:porta" de IPv4, um a <see cref="MaxAddresses"/>, separados por vírgula; null se algum for inválido.</summary>
    private static List<string>? ParseAddresses(string text)
    {
        var parts = text.Split(',');
        if (parts.Length is 0 or > MaxAddresses)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var part in parts)
        {
            if (!TryParseEndpoint(part, out var address, out var port))
            {
                return null;
            }

            var formatted = $"{address}:{port}";
            if (!result.Contains(formatted))
            {
                result.Add(formatted);
            }
        }

        return result;
    }

    /// <summary>IPv4 com os quatro números escritos por extenso (o <see cref="IPAddress.TryParse(string, out IPAddress)"/> aceita "1" ou "0x7f.1") e porta.</summary>
    public static bool TryParseEndpoint(string text, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon != text.LastIndexOf(':') ||
            !int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            return false;
        }

        var octets = text[..colon].Split('.');
        if (octets.Length != 4 || octets.Any(o => o.Length is 0 or > 3 || !o.All(char.IsAsciiDigit) || int.Parse(o, CultureInfo.InvariantCulture) > 255))
        {
            return false;
        }

        var bytes = octets.Select(o => byte.Parse(o, CultureInfo.InvariantCulture)).ToArray();
        // Nem "0.x.x.x", nem multicast, nem o broadcast geral.
        if (bytes[0] == 0 || bytes[0] >= 224)
        {
            return false;
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static bool IsFormatting(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format || c is (char)0x2028 or (char)0x2029;

    private static LocalNetworkException Invalid() => new(Strings.InviteInvalid, failure: LocalFailure.InvalidInvite);
}
