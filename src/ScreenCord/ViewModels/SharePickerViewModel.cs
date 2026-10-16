using System.Collections.ObjectModel;
using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>
/// Janela "Compartilhar tela": escolha entre uma tela ou uma janela, qualidade (resolução e fps) e áudio.
/// O padrão é 720p, 30 fps, com áudio. A taxa de bits é automática.
/// </summary>
public sealed class SharePickerViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<CaptureSource>> _screens;
    private readonly Func<IReadOnlyList<CaptureSource>> _windows;
    private CaptureSourceKind _kind = CaptureSourceKind.Screen;
    private CaptureSource? _selectedSource;
    private bool _shareAudio;

    /// <param name="current">Origem do compartilhamento em andamento, quando a escolha é para trocar as configurações dele.</param>
    public SharePickerViewModel(
        ShareSettings initial,
        Func<IReadOnlyList<CaptureSource>>? screens = null,
        Func<IReadOnlyList<CaptureSource>>? windows = null,
        CaptureSource? current = null)
    {
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

        Qualities = new Choice<ShareQuality>(ShareSettings.IsAvailable(initial.Quality) ? initial.Quality : ShareSettings.Default.Quality,
        [
            Option(ShareQuality.P540Fps60),
            Option(ShareQuality.P720Fps30),
            Option(ShareQuality.P1080Fps60, "Em breve no plano pago."),
        ]);

        ShowScreensCommand = new RelayCommand(() => Kind = CaptureSourceKind.Screen);
        ShowWindowsCommand = new RelayCommand(() => Kind = CaptureSourceKind.Window);
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    public ObservableCollection<CaptureSource> Sources { get; } = [];

    /// <summary>Troca das configurações de um compartilhamento em andamento.</summary>
    public bool IsChange { get; }

    public string HeaderText => IsChange ? "Alterar compartilhamento" : "Compartilhar tela";

    public string ConfirmText => IsChange ? "Aplicar" : "Compartilhar";

    /// <summary>Dica abaixo do título; só aparece ao alterar um compartilhamento em andamento.</summary>
    public string HintText => "Quem está assistindo continua vendo, já com as novas configurações.";

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

    public string EmptyText => Kind == CaptureSourceKind.Screen ? "Nenhuma tela encontrada." : "Nenhuma janela aberta para compartilhar.";

    public bool CanShare => SelectedSource is not null;

    public Choice<ShareQuality> Qualities { get; }

    /// <summary>Explica a taxa de bits, que não é escolhida.</summary>
    public string BitrateHint => $"A taxa de bits se ajusta à sua conexão, até {ScreenShareLimits.MaxBitrateKbps / 1000} Mbps.";

    public bool ShareAudio
    {
        get => _shareAudio;
        set => SetProperty(ref _shareAudio, value);
    }

    public ShareRequest? BuildRequest() =>
        SelectedSource is { } source
            ? new ShareRequest(source, new ShareSettings(Qualities.Selected, ShareAudio))
            : null;

    private static ChoiceOption<ShareQuality> Option(ShareQuality quality, string? unavailableReason = null) =>
        new(quality, $"{ShareSettings.HeightOf(quality)}p · {ShareSettings.FpsOf(quality)} fps",
            ShareSettings.IsAvailable(quality) ? null : unavailableReason, ShareSettings.IsAvailable(quality));

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

/// <param name="isAvailable">Desligada aparece na escolha, mas não pode ser marcada (ex.: de um plano pago).</param>
public sealed class ChoiceOption<T>(T value, string label, string? detail = null, bool isAvailable = true) : ObservableObject
    where T : struct, Enum
{
    internal Choice<T>? Owner { get; set; }

    public T Value { get; } = value;

    public string Label { get; } = label;

    public string? Detail { get; } = detail;

    public bool IsAvailable { get; } = isAvailable;

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
}
