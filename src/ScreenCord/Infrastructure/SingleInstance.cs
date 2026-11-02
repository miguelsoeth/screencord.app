using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Garante um só ScreenCord aberto por usuário: a primeira instância cria um evento nomeado do Windows e as
/// seguintes só o sinalizam (para a primeira aparecer) e fecham. A criação do evento é atômica no kernel, então
/// dois cliques simultâneos não abrem duas instâncias.
/// <para>
/// A instância que fecha pode levar uma mensagem (o link de convite <c>screencord://</c> com que foi aberta): ela vai
/// por um pipe nomeado só deste usuário (<see cref="PipeOptions.CurrentUserOnly"/>) e deste logon. Quem recebe trata
/// o texto como dado não confiável.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Maior mensagem aceita pelo pipe (um link de convite tem bem menos).</summary>
    public const int MaxMessageBytes = 4096;

    private readonly EventWaitHandle _activation;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<string> _queued = [];
    private Action<string>? _onMessage;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(EventWaitHandle activation, bool isFirst, string pipeName)
    {
        _activation = activation;
        IsFirst = isFirst;
        _pipeName = pipeName;
    }

    /// <summary>True se esta é a única instância; false se já havia outra (que foi avisada para aparecer).</summary>
    public bool IsFirst { get; }

    /// <param name="key">Separa ambientes (ex.: o de desenvolvimento roda ao lado da versão instalada).</param>
    /// <param name="message">Repassada à instância já aberta, se houver (ex.: o link de convite).</param>
    public static SingleInstance Acquire(string key, string? message = null)
    {
        // "Local\": por sessão do Windows, cada usuário logado tem o seu.
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\ScreenCord.{key}", out var createdNew);
        var instance = new SingleInstance(activation, createdNew, PipeName(key));
        if (createdNew)
        {
            _ = Task.Run(instance.ListenAsync);
        }
        else
        {
            if (message is not null)
            {
                Send(instance._pipeName, message);
            }

            activation.Set();
        }

        return instance;
    }

    /// <summary>Chamado (numa thread do pool) cada vez que outra instância tenta abrir.</summary>
    public void OnActivationRequested(Action callback)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activation, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Chamado (numa thread do pool) com cada mensagem de outra instância. As que chegaram antes de assinar (o app
    /// ainda abrindo) são entregues agora.
    /// </summary>
    public void OnMessage(Action<string> callback)
    {
        List<string> queued;
        lock (_lock)
        {
            _onMessage = callback;
            queued = [.. _queued];
            _queued.Clear();
        }

        foreach (var message in queued)
        {
            callback(message);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _registration?.Unregister(null);
        _activation.Dispose();
    }

    /// <summary>Por usuário (SID) e por logon (sessão), como o evento.</summary>
    private static string PipeName(string key)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return $"ScreenCord.{key}.{identity.User?.Value}.{Process.GetCurrentProcess().SessionId}";
    }

    private static void Send(string pipeName, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > MaxMessageBytes)
        {
            return;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            pipe.Write(bytes);
            pipe.Flush();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // A outra instância ainda está abrindo (ou fechando): ela só aparece, sem o link.
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var buffer = new byte[MaxMessageBytes + 1];
                var length = 0;
                int read;
                while (length < buffer.Length && (read = await pipe.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    length += read;
                }

                if (length is > 0 and <= MaxMessageBytes)
                {
                    Deliver(Encoding.UTF8.GetString(buffer, 0, length));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // Conexão que não mandou nada a tempo, ou o pipe já existe (outro processo): tenta de novo.
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private void Deliver(string message)
    {
        Action<string>? callback;
        lock (_lock)
        {
            callback = _onMessage;
            if (callback is null)
            {
                // Poucas: o app ainda está abrindo, e ninguém manda dezenas de convites nesse tempo.
                if (_queued.Count < 8)
                {
                    _queued.Add(message);
                }

                return;
            }
        }

        callback(message);
    }
}
