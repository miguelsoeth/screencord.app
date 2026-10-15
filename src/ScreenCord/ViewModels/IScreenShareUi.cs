using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>Janelas do compartilhamento de tela, abertas pela interface (separadas para os testes simularem).</summary>
public interface IScreenShareUi
{
    /// <summary>Abre a escolha do que compartilhar. Retorna null se a pessoa cancelar.</summary>
    ShareRequest? PickShare(ShareSettings initial);

    /// <summary>
    /// Mostra a tela de alguém (no lugar da que estava aberta, se houver). <paramref name="closed"/> é chamado
    /// quando ela deixa de ser mostrada.
    /// </summary>
    void ShowViewer(StreamViewerViewModel viewer, Action closed);

    /// <summary>Traz para a frente um compartilhamento que já está sendo mostrado.</summary>
    void ActivateViewer(StreamViewerViewModel viewer);
}
