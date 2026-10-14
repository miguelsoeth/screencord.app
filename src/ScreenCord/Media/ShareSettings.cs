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

public enum ShareQuality
{
    /// <summary>2000 kbps.</summary>
    Low,

    /// <summary>4000 kbps.</summary>
    Medium,

    /// <summary>5000 kbps.</summary>
    High,
}

/// <summary>Configuração do compartilhamento de tela. O padrão é 720p, 30 fps, qualidade média, com áudio.</summary>
public sealed record ShareSettings(
    ShareResolution Resolution = ShareResolution.P720,
    ShareFrameRate FrameRate = ShareFrameRate.Fps30,
    ShareQuality Quality = ShareQuality.Medium,
    bool ShareAudio = true)
{
    public static ShareSettings Default { get; } = new();

    public int Height => (int)Resolution;

    public int Fps => (int)FrameRate;

    public int BitrateKbps => BitrateOf(Quality);

    public static int BitrateOf(ShareQuality quality) => quality switch
    {
        ShareQuality.Low => 2000,
        ShareQuality.High => 5000,
        _ => 4000,
    };

    public static string LabelOf(ShareQuality quality) => quality switch
    {
        ShareQuality.Low => "Baixo",
        ShareQuality.High => "Alto",
        _ => "Médio",
    };

    /// <summary>Resumo para a interface: "720p · 30 fps · Médio (4000 kbps)".</summary>
    public string Summary => $"{Height}p · {Fps} fps · {LabelOf(Quality)} ({BitrateKbps} kbps)";
}

/// <summary>O que compartilhar e como, escolhido na janela de compartilhamento.</summary>
public sealed record ShareRequest(CaptureSource Source, ShareSettings Settings);
