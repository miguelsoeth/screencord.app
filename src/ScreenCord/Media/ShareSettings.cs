using ScreenCord.Contracts;

namespace ScreenCord.Media;

public enum ShareResolution
{
    P540 = 540,
    P720 = 720,

    /// <summary>Reservada para um plano pago: aparece desligada na escolha e a API ainda não aceita.</summary>
    P1080 = 1080,
}

public enum ShareFrameRate
{
    Fps30 = 30,
    Fps60 = 60,
}

/// <summary>
/// Configuração do compartilhamento de tela. O padrão é 720p, 30 fps, com áudio. 60 fps só em 540p
/// (<see cref="ScreenShareLimits.IsValidQuality"/>). A taxa de bits não é escolhida: começa no teto
/// (<see cref="ScreenShareLimits.MaxBitrateKbps"/>) e o <see cref="BitrateController"/> a adapta à rede.
/// </summary>
public sealed record ShareSettings(
    ShareResolution Resolution = ShareResolution.P720,
    ShareFrameRate FrameRate = ShareFrameRate.Fps30,
    bool ShareAudio = true)
{
    public static ShareSettings Default { get; } = new();

    public int Height => (int)Resolution;

    public int Fps => (int)FrameRate;

    /// <summary>Taxa com que o compartilhamento começa e que é anunciada na sala.</summary>
    public int BitrateKbps => ScreenShareLimits.MaxBitrateKbps;

    /// <summary>A resolução tem ao menos um fps dentro dos limites da API.</summary>
    public static bool IsAvailable(ShareResolution resolution) =>
        Enum.GetValues<ShareFrameRate>().Any(fps => IsAvailable(resolution, fps));

    /// <summary>A combinação está dentro dos limites da API (as outras ficam desligadas na escolha).</summary>
    public static bool IsAvailable(ShareResolution resolution, ShareFrameRate frameRate) =>
        ScreenShareLimits.IsValidQuality((int)resolution, (int)frameRate);

    /// <summary>Resumo para a interface: "720p · 30 fps".</summary>
    public string Summary => $"{Height}p · {Fps} fps";
}

/// <summary>O que compartilhar e como, escolhido na janela de compartilhamento.</summary>
public sealed record ShareRequest(CaptureSource Source, ShareSettings Settings);
