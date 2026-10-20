using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ScreenCord.Local;

/// <summary>
/// Certificado autoassinado das salas locais, novo a cada execução: cifra a sala e a mídia (TLS). Não há autoridade
/// que o assine; quem conecta confere a impressão digital (<see cref="Fingerprint"/>), que chega pelo anúncio da sala
/// e, para a mídia, pelo estado da sala (que já vem por TLS).
/// </summary>
public sealed class LocalCertificate : IDisposable
{
    /// <summary>Tamanho da impressão digital: SHA-256 do certificado, em hexadecimal.</summary>
    public const int FingerprintLength = 64;

    private LocalCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate);
    }

    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }

    public static LocalCertificate Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ScreenCord sala local", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        // O SChannel não aceita chave efêmera no servidor: reimportada pelo PKCS#12, ela vai para um contêiner
        // temporário do Windows, apagado no Dispose.
        return new LocalCertificate(X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null));
    }

    public static string FingerprintOf(X509Certificate certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));

    public static bool IsValidFingerprint(string? fingerprint) =>
        fingerprint is { Length: FingerprintLength } && fingerprint.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Validação do certificado de quem responde: com <paramref name="expected"/>, só aceita esse certificado; sem ele
    /// (entrada pelo IP digitado), aceita qualquer um, e a conexão fica cifrada sem confirmar quem está do outro lado.
    /// <paramref name="rejected"/> é chamado quando o certificado não confere.
    /// </summary>
    public static RemoteCertificateValidationCallback Validator(string? expected, Action? rejected = null) =>
        (_, certificate, _, _) =>
        {
            var accepted = certificate is not null &&
                           (expected is null || string.Equals(FingerprintOf(certificate), expected, StringComparison.OrdinalIgnoreCase));
            if (!accepted)
            {
                rejected?.Invoke();
            }

            return accepted;
        };

    public void Dispose() => Certificate.Dispose();
}
