using System.IO;
using Microsoft.Win32;
using ScreenCord.Local;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Protocolo <c>screencord://</c> (links de convite das salas locais), por usuário em
/// <c>HKCU\Software\Classes\screencord</c>, sem administrador. O instalador registra; builds fora do MSI (desenvolvimento,
/// exe copiado) registram ao abrir, se não houver registro ou se ele apontar para um exe que não existe mais.
/// </summary>
public static class UrlProtocol
{
    public const string KeyPath = @"Software\Classes\" + RoomInvite.Scheme;

    /// <summary>Linha de comando que o Windows roda ao abrir um link: o exe com o link como único argumento.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";

    public static void EnsureRegistered(string exePath)
    {
        try
        {
            using (var command = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command"))
            {
                if (command?.GetValue(null) is string existing && RegisteredExe(existing) is { } registered && File.Exists(registered))
                {
                    return;
                }
            }

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(null, "URL:ScreenCord");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{exePath}\",0");
            }

            using var open = key.CreateSubKey(@"shell\open\command");
            open.SetValue(null, CommandFor(exePath));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Sem registro, o link não abre o app; colar o convite continua funcionando.
        }
    }

    /// <summary>O exe de uma linha <c>"C:\...\ScreenCord.exe" "%1"</c>.</summary>
    public static string? RegisteredExe(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>O link de convite entre os argumentos (o Windows passa só ele, no lugar de <c>%1</c>).</summary>
    public static string? FindLink(IEnumerable<string> args) => args.FirstOrDefault(RoomInvite.IsInviteLink);
}
