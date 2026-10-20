using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ScreenCord.Localization;
using ScreenCord.ViewModels;

namespace ScreenCord.Tests;

// Troca o idioma global: roda sozinha, sem os outros testes (que esperam português) em paralelo.
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public sealed class LanguageCollection;

[Collection(nameof(LanguageCollection))]
public sealed class LocalizationTests : IDisposable
{
    public void Dispose() => Loc.SetLanguage(AppLanguage.Portuguese);

    private static IEnumerable<PropertyInfo> Texts() =>
        typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string));

    [Fact]
    public void Default_IsPortuguese()
    {
        Assert.Equal(AppLanguage.Portuguese, Loc.Language);
        Assert.Equal("Fechar", Strings.Close);
    }

    [Theory]
    [InlineData(AppLanguage.English, "Close")]
    [InlineData(AppLanguage.Spanish, "Cerrar")]
    public void SetLanguage_ChangesTexts(AppLanguage language, string close)
    {
        Loc.SetLanguage(language);

        Assert.Equal(close, Strings.Close);
        Assert.Equal(close, Loc.Instance[nameof(Strings.Close)]);
    }

    [Fact]
    public void EveryText_HasAllLanguages_WithMatchingPlaceholders()
    {
        var byLanguage = Enum.GetValues<AppLanguage>().ToDictionary(l => l, l =>
        {
            Loc.SetLanguage(l);
            return Texts().ToDictionary(p => p.Name, p => (string)p.GetValue(null)!);
        });

        foreach (var key in byLanguage[AppLanguage.Portuguese].Keys)
        {
            var placeholders = Placeholders(byLanguage[AppLanguage.Portuguese][key]);
            foreach (var language in byLanguage.Keys)
            {
                var text = byLanguage[language][key];
                Assert.False(string.IsNullOrWhiteSpace(text), $"{key} vazio em {language}");
                Assert.Equal(placeholders, Placeholders(text));
            }
        }

        static string Placeholders(string text) =>
            string.Join(",", Regex.Matches(text, @"\{\d\}").Select(m => m.Value).Order());
    }

    [Fact]
    public void XamlKeys_AllExist()
    {
        var views = Path.Combine(RepoRoot(), "src", "ScreenCord");
        var used = Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{l:Tr (\w+)").Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(used);
        Assert.All(used, u => Assert.True(Loc.HasKey(u.Key), $"{u.File}: chave {u.Key} não existe em Strings"));
    }

    [Fact]
    public void SetLanguage_NotifiesViewModels()
    {
        var viewModel = new Probe();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Loc.SetLanguage(AppLanguage.English);

        Assert.Contains(string.Empty, changed);
        GC.KeepAlive(viewModel);
    }

    [Fact]
    public void SetLanguage_NotifiesIndexerBindings()
    {
        var changed = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => changed.Add(e.PropertyName);
        Loc.Instance.PropertyChanged += handler;
        try
        {
            Loc.SetLanguage(AppLanguage.Spanish);
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
        }

        Assert.Equal(["Item[]"], changed);
    }

    [Theory]
    [InlineData("pt-BR", AppLanguage.Portuguese)]
    [InlineData("pt-PT", AppLanguage.Portuguese)]
    [InlineData("es-MX", AppLanguage.Spanish)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("de-DE", AppLanguage.English)]
    public void FromCulture_PicksWindowsLanguage(string culture, AppLanguage expected) =>
        Assert.Equal(expected, Loc.FromCulture(System.Globalization.CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData(AppLanguage.Portuguese)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Spanish)]
    public void Code_RoundTrips(AppLanguage language) => Assert.Equal(language, Loc.FromCode(Loc.Code(language)));

    [Fact]
    public void MembersSummary_UsesSingularAndPlural()
    {
        Assert.Equal("1 integrante · ninguém conectado", Strings.MembersSummary(1, 0));
        Loc.SetLanguage(AppLanguage.English);
        Assert.Equal("3 members · 1 connected", Strings.MembersSummary(3, 1));
    }

    private sealed class Probe : ObservableObject;

    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
