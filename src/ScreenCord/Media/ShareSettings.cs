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
/// Configuração do compartilhamento de tela. O padrão é 720p, 30 fps, 4000 kbps, com áudio. A taxa de bits vai
/// de <see cref="ScreenShareLimits.MinBitrateKbps"/> a <see cref="ScreenShareLimits.MaxBitrateKbps"/>, de
/// <see cref="ScreenShareLimits.BitrateStepKbps"/> em <see cref="ScreenShareLimits.BitrateStepKbps"/>.
/// </summary>
public sealed record ShareSettings(
    ShareResolution Resolution = ShareResolution.P720,
    ShareFrameRate FrameRate = ShareFrameRate.Fps30,
    int BitrateKbps = ShareSettings.DefaultBitrateKbps,
    bool ShareAudio = true)
{
    public const int DefaultBitrateKbps = 4000;

    public static ShareSettings Default { get; } = new();

    public int Height => (int)Resolution;

    public int Fps => (int)FrameRate;

    /// <summary>Leva um valor qualquer (ex.: do slide) para a taxa válida mais próxima.</summary>
    public static int SnapBitrate(double kbps)
    {
        var steps = Math.Round((kbps - ScreenShareLimits.MinBitrateKbps) / ScreenShareLimits.BitrateStepKbps, MidpointRounding.AwayFromZero);
        var snapped = ScreenShareLimits.MinBitrateKbps + (int)steps * ScreenShareLimits.BitrateStepKbps;
        return Math.Clamp(snapped, ScreenShareLimits.MinBitrateKbps, ScreenShareLimits.MaxBitrateKbps);
    }

    /// <summary>Resumo para a interface: "720p · 30 fps · 4000 kbps".</summary>
    public string Summary => $"{Height}p · {Fps} fps · {BitrateKbps} kbps";
}

/// <summary>O que compartilhar e como, escolhido na janela de compartilhamento.</summary>
public sealed record ShareRequest(CaptureSource Source, ShareSettings Settings);
