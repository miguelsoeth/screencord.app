namespace ScreenCord.Infrastructure;

/// <summary>
/// Garante um só ScreenCord aberto por usuário: a primeira instância cria um evento nomeado do Windows e as
/// seguintes só o sinalizam (para a primeira aparecer) e fecham. A criação do evento é atômica no kernel, então
/// dois cliques simultâneos não abrem duas instâncias.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(EventWaitHandle activation, bool isFirst)
    {
        _activation = activation;
        IsFirst = isFirst;
    }

    /// <summary>True se esta é a única instância; false se já havia outra (que foi avisada para aparecer).</summary>
    public bool IsFirst { get; }

    /// <param name="key">Separa ambientes (ex.: o de desenvolvimento roda ao lado da versão instalada).</param>
    public static SingleInstance Acquire(string key)
    {
        // "Local\": por sessão do Windows, cada usuário logado tem o seu.
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\ScreenCord.{key}", out var createdNew);
        if (!createdNew)
        {
            activation.Set();
        }

        return new SingleInstance(activation, createdNew);
    }

    /// <summary>Chamado (numa thread do pool) cada vez que outra instância tenta abrir.</summary>
    public void OnActivationRequested(Action callback)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activation, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activation.Dispose();
    }
}
