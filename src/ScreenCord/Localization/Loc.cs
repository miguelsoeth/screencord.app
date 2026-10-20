using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Data;

namespace ScreenCord.Localization;

public enum AppLanguage
{
    Portuguese,
    English,
    Spanish,
}

/// <summary>
/// Idioma da interface. Os textos ficam em <see cref="Strings"/>, com as três traduções lado a lado; o XAML os lê
/// pelo <c>{l:Tr Chave}</c>, que se liga ao indexador desta classe e muda na hora quando o idioma troca. Os
/// ViewModels (<see cref="ViewModels.ObservableObject"/>) se registram aqui e avisam a interface de que todos os
/// seus textos mudaram.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly Lock Sync = new();
    private static readonly List<WeakReference<ViewModels.ObservableObject>> Tracked = [];
    private static readonly Dictionary<string, PropertyInfo> Keys = typeof(Strings)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(string))
        .ToDictionary(p => p.Name, StringComparer.Ordinal);

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    /// <summary>Português até o app escolher (os testes contam com isso).</summary>
    public static AppLanguage Language { get; private set; } = AppLanguage.Portuguese;

    public static CultureInfo Culture => CultureFor(Language);

    /// <summary>Depois que o idioma troca, já com os ViewModels avisados.</summary>
    public static event Action? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Get(key);

    public static bool HasKey(string key) => Keys.ContainsKey(key);

    public static string Get(string key) =>
        Keys.TryGetValue(key, out var property) ? (string)property.GetValue(null)! : $"[{key}]";

    /// <summary>O texto no idioma atual.</summary>
    public static string T(string portuguese, string english, string spanish) => Language switch
    {
        AppLanguage.English => english,
        AppLanguage.Spanish => spanish,
        _ => portuguese,
    };

    public static void SetLanguage(AppLanguage language)
    {
        if (language == Language)
        {
            return;
        }

        Language = language;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(Binding.IndexerName));

        foreach (var viewModel in TrackedAlive())
        {
            viewModel.NotifyAllChanged();
        }

        LanguageChanged?.Invoke();
    }

    public static string Code(AppLanguage language) => language switch
    {
        AppLanguage.English => "en",
        AppLanguage.Spanish => "es",
        _ => "pt",
    };

    public static AppLanguage? FromCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "pt" => AppLanguage.Portuguese,
        "en" => AppLanguage.English,
        "es" => AppLanguage.Spanish,
        _ => null,
    };

    /// <summary>Sem escolha salva: o idioma do Windows, se for um dos três; senão, inglês.</summary>
    public static AppLanguage FromCulture(CultureInfo culture) =>
        FromCode(culture.TwoLetterISOLanguageName) ?? AppLanguage.English;

    public static CultureInfo CultureFor(AppLanguage language) => CultureInfo.GetCultureInfo(language switch
    {
        AppLanguage.English => "en-US",
        AppLanguage.Spanish => "es-ES",
        _ => "pt-BR",
    });

    internal static void Track(ViewModels.ObservableObject viewModel)
    {
        lock (Sync)
        {
            // Limpa as referências mortas de vez em quando, para a lista não crescer com itens descartados.
            if (Tracked.Count > 64 && Tracked.Count % 64 == 0)
            {
                Tracked.RemoveAll(r => !r.TryGetTarget(out _));
            }

            Tracked.Add(new WeakReference<ViewModels.ObservableObject>(viewModel));
        }
    }

    private static List<ViewModels.ObservableObject> TrackedAlive()
    {
        lock (Sync)
        {
            Tracked.RemoveAll(r => !r.TryGetTarget(out _));
            return Tracked.Select(r => r.TryGetTarget(out var target) ? target : null).OfType<ViewModels.ObservableObject>().ToList();
        }
    }
}
