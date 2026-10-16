using Microsoft.Extensions.Logging;

namespace ScreenCord.Media;

/// <summary>
/// Captura de uma tela ou janela, convertida para NV12 no tamanho de saída.
/// Não é thread-safe: <see cref="Update"/> e <see cref="ReadNv12"/> devem ser chamados pela mesma thread.
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>A janela capturada foi fechada (ou a tela foi desconectada).</summary>
    event EventHandler? Closed;

    /// <summary>Método de captura, para os logs.</summary>
    string Name { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>Pega o quadro mais recente, se houver um novo, e o converte. Retorna verdadeiro se havia quadro novo.</summary>
    bool Update();

    /// <summary>
    /// Copia o último quadro convertido para <paramref name="destination"/> em NV12 contíguo (plano Y com
    /// <see cref="Width"/> bytes por linha, seguido do plano UV intercalado). Retorna falso antes do primeiro quadro.
    /// </summary>
    bool ReadNv12(Span<byte> destination);
}

public static class ScreenCapture
{
    public static bool IsSupported => GraphicsCapture.IsSupported;

    /// <summary>
    /// Começa a capturar <paramref name="source"/>. Telas, onde o Windows não deixa tirar a borda amarela do
    /// Windows Graphics Capture (Windows 10), são capturadas com o Desktop Duplication, que não tem borda.
    /// </summary>
    public static IScreenCapture Create(CaptureSource source, int targetHeight, int frameRate, ILogger? logger = null)
    {
        if (source.IsScreen && !GraphicsCapture.CanHideBorder)
        {
            try
            {
                return new DesktopDuplicationCapture(source.Handle, targetHeight, frameRate);
            }
            catch (Exception ex)
            {
                // Ex.: tela girada ou placa de vídeo sem suporte; segue com a borda.
                logger?.LogWarning("Desktop Duplication indisponível ({Message}); capturando com o Windows Graphics Capture.", ex.Message);
            }
        }

        return new GraphicsCapture(source, targetHeight, frameRate);
    }

    /// <summary>
    /// Tamanho do vídeo: a altura escolhida, com a largura na proporção da origem; origens menores não são
    /// ampliadas. Os dois lados são pares, como o H.264 em 4:2:0 exige.
    /// </summary>
    public static (int Width, int Height) OutputSize(int sourceWidth, int sourceHeight, int targetHeight)
    {
        sourceWidth = Math.Max(sourceWidth, 2);
        sourceHeight = Math.Max(sourceHeight, 2);
        var height = Math.Min(targetHeight, sourceHeight);
        var width = (int)Math.Round(sourceWidth * (double)height / sourceHeight);
        if (width > Contracts.ScreenShareLimits.MaxWidth)
        {
            width = Contracts.ScreenShareLimits.MaxWidth;
            height = (int)Math.Round(sourceHeight * (double)width / sourceWidth);
        }

        static int Even(int value) => Math.Max(Contracts.ScreenShareLimits.MinSize, value & ~1);
        return (Even(width), Even(height));
    }
}
