using ScreenCord.Contracts;

namespace ScreenCord.Media;

/// <summary>Combinação de resolução e fps oferecida no compartilhamento.</summary>
public enum ShareQuality
{
    P540Fps60,
    P720Fps30,

    /// <summary>Reservada para um plano pago: aparece desligada na escolha e a API ainda não aceita.</summary>
    P1080Fps60,
}

/// <summary>
/// Configuração do compartilhamento de tela. O padrão é 720p, 30 fps, com áudio. A taxa de bits não é escolhida:
/// começa no teto (<see cref="ScreenShareLimits.MaxBitrateKbps"/>) e o <see cref="BitrateController"/> a adapta à rede.
/// </summary>
public sealed record ShareSettings(
    ShareQuality Quality = ShareQuality.P720Fps30,
    bool ShareAudio = true)
{
    public static ShareSettings Default { get; } = new();

    public int Height => HeightOf(Quality);

    public int Fps => FpsOf(Quality);

    /// <summary>Taxa com que o compartilhamento começa e que é anunciada na sala.</summary>
    public int BitrateKbps => ScreenShareLimits.MaxBitrateKbps;

    public static int HeightOf(ShareQuality quality) => quality switch
    {
        ShareQuality.P540Fps60 => 540,
        ShareQuality.P720Fps30 => 720,
        _ => 1080,
    };

    public static int FpsOf(ShareQuality quality) => quality == ShareQuality.P720Fps30 ? 30 : 60;

    /// <summary>A combinação está dentro dos limites da API (as outras ficam desligadas na escolha).</summary>
    public static bool IsAvailable(ShareQuality quality) => ScreenShareLimits.IsValidQuality(HeightOf(quality), FpsOf(quality));

    /// <summary>Resumo para a interface: "720p · 30 fps".</summary>
    public string Summary => $"{Height}p · {Fps} fps";
}

/// <summary>O que compartilhar e como, escolhido na janela de compartilhamento.</summary>
public sealed record ShareRequest(CaptureSource Source, ShareSettings Settings);
