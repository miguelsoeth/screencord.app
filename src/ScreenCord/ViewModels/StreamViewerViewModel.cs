using ScreenCord.Localization;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Media;
using ScreenCord.Models;
using ScreenCord.Services;

namespace ScreenCord.ViewModels;

public enum StreamViewerState
{
    Connecting,
    Live,
    Ended,
}

/// <summary>
/// Janela que mostra a tela de outra pessoa da sala. Os quadros chegam por <see cref="FrameReady"/> numa
/// thread de fundo; a janela os desenha. O estado e os textos são atualizados na thread da interface.
/// </summary>
public sealed class StreamViewerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<ScreenShareDto, CancellationToken, Task<IStreamViewer>> _open;
    private readonly SynchronizationContext? _ui;
    private readonly CancellationTokenSource _lifetime = new();
    private IStreamViewer? _viewer;
    private StreamViewerState _state = StreamViewerState.Connecting;
    private ScreenShareDto _share;
    private string? _message;
    private double _volume = 1.0;
    private bool _isMuted;
    private bool _isVideoPaused;
    private bool _isPinned;
    private bool _canPin;
    private bool _hasFrame;
    private bool _disposed;

    public StreamViewerViewModel(IScreenShareService service, Guid groupId, string groupName, Person sharer, ScreenShareDto share)
        : this(groupId, groupName, sharer, share, (current, token) => service.WatchAsync(groupId, current, token), isPreview: false)
    {
    }

    /// <summary>A própria transmissão na grade: os quadros vêm direto da captura (<see cref="IActiveScreenShare.OpenPreview"/>), sem rede.</summary>
    public static StreamViewerViewModel ForPreview(IActiveScreenShare active, Guid groupId, string groupName, Person me) =>
        new(groupId, groupName, me, active.Share, (_, _) => Task.FromResult(active.OpenPreview()), isPreview: true);

    private StreamViewerViewModel(
        Guid groupId, string groupName, Person sharer, ScreenShareDto share,
        Func<ScreenShareDto, CancellationToken, Task<IStreamViewer>> open, bool isPreview)
    {
        _open = open;
        IsPreview = isPreview;
        _ui = SynchronizationContext.Current;
        GroupId = groupId;
        GroupName = groupName;
        Sharer = sharer;
        _share = share;

        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
    }

    /// <summary>Quadro decodificado (BGRA), numa thread de fundo. O buffer só vale durante a chamada.</summary>
    public event DecodedFrameHandler? FrameReady;

    /// <summary>A janela deve fechar (ex.: você saiu da sala).</summary>
    public event EventHandler? CloseRequested;

    public Guid GroupId { get; }

    public string GroupName { get; }

    public Person Sharer { get; }

    public Guid SharerId => Sharer.UserId;

    /// <summary>Tela ou câmera: a mesma pessoa pode ter as duas abertas, cada uma no seu lugar da grade.</summary>
    public ShareKind Kind => _share.Kind;

    /// <summary>É a prévia da sua própria transmissão, não uma conexão a outra pessoa; some quando a transmissão acaba.</summary>
    public bool IsPreview { get; }

    public string Title => IsPreview
        ? string.Format(Loc.Culture, _share.Kind == ShareKind.Camera ? Strings.ViewerOwnCameraTitleFormat : Strings.ViewerOwnTitleFormat, GroupName)
        : string.Format(Loc.Culture, _share.Kind == ShareKind.Camera ? Strings.ViewerCameraTitleFormat : Strings.ViewerTitleFormat, Sharer.DisplayName, GroupName);

    public RelayCommand ToggleMuteCommand { get; }

    public RelayCommand TogglePinCommand { get; }

    /// <summary>Esta é a transmissão principal: fica grande e as outras em miniatura ao lado. A janela garante que só uma esteja fixada.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinTooltip));
            }
        }
    }

    /// <summary>Só há o que fixar quando há mais de uma transmissão aberta.</summary>
    public bool CanPin
    {
        get => _canPin;
        set => SetProperty(ref _canPin, value);
    }

    public string PinTooltip => IsPinned ? Strings.UnpinTooltip : Strings.PinTooltip;

    public StreamViewerState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsEnded));
                OnPropertyChanged(nameof(ShowOverlay));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsConnecting => State == StreamViewerState.Connecting;

    public bool IsEnded => State == StreamViewerState.Ended;

    /// <summary>Mensagem sobre o vídeo enquanto não há imagem ou depois que a transmissão acaba.</summary>
    public bool ShowOverlay => State != StreamViewerState.Live || !_hasFrame;

    public string StatusText => State switch
    {
        StreamViewerState.Connecting => Strings.Connecting,
        StreamViewerState.Ended => _message ?? Strings.ShareEnded,
        _ => Strings.WaitingForImage,
    };

    /// <summary>"1280 × 720 · 30 fps · 4000 kbps".</summary>
    public string QualityText => $"{_share.Width} × {_share.Height} · {_share.FrameRate} fps · {_share.BitrateKbps} kbps";

    public bool HasAudio => _share.HasAudio && !IsPreview;

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 1)) && _viewer is { } viewer)
            {
                viewer.Volume = (float)_volume;
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value) && _viewer is { } viewer)
            {
                viewer.IsMuted = value;
            }
        }
    }

    /// <summary>A janela foi minimizada: só o áudio continua chegando.</summary>
    public bool IsVideoPaused
    {
        get => _isVideoPaused;
        set
        {
            if (SetProperty(ref _isVideoPaused, value) && _viewer is { } viewer)
            {
                viewer.IsVideoPaused = value;
            }
        }
    }

    public async Task StartAsync()
    {
        State = StreamViewerState.Connecting;
        try
        {
            var viewer = await _open(_share, _lifetime.Token);
            if (_disposed)
            {
                await viewer.DisposeAsync();
                return;
            }

            viewer.Volume = (float)Volume;
            viewer.IsMuted = IsMuted;
            viewer.IsVideoPaused = IsVideoPaused;
            viewer.FrameReady += OnFrame;
            viewer.Ended += (_, reason) => Post(() => End(reason));
            _viewer = viewer;
            State = StreamViewerState.Live;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            End(ErrorMessages.For(ex));
        }
    }

    /// <summary>A presença trouxe uma configuração nova (ex.: recomeçou com outra resolução) ou o fim do compartilhamento.</summary>
    public void UpdateShare(ScreenShareDto? share)
    {
        if (share is null)
        {
            // Quem compartilhava parou: a tela sai sozinha, mesmo que o canal de vídeo tenha caído
            // antes de a presença chegar (a API pode derrubar a conexão em vez de fechá-la).
            if (State != StreamViewerState.Ended)
            {
                End(null);
            }
            else
            {
                RequestClose();
            }

            return;
        }

        _share = share;
        OnPropertyChanged(nameof(QualityText));
        OnPropertyChanged(nameof(HasAudio));
    }

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_viewer is { } viewer)
        {
            _viewer = null;
            viewer.FrameReady -= OnFrame;
            await viewer.DisposeAsync();
        }

        _lifetime.Dispose();
    }

    private void OnFrame(byte[] bgra, int width, int height, long timestamp)
    {
        if (!_hasFrame)
        {
            _hasFrame = true;
            Post(() => OnPropertyChanged(nameof(ShowOverlay)));
        }

        FrameReady?.Invoke(bgra, width, height, timestamp);
    }

    private void End(string? reason)
    {
        _message = reason;
        State = StreamViewerState.Ended;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowOverlay));
        if (_viewer is { } viewer)
        {
            _viewer = null;
            viewer.FrameReady -= OnFrame;
            _ = viewer.DisposeAsync().AsTask();
        }

        // Quem compartilhava parou: a tela sai sozinha. Com erro, fica aberta mostrando o motivo.
        if (reason is null)
        {
            RequestClose();
        }
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
