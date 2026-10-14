using System.Security.Cryptography;
using System.Text;

namespace ScreenCord.Security;

/// <summary>Geração de valores PKCE (RFC 7636) e do parâmetro <c>state</c>.</summary>
public static class Pkce
{
    /// <summary>43 caracteres base64url (32 bytes aleatórios).</summary>
    public static string CreateCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Desafio S256: BASE64URL(SHA256(ASCII(code_verifier))).</summary>
    public static string CreateCodeChallenge(string codeVerifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Comparação em tempo constante, para validar o <c>state</c> recebido no callback.</summary>
    public static bool FixedTimeEquals(string expected, string? actual) =>
        actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
