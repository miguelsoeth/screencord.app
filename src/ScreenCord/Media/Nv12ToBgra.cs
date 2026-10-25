namespace ScreenCord.Media;

/// <summary>
/// NV12 → BGRA na CPU, para a prévia da câmera (um quadro pequeno por vez; a transmissão não passa por aqui: o
/// encoder lê o NV12 direto). Usa a matriz BT.601 de faixa limitada, a mesma que as webcams costumam entregar.
/// </summary>
public static class Nv12ToBgra
{
    /// <summary>Converte um quadro NV12 contíguo (plano Y seguido do UV intercalado) em <paramref name="bgra"/> (largura × altura × 4).</summary>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra)
    {
        var uvBase = width * height;
        for (var y = 0; y < height; y++)
        {
            var luma = y * width;
            var chroma = uvBase + (y >> 1) * width;
            var output = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var c = Math.Max(nv12[luma + x] - 16, 0) * 298;
                var uv = chroma + (x & ~1);
                var d = nv12[uv] - 128;
                var e = nv12[uv + 1] - 128;

                bgra[output] = Clamp((c + 516 * d + 128) >> 8);
                bgra[output + 1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                bgra[output + 2] = Clamp((c + 409 * e + 128) >> 8);
                bgra[output + 3] = 255;
                output += 4;
            }
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
