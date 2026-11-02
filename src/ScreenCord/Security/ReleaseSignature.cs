using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCord.Security;

/// <summary>
/// Assinatura do instalador da atualização automática. O release.yml assina cada MSI com uma chave privada guardada só
/// como secret do repositório do app (ECDSA P-256, SHA-256) e publica a assinatura ao lado dele (<c>.sig</c>). O app
/// embute a chave pública e só instala o MSI cuja assinatura confere. Assim, quem conseguir publicar uma release no
/// repositório público de downloads (token ou conta roubados) não consegue fazer o app instalar o arquivo dele.
/// <para>
/// O que se assina não é só o arquivo: é o nome (que carrega a versão) junto com o SHA-256, para que um MSI antigo,
/// assinado de verdade, não sirva de versão nova.
/// </para>
/// </summary>
public static class ReleaseSignature
{
    /// <summary>Chaves públicas aceitas (SubjectPublicKeyInfo em base64). Mais de uma permite trocar a chave sem quebrar a atualização.</summary>
    public static readonly IReadOnlyList<string> TrustedKeys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVs2oRkn/eiywmAN4e6YdlltpoObhKTw0golwKBVauoJXFNvP8Vs5h/hyPS8pJbETEh7GqxgOP2Y1lhLIN0htWA==",
    ];

    /// <summary>Maior arquivo de assinatura aceito (a assinatura em base64 tem uns 100 bytes).</summary>
    public const int MaxSignatureFileBytes = 1024;

    private const string Domain = "screencord-release-v1";

    /// <summary>A mensagem assinada: <c>screencord-release-v1\n{nome do MSI}\n{SHA-256 em hexadecimal minúsculo}</c>.</summary>
    public static byte[] Message(string installerName, string sha256Hex) =>
        Encoding.UTF8.GetBytes($"{Domain}\n{installerName}\n{sha256Hex.ToLowerInvariant()}");

    /// <summary>True se o arquivo, com este nome, foi assinado por alguma das chaves. Qualquer defeito na assinatura dá false.</summary>
    public static bool Verify(string filePath, string installerName, string signatureBase64, IEnumerable<string>? trustedKeys = null)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        string hash;
        using (var stream = File.OpenRead(filePath))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        var message = Message(installerName, hash);
        foreach (var key in trustedKeys ?? TrustedKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // Assinatura malformada ou chave ilegível: esta chave não confirma.
            }
        }

        return false;
    }
}
