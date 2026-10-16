using ScreenCord.Contracts;

namespace ScreenCord.Media;

public enum ShareResolution
{
    P540 = 540,
    P720 = 720,

    /// <summary>Só no Pro: no plano gratuito aparece desligada na escolha.</summary>
    P1080 = 1080,
}

public enum ShareFrameRate
{
    Fps30 = 30,
    Fps60 = 60,
}

/// <summary>
/// Configuração do compartilhamento de tela. O padrão é 720p, 30 fps, com áudio. O que cada plano permite está
/// em <see cref="PlanLimits"/> (no gratuito, 60 fps só em 540p). A taxa de bits não é escolhida: começa no teto
/// do plano e o <see cref="BitrateController"/> a adapta à rede.
/// </summary>
/// <param name="BitrateKbps">Taxa com que o compartilhamento começa e que é anunciada na sala: o teto do plano.</param>
public sealed record ShareSettings(
    ShareResolution Resolution = ShareResolution.P720,
    ShareFrameRate FrameRate = ShareFrameRate.Fps30,
    bool ShareAudio = true,
    int BitrateKbps = ScreenShareLimits.MaxBitrateKbps)
{
    public static ShareSettings Default { get; } = new();

    public int Height => (int)Resolution;

    public int Fps => (int)FrameRate;

    /// <summary>A resolução tem ao menos um fps dentro do plano (do gratuito, sem <paramref name="limits"/>).</summary>
    public static bool IsAvailable(ShareResolution resolution, PlanLimits? limits = null) =>
        Enum.GetValues<ShareFrameRate>().Any(fps => IsAvailable(resolution, fps, limits));

    /// <summary>A combinação está dentro do plano (as outras ficam desligadas na escolha).</summary>
    public static bool IsAvailable(ShareResolution resolution, ShareFrameRate frameRate, PlanLimits? limits = null) =>
        (limits ?? PlanLimits.Free).AllowsQuality((int)resolution, (int)frameRate);

    /// <summary>Resumo para a interface: "720p · 30 fps".</summary>
    public string Summary => $"{Height}p · {Fps} fps";
}

/// <summary>O que compartilhar e como, escolhido na janela de compartilhamento.</summary>
public sealed record ShareRequest(CaptureSource Source, ShareSettings Settings);
