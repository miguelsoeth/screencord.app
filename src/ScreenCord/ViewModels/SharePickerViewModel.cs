using ScreenCord.Localization;
using System.Collections.ObjectModel;
using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>
/// Janela "Compartilhar tela": escolha entre uma tela ou uma janela, resolução, fps e áudio.
/// As opções fora do caminho da mídia aparecem desligadas: pelo servidor valem os limites da API (padrão 720p a
/// 60 fps ou 1080p a 30 fps); nas salas locais (direto entre os computadores) até 1080p a 60 fps. A taxa de bits é
/// automática, até o teto do caminho.
/// </summary>
public sealed class SharePickerViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<CaptureSource>> _screens;
    private readonly Func<IReadOnlyList<CaptureSource>> _windows;
    private readonly Func<IReadOnlyList<CaptureSource>> _cameras;
    private CaptureSourceKind _kind = CaptureSourceKind.Screen;
    private CaptureSource? _selectedSource;
    private bool _shareAudio;
    private readonly StreamLimits _limits;

    /// <param name="current">Origem do compartilhamento em andamento, quando a escolha é para trocar as configurações dele.</param>
    /// <param name="limits">Limites do caminho da mídia; sem eles, os do servidor.</param>
    /// <param name="allowed">O que o grupo aceita transmitir; só as abas desses tipos aparecem. Sem isso, tudo.</param>
    public SharePickerViewModel(
        ShareSettings initial,
        Func<IReadOnlyList<CaptureSource>>? screens = null,
        Func<IReadOnlyList<CaptureSource>>? windows = null,
        CaptureSource? current = null,
        StreamLimits? limits = null,
        GroupSettings? allowed = null,
        Func<IReadOnlyList<CaptureSource>>? cameras = null)
    {
        Allowed = allowed ?? GroupSettings.Default;
        _limits = limits ?? StreamLimits.Relay;
        // Os limites do servidor mudam no painel da API e podem ser tão altos quanto os das salas locais: só estes são "direto".
        IsDirect = ReferenceEquals(_limits, StreamLimits.Direct);
        _screens = screens ?? CaptureSources.Screens;
        _windows = windows ?? CaptureSources.Windows;
        _cameras = cameras ?? CaptureSources.Cameras;
        _shareAudio = initial.ShareAudio;
        IsChange = current is not null;

        // Abre no primeiro tipo que o grupo aceita.
        if (!Allowed.AllowScreenShare && Allowed.AllowCamera)
        {
            _kind = CaptureSourceKind.Camera;
        }

        if (current is not null)
        {
            // Abre na aba e com a origem que já está sendo compartilhada (o Refresh mantém a escolha).
            _kind = current.Kind;
            _selectedSource = current;
        }

        // Uma configuração fora do caminho (ex.: a última foi numa sala local) volta ao padrão dele.
        var start = ShareSettings.IsAvailable(initial.Resolution, initial.FrameRate, _limits) ? initial : ShareSettings.DefaultFor(_limits);
        Resolutions = new Choice<ShareResolution>(start.Resolution,
            Enum.GetValues<ShareResolution>()
                .Select(r => ShareSettings.IsAvailable(r, _limits)
                    ? new ChoiceOption<ShareResolution>(r, $"{(int)r}p")
                    : new ChoiceOption<ShareResolution>(r, $"{(int)r}p", DirectOnly, isAvailable: false))
                .ToList());
        FrameRates = new Choice<ShareFrameRate>(start.FrameRate,
        [
            new(ShareFrameRate.Fps30, "30 fps"),
            new(ShareFrameRate.Fps60, "60 fps"),
        ]);
        UpdateFrameRates();
        Resolutions.PropertyChanged += (_, _) => UpdateFrameRates();

        ShowScreensCommand = new RelayCommand(() => Kind = CaptureSourceKind.Screen);
        ShowWindowsCommand = new RelayCommand(() => Kind = CaptureSourceKind.Window);
        ShowCamerasCommand = new RelayCommand(() => Kind = CaptureSourceKind.Camera);
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    private static string DirectOnly => Strings.DirectOnly;

    public ObservableCollection<CaptureSource> Sources { get; } = [];

    /// <summary>A mídia vai direto entre os computadores (sala local), sem passar pelo servidor.</summary>
    public bool IsDirect { get; }

    /// <summary>"Qualidade automática, até 3 Mbps · pelo servidor".</summary>
    public string BitrateText =>
        string.Format(
            Loc.Culture,
            Strings.AutoQualityFormat,
            (_limits.MaxBitrateKbps / 1000.0).ToString("0.#", Loc.Culture),
            IsDirect ? Strings.DirectBetweenComputers : Strings.ThroughServer);

    /// <summary>Troca das configurações de um compartilhamento em andamento.</summary>
    public bool IsChange { get; }

    public string HeaderText => IsChange ? Strings.ChangeSharing : Strings.ShareScreen;

    public string ConfirmText => IsChange ? Strings.Apply : Strings.Share;

    /// <summary>Dica abaixo do título; só aparece ao alterar um compartilhamento em andamento.</summary>
    public string HintText => Strings.ChangeShareHint;

    public RelayCommand ShowScreensCommand { get; }

    public RelayCommand ShowWindowsCommand { get; }

    public RelayCommand ShowCamerasCommand { get; }

    public RelayCommand RefreshCommand { get; }

    /// <summary>O que o grupo aceita transmitir: as abas de tela e janela dependem de <see cref="GroupSettings.AllowScreenShare"/>; a de câmera, de <see cref="GroupSettings.AllowCamera"/>.</summary>
    public GroupSettings Allowed { get; }

    public bool AllowsScreens => Allowed.AllowScreenShare;

    public bool AllowsCameras => Allowed.AllowCamera;

    public CaptureSourceKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsScreenKind));
                OnPropertyChanged(nameof(IsWindowKind));
                OnPropertyChanged(nameof(IsCameraKind));
                OnPropertyChanged(nameof(ShowAudioOption));
                Refresh();
            }
        }
    }

    public bool IsCameraKind
    {
        get => Kind == CaptureSourceKind.Camera;
        set
        {
            if (value)
            {
                Kind = CaptureSourceKind.Camera;
            }
        }
    }

    /// <summary>A câmera não leva áudio (o som capturado é o do programa ou do computador, não o microfone).</summary>
    public bool ShowAudioOption => Kind != CaptureSourceKind.Camera;

    public bool IsScreenKind
    {
        get => Kind == CaptureSourceKind.Screen;
        set
        {
            if (value)
            {
                Kind = CaptureSourceKind.Screen;
            }
        }
    }

    public bool IsWindowKind
    {
        get => Kind == CaptureSourceKind.Window;
        set
        {
            if (value)
            {
                Kind = CaptureSourceKind.Window;
            }
        }
    }

    public CaptureSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                OnPropertyChanged(nameof(CanShare));
            }
        }
    }

    public bool HasNoSources => Sources.Count == 0;

    public string EmptyText => Kind switch
    {
        CaptureSourceKind.Screen => Strings.NoScreensFound,
        CaptureSourceKind.Camera => Strings.NoCamerasFound,
        _ => Strings.NoWindowsToShare,
    };

    public bool CanShare => SelectedSource is not null;

    public Choice<ShareResolution> Resolutions { get; }

    /// <summary>Os fps que a resolução escolhida não permite ficam desligados (ex.: 60 fps em 720p).</summary>
    public Choice<ShareFrameRate> FrameRates { get; }

    public bool ShareAudio
    {
        get => _shareAudio;
        set => SetProperty(ref _shareAudio, value);
    }

    public ShareRequest? BuildRequest() =>
        SelectedSource is { } source
            ? new ShareRequest(source, new ShareSettings(Resolutions.Selected, FrameRates.Selected, ShareAudio && !source.IsCamera, _limits.MaxBitrateKbps))
            : null;

    /// <summary>Liga só os fps permitidos na resolução escolhida; se o marcado deixou de valer, volta a 30 fps.</summary>
    private void UpdateFrameRates()
    {
        var resolution = Resolutions.Selected;
        foreach (var option in FrameRates.Options)
        {
            var available = ShareSettings.IsAvailable(resolution, option.Value, _limits);
            option.SetAvailability(available, available ? null : DirectOnly);
        }

        if (!ShareSettings.IsAvailable(resolution, FrameRates.Selected, _limits))
        {
            FrameRates.Selected = FrameRates.Options.First(o => o.IsAvailable).Value;
        }
    }

    private void Refresh()
    {
        var previous = SelectedSource;
        IReadOnlyList<CaptureSource> sources;
        try
        {
            sources = Kind switch
            {
                CaptureSourceKind.Screen => _screens(),
                CaptureSourceKind.Camera => _cameras(),
                _ => _windows(),
            };
        }
        catch (Exception)
        {
            sources = [];
        }

        Sources.Clear();
        foreach (var source in sources)
        {
            Sources.Add(source);
        }

        // Mantém a escolha ao atualizar; na troca de aba, a primeira tela (a principal) ou câmera já vem marcada.
        SelectedSource = Sources.FirstOrDefault(s => s.IsSameSource(previous))
            ?? (Kind != CaptureSourceKind.Window ? Sources.FirstOrDefault() : null);
        OnPropertyChanged(nameof(HasNoSources));
        OnPropertyChanged(nameof(EmptyText));
    }
}

/// <summary>Escolha segmentada (ex.: 540p 60 fps / 720p 30 fps), exibida como botões de opção.</summary>
public sealed class Choice<T> : ObservableObject
    where T : struct, Enum
{
    private T _selected;

    public Choice(T selected, IReadOnlyList<ChoiceOption<T>> options)
    {
        Options = options;
        foreach (var option in options)
        {
            option.Owner = this;
        }

        Selected = selected;
    }

    public IReadOnlyList<ChoiceOption<T>> Options { get; }

    public T Selected
    {
        get => _selected;
        set
        {
            SetProperty(ref _selected, value);
            foreach (var option in Options)
            {
                option.Refresh();
            }
        }
    }
}

/// <param name="detail">Dica da opção (ex.: por que está desligada).</param>
/// <param name="isAvailable">Desligada aparece na escolha, mas não pode ser marcada (ex.: de um plano pago).</param>
public sealed class ChoiceOption<T>(T value, string label, string? detail = null, bool isAvailable = true) : ObservableObject
    where T : struct, Enum
{
    internal Choice<T>? Owner { get; set; }

    public T Value { get; } = value;

    public string Label { get; } = label;

    private string? _detail = detail;
    private bool _isAvailable = isAvailable;

    public string? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    /// <summary>Marcar escolhe esta opção; desmarcar é ignorado (o botão de opção desmarca os outros sozinho).</summary>
    public bool IsSelected
    {
        get => Owner is not null && EqualityComparer<T>.Default.Equals(Owner.Selected, Value);
        set
        {
            if (value && IsAvailable && Owner is not null)
            {
                Owner.Selected = Value;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    internal void SetAvailability(bool isAvailable, string? detail)
    {
        IsAvailable = isAvailable;
        Detail = detail;
    }
}
