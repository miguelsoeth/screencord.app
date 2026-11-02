using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ScreenCord.Infrastructure;

/// <summary>Área de transferência (para copiar o convite da sala local).</summary>
public interface IClipboard
{
    /// <summary>Copia o texto; false se outro programa estava usando a área de transferência.</summary>
    /// <param name="sensitive">O texto dá acesso a algo (o convite tem o token): fica fora do histórico e da sincronização do Windows.</param>
    bool TrySetText(string text, bool sensitive = false);
}

public sealed class WpfClipboard : IClipboard
{
    public bool TrySetText(string text, bool sensitive = false)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text);
            if (sensitive)
            {
                // Formatos que o Windows lê para não guardar no histórico (Win+V) nem mandar para a nuvem.
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            }

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
