using Microsoft.Win32;
using ScreenCord.Localization;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Se a transmissão (tela e câmera) usa o encoder H.264 da placa de vídeo, em HKCU\Software\ScreenCord\HardwareEncoder
/// (1 ou 0; ligado por padrão). Desligado, vai direto para o encoder por software do Windows. A captura e a conversão
/// para NV12 continuam na GPU. Escolhido no menu da engrenagem; vale a partir da próxima transmissão.
/// </summary>
public static class HardwareAccelerationPreference
{
    public const string ValueName = "HardwareEncoder";

    private static bool _enabled = Load();

    public static bool Enabled => _enabled;

    public static void Set(bool enabled)
    {
        _enabled = enabled;
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

    private static bool Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LanguagePreference.KeyPath);
            return key?.GetValue(ValueName) is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Sem acesso ao registro: vale o padrão.
            return true;
        }
    }
}
