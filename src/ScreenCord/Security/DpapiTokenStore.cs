using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenCord.Security;

public interface ITokenStore
{
    Task<TokenSession?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TokenSession session, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Persiste a sessão cifrada com DPAPI no escopo do usuário atual do Windows.
/// O arquivo só pode ser decifrado pela mesma conta, na mesma máquina.
/// </summary>
public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenCord.TokenSession.v1");

    private readonly string _filePath;

    public DpapiTokenStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenCord",
            "session.dat"))
    {
    }

    public DpapiTokenStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<TokenSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var session = JsonSerializer.Deserialize<TokenSession>(plain);
                return string.IsNullOrEmpty(session?.AccessToken) ? null : session;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Arquivo corrompido ou de outro usuário/máquina: descarta e exige novo login.
            TryDelete();
            return null;
        }
    }

    public async Task SaveAsync(TokenSession session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(_filePath, encrypted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        TryDelete();
        return Task.CompletedTask;
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
