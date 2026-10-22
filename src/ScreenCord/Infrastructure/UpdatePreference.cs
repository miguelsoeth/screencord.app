using Microsoft.Win32;
using ScreenCord.Localization;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Como o app se atualiza, em HKCU\Software\ScreenCord: "AutoUpdate" (1 ou 0; ligado por padrão, instala sem
/// perguntar) e "SkippedUpdate" (a versão que a pessoa mandou pular). Escolhidos no menu da engrenagem e na pergunta
/// de atualização. Uma versão que a API não aceita mais é instalada ou fecha o app, valha o que valer aqui.
/// </summary>
public static class UpdatePreference
{
    public const string AutoUpdateValueName = "AutoUpdate";
    public const string SkippedValueName = "SkippedUpdate";

    private static bool _autoUpdate = LoadAutoUpdate();
    private static Version? _skipped = LoadSkipped();

    /// <summary>Instala a versão nova ao abrir sem perguntar.</summary>
    public static bool AutoUpdate => _autoUpdate;

    /// <summary>Versão que a pessoa pediu para pular; a pergunta volta numa versão maior.</summary>
    public static Version? SkippedVersion => _skipped;

    public static void SetAutoUpdate(bool enabled)
    {
        _autoUpdate = enabled;
        Save(key => key.SetValue(AutoUpdateValueName, enabled ? 1 : 0, RegistryValueKind.DWord));
    }

    public static void Skip(Version version)
    {
        _skipped = version;
        Save(key => key.SetValue(SkippedValueName, version.ToString(3), RegistryValueKind.String));
    }

    private static bool LoadAutoUpdate() =>
        Load(key => key?.GetValue(AutoUpdateValueName) is not int value || value != 0, true);

    private static Version? LoadSkipped() =>
        Load(key => AppVersion.TryParse(key?.GetValue(SkippedValueName) as string, out var version) ? version : null, null);

    private static T Load<T>(Func<RegistryKey?, T> read, T fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LanguagePreference.KeyPath);
            return read(key);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Sem acesso ao registro: vale o padrão.
            return fallback;
        }
    }

    private static void Save(Action<RegistryKey> write)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LanguagePreference.KeyPath);
            write(key);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Não salvou: a escolha vale só até fechar o app.
        }
    }
}
