using Microsoft.Win32;
using ScreenCord.Localization;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Se a pessoa aceita enviar os relatórios de erro (<see cref="ErrorReporter"/>), em HKCU\Software\ScreenCord
/// (valor "Telemetry": 1 ou 0). Ligado por padrão, como dizem os termos do instalador; a escolha fica no menu de
/// privacidade do app e vale na hora. O <c>Telemetry:Enabled=false</c> da configuração desliga de vez, acima daqui.
/// </summary>
public static class TelemetryPreference
{
    public const string ValueName = "Telemetry";

    private static bool _enabled = Load();

    /// <summary>A escolha mudou (no menu de privacidade); o <see cref="ErrorReporter"/> segue na hora.</summary>
    public static event Action<bool>? Changed;

    public static bool Enabled => _enabled;

    public static void Set(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        Save(enabled);
        Changed?.Invoke(enabled);
    }

    private static bool Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LanguagePreference.KeyPath);
            return key?.GetValue(ValueName) is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Sem acesso ao registro: vale o padrão dos termos.
            return true;
        }
    }

    private static void Save(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LanguagePreference.KeyPath);
            key.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Não salvou: a escolha vale só até fechar o app.
        }
    }
}
