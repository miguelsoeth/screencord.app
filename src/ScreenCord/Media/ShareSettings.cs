using ScreenCord.Contracts;

namespace ScreenCord.Media;

public enum ShareResolution
{
    P540 = 540,
    P720 = 720,
    P1080 = 1080,
}

public enum ShareFrameRate
{
    Fps30 = 30,
    Fps60 = 60,
}

/// <summary>
/// Configuração do compartilhamento de tela. O que dá para escolher depende do caminho da mídia
/// (<see cref="StreamLimits"/>): pelo servidor, até 720p a 30 fps; nas salas locais, até 1080p a 60 fps. A taxa de
/// bits não é escolhida: começa no teto do caminho e o <see cref="BitrateController"/> a adapta à rede.
/// </summary>
/// <param name="BitrateKbps">Taxa com que o compartilhamento começa e que é anunciada na sala: o teto do caminho.</param>
public sealed record ShareSettings(
    ShareResolution Resolution = ShareResolution.P720,
    ShareFrameRate FrameRate = ShareFrameRate.Fps30,
    bool ShareAudio = true,
    int BitrateKbps = ScreenShareLimits.MaxBitrateKbps)
{
    /// <summary>Pelo servidor: 720p, 30 fps, com áudio.</summary>
    public static ShareSettings Default { get; } = new();

    public int Height => (int)Resolution;

    public int Fps => (int)FrameRate;

    /// <summary>O padrão de um caminho: a maior resolução que ele permite, a 30 fps, com áudio.</summary>
    public static ShareSettings DefaultFor(StreamLimits limits)
    {
        var resolution = Enum.GetValues<ShareResolution>()
            .Where(r => IsAvailable(r, ShareFrameRate.Fps30, limits))
            .DefaultIfEmpty(ShareResolution.P540)
            .Max();
        return new ShareSettings(resolution, ShareFrameRate.Fps30, true, limits.MaxBitrateKbps);
    }

    /// <summary>A resolução tem ao menos um fps dentro do caminho.</summary>
    public static bool IsAvailable(ShareResolution resolution, StreamLimits limits) =>
        Enum.GetValues<ShareFrameRate>().Any(fps => IsAvailable(resolution, fps, limits));

    /// <summary>A combinação cabe no caminho (as outras ficam desligadas na escolha).</summary>
    public static bool IsAvailable(ShareResolution resolution, ShareFrameRate frameRate, StreamLimits limits) =>
        limits.AllowsQuality((int)resolution, (int)frameRate);

    /// <summary>Resumo para a interface: "720p · 30 fps".</summary>
    public string Summary => $"{Height}p · {Fps} fps";
}

/// <summary>O que compartilhar e como, escolhido na janela de compartilhamento.</summary>
public sealed record ShareRequest(CaptureSource Source, ShareSettings Settings);
