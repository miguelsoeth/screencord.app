using System.Windows;

namespace ScreenCord.Views;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
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
}
