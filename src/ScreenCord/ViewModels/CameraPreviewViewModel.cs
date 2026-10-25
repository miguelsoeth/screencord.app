using System.Collections.ObjectModel;
using ScreenCord.Contracts;
using ScreenCord.Localization;
using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>
/// Janela de ligar a câmera: mostra a prévia da câmera escolhida, com Cancelar e Ativar câmera. A prévia abre a câmera
/// em tamanho pequeno só para a pessoa se ver; nada é transmitido até ela ativar. Quem cria a janela descarta o
/// view model depois de fechá-la, antes de a transmissão abrir a câmera de novo (algumas só aceitam um uso por vez).
/// </summary>
public sealed class CameraPreviewViewModel : ObservableObject, IDisposable
{
    private const int PreviewHeight = 480;
    private const int PreviewFps = 30;

    private readonly StreamLimits _limits;
    private readonly Func<IReadOnlyList<CaptureSource>> _cameras;
    private readonly Func<CaptureSource, IScreenCapture> _open;
    private readonly SynchronizationContext? _ui;
    private readonly Lock _lock = new();
    private IScreenCapture? _capture;
    private byte[] _nv12 = [];
    private CaptureSource? _selected;
    private string? _error;
    private bool _isOpening;
    private bool _hasPreview;
    private bool _loaded;
    private bool _disposed;
    private int _version;

    /// <param name="cameras">Lista das câmeras (padrão: as do Windows).</param>
    /// <param name="open">Abre a prévia de uma câmera (padrão: a captura da câmera em tamanho pequeno).</param>
    public CameraPreviewViewModel(
        StreamLimits limits,
        Func<IReadOnlyList<CaptureSource>>? cameras = null,
        Func<CaptureSource, IScreenCapture>? open = null)
    {
        _limits = limits;
        _cameras = cameras ?? CaptureSources.Cameras;
        _open = open ?? (source => ScreenCapture.Create(source, PreviewHeight, PreviewFps));
        _ui = SynchronizationContext.Current;
    }

    public ObservableCollection<CaptureSource> Cameras { get; } = [];

    /// <summary>Há mais de uma câmera: a escolha aparece.</summary>
    public bool HasChoice => Cameras.Count > 1;

    /// <summary>Procurou as câmeras e não achou nenhuma.</summary>
    public bool HasNoCameras => _loaded && Cameras.Count == 0;

    public CaptureSource? SelectedCamera
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                _ = OpenAsync();
            }
        }
    }

    /// <summary>Texto do que impede a prévia (ex.: a câmera está em uso por outro programa).</summary>
    public string? ErrorText
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(CanActivate));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>A câmera está abrindo.</summary>
    public bool IsOpening
    {
        get => _isOpening;
        private set => SetProperty(ref _isOpening, value);
    }

    /// <summary>A prévia está chegando; só então dá para ativar.</summary>
    public bool HasPreview
    {
        get => _hasPreview;
        private set
        {
            if (SetProperty(ref _hasPreview, value))
            {
                OnPropertyChanged(nameof(CanActivate));
            }
        }
    }

    public bool CanActivate => HasPreview && SelectedCamera is not null && !HasError;

    public string EmptyText => Strings.NoCamerasFound;

    /// <summary>Procura as câmeras e abre a prévia da primeira.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<CaptureSource> cameras;
        try
        {
            cameras = await Task.Run(_cameras);
        }
        catch (Exception)
        {
            cameras = [];
        }

        if (_disposed)
        {
            return;
        }

        foreach (var camera in cameras)
        {
            Cameras.Add(camera);
        }

        _loaded = true;
        OnPropertyChanged(nameof(HasChoice));
        OnPropertyChanged(nameof(HasNoCameras));
        SelectedCamera = Cameras.FirstOrDefault();
    }

    /// <summary>
    /// Copia o quadro novo da prévia, já em BGRA (<paramref name="bgra"/> é criado ou refeito se o tamanho mudou).
    /// Retorna falso se não há quadro novo. Chamado pela thread da interface, no ritmo da prévia.
    /// </summary>
    public bool TryReadFrame(ref byte[]? bgra, out int width, out int height)
    {
        width = height = 0;
        lock (_lock)
        {
            if (_capture is not { } capture || !capture.Update())
            {
                return false;
            }

            width = capture.Width;
            height = capture.Height;
            var size = width * height * 3 / 2;
            if (_nv12.Length != size)
            {
                _nv12 = new byte[size];
            }

            if (!capture.ReadNv12(_nv12))
            {
                return false;
            }

            if (bgra is null || bgra.Length != width * height * 4)
            {
                bgra = new byte[width * height * 4];
            }

            Nv12ToBgra.Convert(_nv12, width, height, bgra);
            return true;
        }
    }

    /// <summary>O pedido para transmitir a câmera escolhida, na qualidade que o caminho da mídia permite.</summary>
    public ShareRequest? BuildRequest() =>
        CanActivate ? new ShareRequest(SelectedCamera!, ShareSettings.ForCamera(_limits)) : null;

    public void Dispose()
    {
        _disposed = true;
        _version++;
        CloseCapture();
    }

    private async Task OpenAsync()
    {
        var version = ++_version;
        var camera = SelectedCamera;
        CloseCapture();
        ErrorText = null;
        HasPreview = false;
        if (camera is null)
        {
            return;
        }

        IsOpening = true;
        IScreenCapture? capture = null;
        try
        {
            // A câmera demora para abrir (e algumas, para recusar): fora da thread da interface.
            capture = await Task.Run(() => _open(camera));
        }
        catch (Exception)
        {
            if (version == _version)
            {
                ErrorText = Strings.CameraPreviewFailed;
            }
        }

        if (version != _version || _disposed)
        {
            capture?.Dispose();
            return;
        }

        IsOpening = false;
        if (capture is null)
        {
            return;
        }

        capture.Closed += (_, _) => Post(() =>
        {
            if (version == _version)
            {
                CloseCapture();
                HasPreview = false;
                ErrorText = Strings.SharedCameraDisconnected;
            }
        });
        lock (_lock)
        {
            _capture = capture;
        }

        HasPreview = true;
    }

    private void CloseCapture()
    {
        IScreenCapture? capture;
        lock (_lock)
        {
            capture = _capture;
            _capture = null;
        }

        capture?.Dispose();
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
