using ScreenCord.Models;

namespace ScreenCord.Services;

/// <summary>
/// Monta URLs do CDN conforme a seção "Image Formats" da referência oficial:
/// <c>avatars/{user_id}/{user_avatar}.png</c> e <c>embed/avatars/{index}.png</c>.
/// </summary>
public static class AvatarUrlBuilder
{
    private const string CdnBase = "https://cdn.discordapp.com/";

    /// <param name="size">Potência de dois entre 16 e 4096.</param>
    public static string Build(DiscordUser user, int size = 128)
    {
        ArgumentException.ThrowIfNullOrEmpty(user.Id);
        if (size is < 16 or > 4096 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "O tamanho deve ser uma potência de dois entre 16 e 4096.");
        }

        if (!string.IsNullOrEmpty(user.Avatar) && IsValidHash(user.Avatar))
        {
            // PNG é aceito também para avatares animados (hash "a_..."), retornando o quadro estático,
            // que é o que o WPF consegue decodificar nativamente.
            return $"{CdnBase}avatars/{user.Id}/{user.Avatar}.png?size={size}";
        }

        // O parâmetro size é ignorado pelo CDN para avatares padrão.
        return $"{CdnBase}embed/avatars/{DefaultAvatarIndex(user)}.png";
    }

    public static int DefaultAvatarIndex(DiscordUser user)
    {
        // Sistema legado: discriminator % 5. Sistema novo (discriminator "0"): (user_id >> 22) % 6.
        if (int.TryParse(user.Discriminator, out var discriminator) && discriminator > 0)
        {
            return discriminator % 5;
        }

        return ulong.TryParse(user.Id, out var id) ? (int)((id >> 22) % 6) : 0;
    }

    /// <summary>O hash vai para o caminho da URL; só são aceitos hexadecimais com prefixo opcional <c>a_</c>.</summary>
    public static bool IsValidHash(string hash)
    {
        var span = hash.AsSpan();
        if (span.StartsWith("a_"))
        {
            span = span[2..];
        }

        if (span.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
