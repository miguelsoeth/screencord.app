using ScreenCord.Localization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ScreenCord.Contracts;

namespace ScreenCord.Local;

/// <summary>
/// Sala local hospedada neste computador: guarda quem está nela e quem está compartilhando, e envia o estado a
/// todos a cada mudança. Quem hospeda é integrante desde a criação; quando ele sai, a sala acaba para todos.
/// <para>
/// A sala tem um token (<see cref="Token"/>, 256 bits aleatórios), que vai no convite e é exigido de quem conecta de
/// fora das redes locais. Ele só vale enquanto a sala existe; <see cref="RenewToken"/> troca e revoga o anterior.
/// </para>
/// </summary>
public sealed class LocalRoomHost
{
    public const int MaxMembers = 20;

    /// <summary>Cabeçalho do pedido de conexão com o token da sala (do convite).</summary>
    public const string TokenHeader = "X-ScreenCord-Room-Token";

    /// <summary>Cabeçalho do pedido de conexão com o id da sala esperada (do convite): outra sala responde 404.</summary>
    public const string RoomHeader = "X-ScreenCord-Room";

    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly List<LocalMember> _members = [];
    private readonly Dictionary<(Guid UserId, ShareKind Kind), LocalShare> _shares = [];
    private readonly Dictionary<Guid, Connection> _connections = [];
    private readonly ILogger _logger;
    private GroupSettings _settings = GroupSettings.Default;
    private string _token = RoomInvite.NewToken();
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

    /// <summary>Token atual da sala. Nunca vai para logs nem para o relatório de erros.</summary>
    public string Token
    {
        get
        {
            lock (_lock)
            {
                return _token;
            }
        }
    }

    /// <summary>Troca o token: os convites anteriores deixam de valer (quem já está na sala continua).</summary>
    public string RenewToken()
    {
        lock (_lock)
        {
            _token = RoomInvite.NewToken();
            return _token;
        }
    }

    /// <summary>Confere o token em tempo constante (compara os hashes, para o tamanho também não vazar).</summary>
    public bool AcceptsToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > RoomInvite.MaxTokenLength)
        {
            return false;
        }

        string expected;
        lock (_lock)
        {
            expected = _token;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>O estado mudou (para quem hospeda, que não recebe pela rede). Vem de threads de fundo.</summary>
    public event Action<LocalRoomState>? Changed;

    public LocalRoomState Snapshot()
    {
        lock (_lock)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>O que a sala aceita transmitir (tela e câmera).</summary>
    public GroupSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    /// <summary>
    /// Muda o que a sala aceita. As transmissões de um tipo desligado saem do estado na hora; retorna os tipos de
    /// quem hospeda que foram removidos (o aplicativo dele precisa parar essas capturas).
    /// </summary>
    public IReadOnlyList<ShareKind> UpdateSettings(GroupSettings settings)
    {
        List<ShareKind> hostKindsRemoved;
        lock (_lock)
        {
            if (_closed)
            {
                return [];
            }

            _settings = settings;
            hostKindsRemoved = _shares.Keys.Where(k => k.UserId == HostId && !settings.Allows(k.Kind)).Select(k => k.Kind).ToList();
            foreach (var disallowed in _shares.Where(s => !settings.Allows(s.Key.Kind)).Select(s => s.Key).ToList())
            {
                _shares.Remove(disallowed);
            }
        }

        Broadcast();
        return hostKindsRemoved;
    }

    /// <summary>Transmissão de quem hospeda; substitui a do mesmo tipo (a do outro tipo continua).</summary>
    public void SetHostShare(LocalShare share)
    {
        lock (_lock)
        {
            if (_closed || !_settings.Allows(share.Kind))
            {
                return;
            }

            _shares[(HostId, share.Kind)] = share with { UserId = HostId };
        }

        Broadcast();
    }

    /// <summary>Encerra a transmissão de quem hospeda desse tipo.</summary>
    public void RemoveHostShare(ShareKind kind)
    {
        bool removed;
        lock (_lock)
        {
            removed = !_closed && _shares.Remove((HostId, kind));
        }

        if (removed)
        {
            Broadcast();
        }
    }

    /// <summary>Atende quem entrou pela rede, até a conexão fechar ou a sala acabar.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        LocalMember? member;
        try
        {
            using var hello = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hello.CancelAfter(HelloTimeout);
            var message = await LocalMessage.ReceiveAsync(socket, hello.Token, LocalMessage.MaxHelloBytes).ConfigureAwait(false);
            member = message is { Type: LocalMessage.Hello, Protocol: LocalMessage.CurrentProtocol } ? message.Member?.Sanitize() : null;
            if (member is null || member.Id == HostId)
            {
                var reason = message is { Type: LocalMessage.Hello } && message.Protocol != LocalMessage.CurrentProtocol
                    ? Strings.HostOtherVersion
                    : Strings.InvalidJoinRequest;
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
                refusal = Strings.RoomClosed;
            }
            else if (_members.All(m => m.Id != member.Id) && _members.Count >= MaxMembers)
            {
                refusal = string.Format(Loc.Culture, Strings.RoomFullFormat, MaxMembers);
            }
            else
            {
                // A mesma pessoa entrando de novo (ex.: a conexão caiu e voltou) substitui a conexão anterior.
                _connections.Remove(member.Id, out replaced);
                _members.RemoveAll(m => m.Id == member.Id);
                RemoveSharesOf(member.Id);
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
                    bool accepted;
                    lock (_lock)
                    {
                        // A sala decide se aceita tela e câmera; quem não respeita a configuração fica sem anúncio.
                        accepted = _settings.Allows(share.Kind);
                        if (accepted)
                        {
                            _shares[(member.Id, share.Kind)] = share with { UserId = member.Id };
                        }
                    }

                    if (accepted)
                    {
                        Broadcast();
                    }
                }
                else if (message.Type == LocalMessage.ShareStop)
                {
                    bool removed;
                    lock (_lock)
                    {
                        // Sem o tipo (versão que só tinha uma transmissão por pessoa), encerra todas.
                        removed = message.Kind is { } kind ? _shares.Remove((member.Id, kind)) : RemoveSharesOf(member.Id);
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
                    RemoveSharesOf(member.Id);
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

    /// <summary>Remove as transmissões da pessoa (dentro do lock). Retorna se havia alguma.</summary>
    private bool RemoveSharesOf(Guid userId)
    {
        var keys = _shares.Keys.Where(k => k.UserId == userId).ToList();
        foreach (var key in keys)
        {
            _shares.Remove(key);
        }

        return keys.Count > 0;
    }

    /// <summary>A tela antes da câmera: quem só conhece uma transmissão por pessoa fica com a tela.</summary>
    private LocalRoomState SnapshotLocked() =>
        new(Id, Name, HostId, _version, [.. _members], [.. _shares.Values.OrderBy(s => s.Kind)], _settings);

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
