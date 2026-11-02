using System.IO;
using System.Security.Cryptography;
using ScreenCord.Security;

namespace ScreenCord.Tests;

public sealed class ReleaseSignatureTests : IDisposable
{
    private const string Name = "ScreenCord-Setup-1.0.0-x64.msi";

    private readonly string _folder = Directory.CreateTempSubdirectory("screencord-sig").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string PublicKey(ECDsa? key = null) => Convert.ToBase64String((key ?? _key).ExportSubjectPublicKeyInfo());

    private string Msi(string content = "conteudo do instalador")
    {
        var path = Path.Combine(_folder, "setup.msi");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Assina como o release.yml (openssl dgst -sha256 -sign): ECDSA em DER sobre a mensagem do nome e do hash.</summary>
    private string Sign(string path, string name = Name, ECDsa? key = null)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var signature = (key ?? _key).SignData(ReleaseSignature.Message(name, hash), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    [Fact]
    public void AssinaturaDaChaveConfiavel_Confere()
    {
        var msi = Msi();

        Assert.True(ReleaseSignature.Verify(msi, Name, Sign(msi) + "\n", [PublicKey()]));
    }

    [Fact]
    public void ArquivoAlterado_NaoConfere()
    {
        var msi = Msi();
        var signature = Sign(msi);

        Assert.False(ReleaseSignature.Verify(Msi("outro conteudo"), Name, signature, [PublicKey()]));
    }

    [Fact]
    public void MsiAntigoAssinadoSobOutroNome_NaoServeDeVersaoNova()
    {
        var msi = Msi();
        var signatureOfOldVersion = Sign(msi, "ScreenCord-Setup-0.9.0-x64.msi");

        Assert.False(ReleaseSignature.Verify(msi, Name, signatureOfOldVersion, [PublicKey()]));
    }

    [Fact]
    public void AssinaturaDeOutraChave_NaoConfere()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var msi = Msi();

        Assert.False(ReleaseSignature.Verify(msi, Name, Sign(msi, key: attacker), [PublicKey()]));
    }

    [Fact]
    public void AceitaQualquerUmaDasChavesConfiaveis()
    {
        using var spare = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var msi = Msi();

        Assert.True(ReleaseSignature.Verify(msi, Name, Sign(msi, key: spare), [PublicKey(), PublicKey(spare)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("isto não é base64!")]
    [InlineData("AAAA")]
    public void AssinaturaMalformada_NaoConfere(string signature)
    {
        Assert.False(ReleaseSignature.Verify(Msi(), Name, signature, [PublicKey()]));
    }

    [Fact]
    public void SemChavesLegiveis_NaoConfere()
    {
        var msi = Msi();

        Assert.False(ReleaseSignature.Verify(msi, Name, Sign(msi), ["lixo", ""]));
    }

    [Fact]
    public void ChaveEmbutidaNoAppELegivel()
    {
        Assert.NotEmpty(ReleaseSignature.TrustedKeys);
        foreach (var key in ReleaseSignature.TrustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            Assert.Equal(256, ecdsa.KeySize);
        }
    }
}
