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
    /// Começa a capturar <paramref name="source"/>. Câmeras usam o Media Foundation. Telas, onde o Windows não deixa tirar a borda amarela do
    /// Windows Graphics Capture (Windows 10), são capturadas com o Desktop Duplication, que não tem borda.
    /// </summary>
    public static IScreenCapture Create(CaptureSource source, int targetHeight, int frameRate, ILogger? logger = null)
    {
        if (source.IsCamera)
        {
            return new CameraCapture(source.DeviceId ?? throw new ArgumentException("Câmera sem identificador.", nameof(source)), targetHeight, frameRate, logger);
        }

        if (source.IsScreen && !GraphicsCapture.CanHideBorder)
        {
            try
            {
                return new DesktopDuplicationCapture(source.Handle, targetHeight, frameRate, logger);
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

    /// <summary>
    /// Onde a imagem da origem fica no quadro de saída quando as proporções não batem (ex.: a janela foi
    /// redimensionada ou a tela girou no meio da transmissão): o maior retângulo com a proporção da origem,
    /// centralizado, com posição e lados pares (NV12) e pelo menos 2 × 2. O resto fica preto.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) Letterbox(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth < 2 || outputHeight < 2)
        {
            return (0, 0, outputWidth & ~1, outputHeight & ~1);
        }

        var scale = Math.Min(outputWidth / (double)sourceWidth, outputHeight / (double)sourceHeight);
        var width = Math.Clamp((int)Math.Round(sourceWidth * scale) & ~1, 2, outputWidth & ~1);
        var height = Math.Clamp((int)Math.Round(sourceHeight * scale) & ~1, 2, outputHeight & ~1);
        return ((outputWidth - width) / 2 & ~1, (outputHeight - height) / 2 & ~1, width, height);
    }
}
