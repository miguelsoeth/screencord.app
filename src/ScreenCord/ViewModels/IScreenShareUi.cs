using ScreenCord.Contracts;
using ScreenCord.Media;

namespace ScreenCord.ViewModels;

/// <summary>Janelas do compartilhamento de tela, abertas pela interface (separadas para os testes simularem).</summary>
public interface IScreenShareUi
{
    /// <summary>
    /// Abre a escolha do que compartilhar. Com <paramref name="current"/>, é a troca das configurações de um
    /// compartilhamento em andamento, com a origem atual já marcada. Só as opções dentro de <paramref name="limits"/>
    /// (o caminho da mídia: servidor ou sala local) podem ser escolhidas. Retorna null se a pessoa cancelar.
    /// </summary>
    ShareRequest? PickShare(ShareSettings initial, StreamLimits limits, CaptureSource? current = null);

    /// <summary>
    /// Mostra a tela de alguém (no lugar da que estava aberta, se houver). <paramref name="closed"/> é chamado
    /// quando ela deixa de ser mostrada.
    /// </summary>
    void ShowViewer(StreamViewerViewModel viewer, Action closed);

    /// <summary>Traz para a frente um compartilhamento que já está sendo mostrado.</summary>
    void ActivateViewer(StreamViewerViewModel viewer);
}
