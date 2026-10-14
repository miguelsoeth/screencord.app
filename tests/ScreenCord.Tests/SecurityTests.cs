using System.Security.Cryptography;
using System.Text;
using ScreenCord.Security;

namespace ScreenCord.Tests;

public class PkceTests
{
    [Fact]
    public void CodeVerifier_TemTamanhoEAlfabetoValidos()
    {
        var verifier = Pkce.CreateCodeVerifier();

        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.NotEqual(verifier, Pkce.CreateCodeVerifier());
    }

    [Fact]
    public void CodeChallenge_SegueOVetorDaRfc7636()
    {
        // Apêndice B da RFC 7636.
        var challenge = Pkce.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void State_EhAleatorio()
    {
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "abcd", false)]
    [InlineData("abc", null, false)]
    public void FixedTimeEquals_ComparaCorretamente(string expected, string? actual, bool result)
    {
        Assert.Equal(result, Pkce.FixedTimeEquals(expected, actual));
    }
}

public class TokenSessionTests
{
    [Fact]
    public void ToString_NaoExpoeTokens()
    {
        var session = new TokenSession("access-secreto", "refresh-secreto", DateTimeOffset.UtcNow, "identify");

        var text = session.ToString();

        Assert.DoesNotContain("access-secreto", text);
        Assert.DoesNotContain("refresh-secreto", text);
    }

    [Fact]
    public void IsExpired_ConsideraMargemDeSeguranca()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(new TokenSession("a", null, now.AddSeconds(30), null).IsExpired(now));
        Assert.False(new TokenSession("a", null, now.AddMinutes(10), null).IsExpired(now));
    }
}

public sealed class DpapiTokenStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ScreenCord.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "session.dat");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveELoad_PreservamASessao()
    {
        var store = new DpapiTokenStore(FilePath);
        var session = new TokenSession("access-token-123", "refresh-token-456", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), "identify");

        await store.SaveAsync(session);
        var loaded = await new DpapiTokenStore(FilePath).LoadAsync();

        Assert.Equal(session, loaded);
    }

    [Fact]
    public async Task Save_NaoGravaTokensEmTextoPuro()
    {
        var store = new DpapiTokenStore(FilePath);
        await store.SaveAsync(new TokenSession("access-token-123", "refresh-token-456", DateTimeOffset.UtcNow, "identify"));

        var raw = await File.ReadAllBytesAsync(FilePath);
        var asText = Encoding.UTF8.GetString(raw);

        Assert.DoesNotContain("access-token-123", asText);
        Assert.DoesNotContain("refresh-token-456", asText);
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public async Task Load_SemArquivo_RetornaNulo()
    {
        Assert.Null(await new DpapiTokenStore(FilePath).LoadAsync());
    }

    [Fact]
    public async Task Load_ArquivoCorrompido_RetornaNuloEApaga()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "isto não é um blob DPAPI");

        Assert.Null(await new DpapiTokenStore(FilePath).LoadAsync());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Clear_RemoveOArquivo()
    {
        var store = new DpapiTokenStore(FilePath);
        await store.SaveAsync(new TokenSession("a", "b", DateTimeOffset.UtcNow, null));

        await store.ClearAsync();

        Assert.False(File.Exists(FilePath));
        Assert.Null(await store.LoadAsync());
    }
}
