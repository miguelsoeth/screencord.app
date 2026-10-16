using System.Reflection;

namespace ScreenCord.Infrastructure;

/// <summary>Versão deste build: a da tag git no CI (ex.: 0.2.0) ou 0.0.0-dev no desenvolvimento.</summary>
public static class AppVersion
{
    /// <summary>Texto enviado à API no cabeçalho de versão.</summary>
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0-dev";

    /// <summary>Build local (dotnet run, testes): não se atualiza sozinho e a API sempre aceita.</summary>
    public static bool IsDevBuild => !TryParse(Current, out var version) || version == new Version(0, 0, 0);

    /// <summary>Interpreta "MAJOR.MINOR.PATCH", com "v" inicial e sufixo "-pre" ou "+meta" opcionais.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v');
        var suffix = core.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            core = core[..suffix];
        }

        var parts = core.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch) || major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new Version(major, minor, patch);
        return true;
    }
}
