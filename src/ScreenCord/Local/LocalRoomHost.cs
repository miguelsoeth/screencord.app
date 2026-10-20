using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Local;

/// <summary>
/// Sala local hospedada neste computador: guarda quem está nela e quem está compartilhando, e envia o estado a
/// todos a cada mudança. Quem hospeda é integrante desde a criação; quando ele sai, a sala acaba para todos.
/// </summary>
public sealed class LocalRoomHost
{
    public const int MaxMembers = 20;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly List<LocalMember> _members = [];
    private readonly Dictionary<Guid, LocalShare> _shares = [];
    private readonly Dictionary<Guid, Connection> _connections = [];
    private readonly ILogger _logger;
    private long _version;
    private bool _closed;

    public LocalRoomHost(Guid id, string name, LocalMember host, ILogger logger)
    {
        Id = id;
        Name = name;
        HostId = host.Id;
        _members.Add(host);
        _logger = logger;
    }

    public Guid Id { get; }

    public string Name { get; }

    public Guid HostId { get; }

    /// <summary>O estado mudou (para quem hospeda, que não recebe pela rede). Vem de threads de fundo.</summary>
    public event Action<LocalRoomState>? Changed;

    public LocalRoomState Snapshot()
    {
        lock (_lock)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>Compartilhamento de quem hospeda (null para encerrar).</summary>
    public void SetHostShare(LocalShare? share)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            if (share is null)
            {
                _shares.Remove(HostId);
            }
            else
            {
                _shares[HostId] = share with { UserId = HostId };
            }
        }

        Broadcast();
    }

    /// <summary>Atende quem entrou pela rede, até a conexão fechar ou a sala acabar.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        LocalMember? member;
        try
        {
            using var hello = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hello.CancelAfter(HelloTimeout);
            var message = await LocalMessage.ReceiveAsync(socket, hello.Token).ConfigureAwait(false);
            member = message is { Type: LocalMessage.Hello, Protocol: LocalMessage.CurrentProtocol } ? message.Member?.Sanitize() : null;
            if (member is null || member.Id == HostId)
            {
                var reason = message is { Type: LocalMessage.Hello } && message.Protocol != LocalMessage.CurrentProtocol
                    ? "Quem hospeda a sala usa outra versão do ScreenCord. Atualizem o app."
                    : "Pedido de entrada inválido.";
                await LocalMessage.SendAsync(socket, new LocalMessage(LocalMessage.Closed, Reason: reason), cancellationToken).ConfigureAwait(false);
                await LocalMessage.CloseAsync(socket);
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException or WebSocketException)
        {
            await LocalMessage.CloseAsync(socket);
            return;
        }

        var connection = new Connection(socket, member);
        string? refusal = null;
        Connection? replaced = null;
        lock (_lock)
        {
            if (_closed)
            {
                refusal = "A sala foi encerrada.";
            }
            else if (_members.All(m => m.Id != member.Id) && _members.Count >= MaxMembers)
            {
                refusal = $"A sala está cheia ({MaxMembers} pessoas).";
            }
            else
            {
                // A mesma pessoa entrando de novo (ex.: a conexão caiu e voltou) substitui a conexão anterior.
                _connections.Remove(member.Id, out replaced);
                _members.RemoveAll(m => m.Id == member.Id);
                _shares.Remove(member.Id);
                _members.Add(member);
                _connections[member.Id] = connection;
            }
        }

        if (refusal is not null)
        {
            await LocalMessage.SendAsync(socket, new LocalMessage(LocalMessage.Closed, Reason: refusal), cancellationToken).ConfigureAwait(false);
            await LocalMessage.CloseAsync(socket);
            return;
        }

        replaced?.Close();
        _logger.LogInformation("{Member} entrou na sala local {Room}.", member.Id, Id);
        var sending = connection.RunSendLoopAsync(cancellationToken);
        Broadcast();
        try
        {
            while (await LocalMessage.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false) is { } message)
            {
                if (message.Type == LocalMessage.ShareStart && message.Share is { } share && (share with { UserId = member.Id }).IsValid)
                {
                    lock (_lock)
                    {
                        _shares[member.Id] = share with { UserId = member.Id };
                    }

                    Broadcast();
                }
                else if (message.Type == LocalMessage.ShareStop)
                {
                    bool removed;
                    lock (_lock)
                    {
                        removed = _shares.Remove(member.Id);
                    }

                    if (removed)
                    {
                        Broadcast();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            var left = false;
            lock (_lock)
            {
                if (_connections.TryGetValue(member.Id, out var current) && current == connection)
                {
                    _connections.Remove(member.Id);
                    _members.RemoveAll(m => m.Id == member.Id);
                    _shares.Remove(member.Id);
                    left = true;
                }
            }

            connection.Close();
            if (left)
            {
                _logger.LogInformation("{Member} saiu da sala local {Room}.", member.Id, Id);
                Broadcast();
            }

            await sending.ConfigureAwait(false);
        }
    }

    /// <summary>Encerra a sala: cada um recebe o motivo e é desconectado.</summary>
    public async Task CloseAsync(string reason)
    {
        List<Connection> connections;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            connections = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var connection in connections)
        {
            connection.Enqueue(new LocalMessage(LocalMessage.Closed, Reason: reason));
            connection.Close();
        }

        await Task.WhenAll(connections.Select(c => c.Completion)).WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { }).ConfigureAwait(false);
    }

    private LocalRoomState SnapshotLocked() =>
        new(Id, Name, HostId, _version, [.. _members], [.. _shares.Values]);

    private void Broadcast()
    {
        LocalRoomState state;
        List<Connection> connections;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _version++;
            state = SnapshotLocked();
            connections = [.. _connections.Values];
        }

        var message = new LocalMessage(LocalMessage.State, Room: state);
        foreach (var connection in connections)
        {
            connection.Enqueue(message);
        }

        Changed?.Invoke(state);
    }

    /// <summary>Uma pessoa conectada; as mensagens saem por uma fila, uma de cada vez.</summary>
    private sealed class Connection(WebSocket socket, LocalMember member)
    {
        private readonly Channel<LocalMessage> _outgoing = Channel.CreateUnbounded<LocalMessage>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LocalMember Member { get; } = member;

        public Task Completion => _completion.Task;

        public void Enqueue(LocalMessage message) => _outgoing.Writer.TryWrite(message);

        /// <summary>Envia o que já está na fila e fecha a conexão.</summary>
        public void Close() => _outgoing.Writer.TryComplete();

        public async Task RunSendLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var message in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await LocalMessage.SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
            }

            await LocalMessage.CloseAsync(socket);
            _completion.TrySetResult();
        }
    }
}
