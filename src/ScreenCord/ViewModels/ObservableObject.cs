using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenCord.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>Registra-se para avisar a interface quando o idioma troca (os textos são calculados na hora).</summary>
    protected ObservableObject()
    {
        Localization.Loc.Track(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Todas as propriedades mudaram (ex.: os textos, depois da troca de idioma).</summary>
    internal void NotifyAllChanged() => OnPropertyChanged(string.Empty);

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
