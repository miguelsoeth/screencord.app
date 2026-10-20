using System.Globalization;
using Microsoft.Win32;

namespace ScreenCord.Localization;

/// <summary>
/// Idioma escolhido, em HKCU\Software\ScreenCord (valor "Language": pt, en ou es). O instalador grava o mesmo
/// valor com o idioma escolhido na instalação e o lê de volta nas atualizações, então a troca feita no app vale
/// também depois de atualizar.
/// </summary>
public static class LanguagePreference
{
    public const string KeyPath = @"Software\ScreenCord";
    public const string ValueName = "Language";

    public static AppLanguage Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (Loc.FromCode(key?.GetValue(ValueName) as string) is { } saved)
            {
                return saved;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Sem acesso ao registro: segue com o idioma do Windows.
        }

        return Loc.FromCulture(CultureInfo.CurrentUICulture);
    }

    public static void Save(AppLanguage language)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, Loc.Code(language), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Não salvou: a troca vale só até fechar o app.
        }
    }
}
