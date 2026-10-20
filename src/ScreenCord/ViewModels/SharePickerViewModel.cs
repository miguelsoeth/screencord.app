using ScreenCord.Localization;
using System.Collections.ObjectModel;
using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>
/// Janela "Compartilhar tela": escolha entre uma tela ou uma janela, resolução, fps e áudio.
/// As opções fora do caminho da mídia aparecem desligadas: pelo servidor até 720p a 30 fps; nas salas locais
/// (direto entre os computadores) até 1080p a 60 fps. A taxa de bits é automática, até o teto do caminho.
/// </summary>
public sealed class SharePickerViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<CaptureSource>> _screens;
    private readonly Func<IReadOnlyList<CaptureSource>> _windows;
    private CaptureSourceKind _kind = CaptureSourceKind.Screen;
    private CaptureSource? _selectedSource;
    private bool _shareAudio;
    private readonly StreamLimits _limits;

    /// <param name="current">Origem do compartilhamento em andamento, quando a escolha é para trocar as configurações dele.</param>
    /// <param name="limits">Limites do caminho da mídia; sem eles, os do servidor.</param>
    public SharePickerViewModel(
        ShareSettings initial,
        Func<IReadOnlyList<CaptureSource>>? screens = null,
        Func<IReadOnlyList<CaptureSource>>? windows = null,
        CaptureSource? current = null,
        StreamLimits? limits = null)
    {
        _limits = limits ?? StreamLimits.Relay;
        IsDirect = _limits.MaxHeight > StreamLimits.Relay.MaxHeight || _limits.MaxFrameRate > StreamLimits.Relay.MaxFrameRate;
        _screens = screens ?? CaptureSources.Screens;
        _windows = windows ?? CaptureSources.Windows;
        _shareAudio = initial.ShareAudio;
        IsChange = current is not null;
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
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    private static string DirectOnly => Strings.DirectOnly;

    public ObservableCollection<CaptureSource> Sources { get; } = [];

    /// <summary>A mídia vai direto entre os computadores (sala local), sem passar pelo servidor.</summary>
    public bool IsDirect { get; }

    /// <summary>"Qualidade automática, até 2,5 Mbps · pelo servidor".</summary>
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

    public RelayCommand RefreshCommand { get; }

    public CaptureSourceKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsScreenKind));
                OnPropertyChanged(nameof(IsWindowKind));
                Refresh();
            }
        }
    }

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

    public string EmptyText => Kind == CaptureSourceKind.Screen ? Strings.NoScreensFound : Strings.NoWindowsToShare;

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
            ? new ShareRequest(source, new ShareSettings(Resolutions.Selected, FrameRates.Selected, ShareAudio, _limits.MaxBitrateKbps))
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
            sources = Kind == CaptureSourceKind.Screen ? _screens() : _windows();
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

        // Mantém a escolha ao atualizar; na troca de aba, a primeira tela (a principal) já vem marcada.
        SelectedSource = Sources.FirstOrDefault(s => previous is not null && s.Kind == previous.Kind && s.Handle == previous.Handle)
            ?? (Kind == CaptureSourceKind.Screen ? Sources.FirstOrDefault() : null);
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
