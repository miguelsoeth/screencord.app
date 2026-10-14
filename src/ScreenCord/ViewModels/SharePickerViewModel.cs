using System.Collections.ObjectModel;
using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>
/// Janela "Compartilhar tela": escolha entre uma tela ou uma janela, resolução, fps, qualidade e áudio.
/// O padrão é 720p, 30 fps, qualidade média, com áudio.
/// </summary>
public sealed class SharePickerViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<CaptureSource>> _screens;
    private readonly Func<IReadOnlyList<CaptureSource>> _windows;
    private CaptureSourceKind _kind = CaptureSourceKind.Screen;
    private CaptureSource? _selectedSource;
    private bool _shareAudio;

    public SharePickerViewModel(ShareSettings initial, Func<IReadOnlyList<CaptureSource>>? screens = null, Func<IReadOnlyList<CaptureSource>>? windows = null)
    {
        _screens = screens ?? CaptureSources.Screens;
        _windows = windows ?? CaptureSources.Windows;
        _shareAudio = initial.ShareAudio;
        Resolutions = new Choice<ShareResolution>(initial.Resolution,
        [
            new(ShareResolution.P540, "540p"),
            new(ShareResolution.P720, "720p"),
            new(ShareResolution.P1080, "1080p"),
        ]);
        FrameRates = new Choice<ShareFrameRate>(initial.FrameRate,
        [
            new(ShareFrameRate.Fps30, "30 fps"),
            new(ShareFrameRate.Fps60, "60 fps"),
        ]);
        Qualities = new Choice<ShareQuality>(initial.Quality,
        [
            new(ShareQuality.Low, "Baixo", $"{ShareSettings.BitrateOf(ShareQuality.Low)} kbps"),
            new(ShareQuality.Medium, "Médio", $"{ShareSettings.BitrateOf(ShareQuality.Medium)} kbps"),
            new(ShareQuality.High, "Alto", $"{ShareSettings.BitrateOf(ShareQuality.High)} kbps"),
        ]);

        ShowScreensCommand = new RelayCommand(() => Kind = CaptureSourceKind.Screen);
        ShowWindowsCommand = new RelayCommand(() => Kind = CaptureSourceKind.Window);
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    public ObservableCollection<CaptureSource> Sources { get; } = [];

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
                OnPropertyChanged(nameof(AudioDescription));
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

    public string EmptyText => Kind == CaptureSourceKind.Screen ? "Nenhuma tela encontrada." : "Nenhuma janela aberta para compartilhar.";

    public bool CanShare => SelectedSource is not null;

    public Choice<ShareResolution> Resolutions { get; }

    public Choice<ShareFrameRate> FrameRates { get; }

    public Choice<ShareQuality> Qualities { get; }

    public bool ShareAudio
    {
        get => _shareAudio;
        set => SetProperty(ref _shareAudio, value);
    }

    public string AudioDescription => Kind == CaptureSourceKind.Screen
        ? "Tudo o que toca no computador, menos o ScreenCord."
        : "Só o áudio do programa da janela.";

    public ShareRequest? BuildRequest() =>
        SelectedSource is { } source
            ? new ShareRequest(source, new ShareSettings(Resolutions.Selected, FrameRates.Selected, Qualities.Selected, ShareAudio))
            : null;

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

/// <summary>Escolha segmentada (ex.: 540p / 720p / 1080p), exibida como botões de opção.</summary>
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

public sealed class ChoiceOption<T>(T value, string label, string? detail = null) : ObservableObject
    where T : struct, Enum
{
    internal Choice<T>? Owner { get; set; }

    public T Value { get; } = value;

    public string Label { get; } = label;

    public string? Detail { get; } = detail;

    /// <summary>Marcar escolhe esta opção; desmarcar é ignorado (o botão de opção desmarca os outros sozinho).</summary>
    public bool IsSelected
    {
        get => Owner is not null && EqualityComparer<T>.Default.Equals(Owner.Selected, Value);
        set
        {
            if (value && Owner is not null)
            {
                Owner.Selected = Value;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}
