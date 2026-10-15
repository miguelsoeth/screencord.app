namespace ScreenCord.Media;

/// <summary>
/// Decide quando codificar um quadro. A captura só entrega quadro novo quando a tela muda; com a tela parada,
/// reenviar o mesmo quadro na taxa cheia gastaria banda à toa. Depois de cada mudança o ritmo cheio continua
/// por <see cref="SettleTime"/>, para o encoder terminar de refinar a imagem, e então cai para um quadro a cada
/// <see cref="IdleInterval"/>, o bastante para quem assiste não achar que a transmissão travou.
/// Quadros-chave pedidos saem na hora.
/// </summary>
public sealed class FramePacer
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1);

    /// <summary>Abaixo do vigia de quem assiste (1,5 s sem imagem pede um quadro-chave).</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(500);

    private TimeSpan? _lastChange;
    private TimeSpan? _lastEncode;

    /// <param name="now">Tempo atual da transmissão.</param>
    /// <param name="changed">A captura trouxe um quadro novo.</param>
    /// <param name="keyframePending">Há um quadro-chave pedido esperando para sair.</param>
    public bool ShouldEncode(TimeSpan now, bool changed, bool keyframePending)
    {
        if (changed)
        {
            _lastChange = now;
        }

        var due = changed ||
                  keyframePending ||
                  _lastEncode is not { } last ||
                  (_lastChange is { } change && now - change < SettleTime) ||
                  now - last >= IdleInterval;
        if (due)
        {
            _lastEncode = now;
        }

        return due;
    }
}
