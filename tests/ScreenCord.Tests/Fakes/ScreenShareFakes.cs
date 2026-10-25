using ScreenCord.Contracts;
using ScreenCord.Media;
using ScreenCord.Services;
using ScreenCord.ViewModels;

namespace ScreenCord.Tests.Fakes;

/// <summary>Compartilhamento simulado: nada é capturado; os testes encerram ou observam as chamadas.</summary>
public sealed class FakeScreenShareService : IScreenShareService
{
    public Guid? CurrentUserId { get; set; } = FakeGroupsService.Me.UserId;

    public List<string> Calls { get; } = [];

    public List<FakeActiveShare> Started { get; } = [];

    public List<FakeStreamViewer> Watching { get; } = [];

    public Exception? StartFailure { get; set; }

    /// <summary>Simula a captura de áudio falhando: o compartilhamento começa sem som e com este aviso.</summary>
    public string? AudioFailure { get; set; }

    public Task<IActiveScreenShare> StartAsync(Guid groupId, ShareRequest request, IActiveScreenShare? replacing = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"{(replacing is null ? "start" : "change")}:{groupId}:{request.Settings.Height}p{request.Settings.Fps}:{request.Settings.BitrateKbps}");
        if (StartFailure is { } failure)
        {
            return Task.FromException<IActiveScreenShare>(failure);
        }

        // Como o serviço real: o compartilhamento substituído para sem disparar Ended.
        if (replacing is FakeActiveShare previous)
        {
            previous.Replace();
        }

        var settings = request.Settings;
        var share = new FakeActiveShare(
            new ScreenShareDto(FakeGroupsService.Me.UserId, settings.Height * 16 / 9, settings.Height, settings.Fps, settings.BitrateKbps, settings.ShareAudio && AudioFailure is null),
            request)
        {
            AudioWarning = settings.ShareAudio ? AudioFailure : null,
        };
        Started.Add(share);
        return Task.FromResult<IActiveScreenShare>(share);
    }

    public Task<IStreamViewer> WatchAsync(Guid groupId, ScreenShareDto share, CancellationToken cancellationToken = default)
    {
        Calls.Add($"watch:{groupId}:{share.UserId}");
        var viewer = new FakeStreamViewer(share.HasAudio);
        Watching.Add(viewer);
        return Task.FromResult<IStreamViewer>(viewer);
    }
}

public sealed class FakeActiveShare(ScreenShareDto share, ShareRequest request) : IActiveScreenShare
{
    public ScreenShareDto Share { get; } = share;

    public ShareRequest Request { get; } = request;

    public string? AudioWarning { get; set; }

    public bool IsStopped { get; private set; }

    public event EventHandler<string>? Ended;

    /// <summary>Parou por ter sido substituído por um compartilhamento com outras configurações.</summary>
    public bool IsReplaced { get; private set; }

    public Task StopAsync()
    {
        IsStopped = true;
        return Task.CompletedTask;
    }

    public void Replace()
    {
        IsReplaced = true;
        IsStopped = true;
    }

    /// <summary>Simula o fim espontâneo (ex.: a janela compartilhada fechou).</summary>
    public void End(string reason)
    {
        IsStopped = true;
        Ended?.Invoke(this, reason);
    }
}

public sealed class FakeStreamViewer(bool hasAudio) : IStreamViewer
{
    public event DecodedFrameHandler? FrameReady;

    public event EventHandler<string?>? Ended;

    public bool HasAudio { get; } = hasAudio;

    public float Volume { get; set; } = 1;

    public bool IsMuted { get; set; }

    public bool IsVideoPaused { get; set; }

    public bool IsDisposed { get; private set; }

    public void RaiseFrame() => FrameReady?.Invoke(new byte[16], 2, 2, 0);

    public void RaiseEnded(string? reason) => Ended?.Invoke(this, reason);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Janelas simuladas: a escolha devolvida é configurável e as janelas de espectador ficam numa lista.</summary>
public sealed class FakeScreenShareUi : IScreenShareUi
{
    public static readonly CaptureSource Screen = new(CaptureSourceKind.Screen, 1, "Tela 1 (principal)", "", 1920, 1080, 0);

    public Func<ShareSettings, ShareRequest?> OnPick { get; set; } = settings => new ShareRequest(Screen, settings);

    public List<ShareSettings> Picks { get; } = [];

    public List<StreamViewerViewModel> Open { get; } = [];

    public List<StreamViewerViewModel> Activated { get; } = [];

    /// <summary>Origem atual recebida em cada escolha (null ao começar um compartilhamento novo).</summary>
    public List<CaptureSource?> PickCurrents { get; } = [];

    /// <summary>Limites do caminho da mídia recebidos em cada escolha.</summary>
    public List<StreamLimits> PickLimits { get; } = [];

    /// <summary>O que o grupo aceitava transmitir (null: sem restrição) em cada escolha.</summary>
    public List<GroupSettings?> PickAllowed { get; } = [];

    public ShareRequest? PickShare(ShareSettings initial, StreamLimits limits, CaptureSource? current = null, GroupSettings? allowed = null)
    {
        Picks.Add(initial);
        PickLimits.Add(limits);
        PickCurrents.Add(current);
        PickAllowed.Add(allowed);
        return OnPick(initial);
    }

    public void ShowViewer(StreamViewerViewModel viewer, Action closed)
    {
        Open.Add(viewer);
        viewer.CloseRequested += (_, _) =>
        {
            if (Open.Remove(viewer))
            {
                closed();
            }
        };
    }

    public void ActivateViewer(StreamViewerViewModel viewer) => Activated.Add(viewer);
}
