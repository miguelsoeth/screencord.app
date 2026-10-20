using System.IO;
using System.Text.Json;

namespace ScreenCord.Local;

/// <summary>Perfil local: identifica este computador nas salas locais e guarda o nome de quem usa sem conta.</summary>
/// <param name="Name">Nome escolhido no "Usar sem conta"; null quando nunca foi usado.</param>
/// <param name="SignedIn">O app está sendo usado sem conta: ao abrir de novo, volta direto para as salas locais.</param>
public sealed record LocalProfile(Guid Id, string? Name, bool SignedIn = false);

public interface ILocalIdentityStore
{
    /// <summary>O perfil salvo; na primeira vez, cria um com um id novo.</summary>
    LocalProfile Load();

    void Save(LocalProfile profile);
}

/// <summary>Guarda o perfil em <c>%LocalAppData%\ScreenCord\local-profile.json</c>.</summary>
public sealed class LocalIdentityStore : ILocalIdentityStore
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public LocalIdentityStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenCord", "local-profile.json");
    }

    public LocalProfile Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) &&
                    JsonSerializer.Deserialize<LocalProfile>(File.ReadAllText(_path), LocalMessage.Json) is { } saved &&
                    saved.Id != Guid.Empty)
                {
                    return saved with { Name = Clean(saved.Name) };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }

            var profile = new LocalProfile(Guid.NewGuid(), null);
            SaveLocked(profile);
            return profile;
        }
    }

    public void Save(LocalProfile profile)
    {
        lock (_lock)
        {
            SaveLocked(profile with { Name = Clean(profile.Name) });
        }
    }

    /// <summary>Nome sem espaços sobrando, com até <see cref="LocalMember.MaxNameLength"/> caracteres; null se vazio.</summary>
    public static string? Clean(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length == 0 ? null : clean.Length > LocalMember.MaxNameLength ? clean[..LocalMember.MaxNameLength] : clean;
    }

    private void SaveLocked(LocalProfile profile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(profile, LocalMessage.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem salvar, o id muda na próxima abertura: só faz a pessoa parecer outra nas salas.
        }
    }
}
