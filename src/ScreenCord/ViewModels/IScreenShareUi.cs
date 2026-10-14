using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>Janelas do compartilhamento de tela, abertas pela interface (separadas para os testes simularem).</summary>
public interface IScreenShareUi
{
    /// <summary>Abre a escolha do que compartilhar. Retorna null se a pessoa cancelar.</summary>
    ShareRequest? PickShare(ShareSettings initial);

    /// <summary>Abre a janela que mostra a tela de alguém. <paramref name="closed"/> é chamado quando ela fecha.</summary>
    void ShowViewer(StreamViewerViewModel viewer, Action closed);

    /// <summary>Traz para a frente a janela de um compartilhamento que já está aberta.</summary>
    void ActivateViewer(StreamViewerViewModel viewer);
}
