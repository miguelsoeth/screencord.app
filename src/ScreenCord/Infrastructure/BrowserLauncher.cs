using System.Diagnostics;

namespace ScreenCord.Infrastructure;

public interface IBrowserLauncher
{
    /// <summary>Abre a URL no navegador padrão do sistema.</summary>
    void Open(Uri url);
}

public sealed class BrowserLauncher : IBrowserLauncher
{
    public void Open(Uri url)
    {
        // UseShellExecute entrega a URL ao shell; restringir a https evita executar outros esquemas/arquivos.
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Somente URLs https podem ser abertas.", nameof(url));
        }

        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new DiscordException(
                DiscordErrorKind.Configuration,
                "Não foi possível abrir o navegador padrão do Windows.",
                ex);
        }
    }
}
