using System.Windows;
using ScreenCord.Localization;

namespace ScreenCord.Views;

/// <summary>Resposta à pergunta de atualização.</summary>
public enum UpdateChoice
{
    Install,
    Later,
    Skip,
    Exit,
}

public partial class UpdateWindow : Window
{
    private UpdateChoice _choice;

    public UpdateWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pergunta se instala a versão nova. Opcional: atualizar agora, agora não ou pular esta versão. Obrigatória
    /// (a API não aceita mais esta versão): atualizar ou sair.
    /// </summary>
    public static UpdateChoice Ask(string text, bool required)
    {
        var window = new UpdateWindow { _choice = required ? UpdateChoice.Exit : UpdateChoice.Later };
        window.StatusText.Text = text;
        window.Progress.Visibility = Visibility.Collapsed;
        window.Buttons.Visibility = Visibility.Visible;
        window.InstallButton.Content = required ? Strings.Update : Strings.UpdateNow;
        if (required)
        {
            window.SkipButton.Visibility = Visibility.Collapsed;
            window.LaterButton.Visibility = Visibility.Collapsed;
            window.ExitButton.Visibility = Visibility.Visible;
            window.ExitButton.IsCancel = true;
        }

        window.ShowDialog();
        return window._choice;
    }

    public void SetStatus(string text, double? progress = null)
    {
        StatusText.Text = text;
        Progress.IsIndeterminate = progress is null;
        if (progress is { } value)
        {
            Progress.Value = Math.Clamp(value, 0, 1);
        }
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => Choose(UpdateChoice.Install);

    private void OnLaterClick(object sender, RoutedEventArgs e) => Choose(UpdateChoice.Later);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Choose(UpdateChoice.Skip);

    private void OnExitClick(object sender, RoutedEventArgs e) => Choose(UpdateChoice.Exit);

    private void Choose(UpdateChoice choice)
    {
        _choice = choice;
        Close();
    }
}
