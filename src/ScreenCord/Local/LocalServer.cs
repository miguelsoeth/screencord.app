using ScreenCord.Localization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Local;

/// <summary>Pedido de conexão recebido pelo <see cref="LocalServer"/>.</summary>
/// <param name="IsTrusted">Veio da rede local ou de uma VPN (<see cref="LocalNetwork.IsLocal"/>); de fora, a sala exige o token.</param>
public sealed record LocalRequest(string Path, IReadOnlyDictionary<string, string> Headers, IPEndPoint Remote, bool IsTrusted = true)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Resposta da rota: 101 com quem cuida do WebSocket, ou um status de recusa (404, 403...).</summary>
public sealed record LocalRoute(int Status, Func<WebSocket, CancellationToken, Task>? Handler = null)
{
    public static LocalRoute NotFound { get; } = new(404);

    /// <summary>Chave ou token errado: conta como tentativa inválida de quem vem de fora (<see cref="ConnectionGuard"/>).</summary>
    public static LocalRoute Forbidden { get; } = new(403);

    public static LocalRoute Accept(Func<WebSocket, CancellationToken, Task> handler) => new(101, handler);
}

/// <summary>
/// Servidor WebSocket mínimo das salas locais, sobre <see cref="TcpListener"/>: escutar fora do localhost com o
/// <c>HttpListener</c> exigiria administrador (urlacl). Tudo passa por TLS com o <see cref="LocalCertificate"/>
/// (<c>wss://</c>). Só aceita o upgrade para WebSocket; o resto recebe 400.
/// <para>
/// Com o convite, a porta pode ficar exposta à internet. Até a rota aceitar a conexão (antes disso ninguém está
/// autenticado), valem limites: tempo para o TLS e os cabeçalhos, tamanho dos cabeçalhos, conexões abertas ao mesmo
/// tempo (no total e por endereço de fora) e, para quem vem de fora, bloqueio temporário depois de várias recusas
/// por chave ou token errado (<see cref="ConnectionGuard"/>).
/// </para>
/// </summary>
public sealed class LocalServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    public const int MaxHeaderBytes = 8 * 1024;

    /// <summary>Conexões ainda não aceitas pela rota, ao mesmo tempo, no total.</summary>
    public const int MaxPendingConnections = 32;

    /// <summary>Conexões ainda não aceitas pela rota, ao mesmo tempo, de um mesmo endereço de fora das redes locais.</summary>
    public const int MaxPendingPerAddress = 4;

    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<LocalRequest, LocalRoute> _route;
    private readonly X509Certificate2 _certificate;
    private readonly ILogger _logger;
    private readonly Func<IPAddress, bool> _isTrusted;
    private readonly TimeSpan _handshakeTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _pendingLock = new();
    private readonly Dictionary<IPAddress, int> _pendingByAddress = [];
    private int _pending;
    private TcpListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;

    /// <param name="isTrusted">Endereços que dispensam o token e os limites por endereço (padrão: <see cref="LocalNetwork.IsLocal"/>).</param>
    public LocalServer(
        Func<LocalRequest, LocalRoute> route,
        X509Certificate2 certificate,
        ILogger logger,
        Func<IPAddress, bool>? isTrusted = null,
        TimeProvider? time = null,
        TimeSpan? handshakeTimeout = null)
    {
        _route = route;
        _certificate = certificate;
        _logger = logger;
        _isTrusted = isTrusted ?? LocalNetwork.IsLocal;
        _handshakeTimeout = handshakeTimeout ?? DefaultHandshakeTimeout;
        Guard = new ConnectionGuard(time ?? TimeProvider.System);
    }

    /// <summary>Porta em que está escutando (útil quando foi pedida a porta 0, nos testes).</summary>
    public int Port { get; private set; }

    /// <summary>Tentativas inválidas e bloqueios de quem vem de fora.</summary>
    public ConnectionGuard Guard { get; }

    /// <summary>Começa a escutar em todas as interfaces. Falha com <see cref="LocalNetworkException"/> se a porta estiver ocupada.</summary>
    public void Start(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new LocalNetworkException(
                string.Format(Loc.Culture, Strings.PortInUseFormat, port), ex);
        }

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            IPEndPoint remote;
            try
            {
                remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                client.Dispose();
                continue;
            }

            if (remote.Address.IsIPv4MappedToIPv6)
            {
                remote = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
            }

            var trusted = _isTrusted(remote.Address);
            if (!TryEnterPending(remote.Address, trusted))
            {
                // Conexões demais esperando: fecha antes do TLS, que é o que custa.
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, remote, trusted));
        }
    }

    private bool TryEnterPending(IPAddress address, bool trusted)
    {
        lock (_pendingLock)
        {
            var fromAddress = _pendingByAddress.GetValueOrDefault(address);
            if (_pending >= MaxPendingConnections || (!trusted && fromAddress >= MaxPendingPerAddress))
            {
                return false;
            }

            _pending++;
            _pendingByAddress[address] = fromAddress + 1;
            return true;
        }
    }

    private void LeavePending(IPAddress address)
    {
        lock (_pendingLock)
        {
            _pending--;
            if (_pendingByAddress.TryGetValue(address, out var count) && count > 1)
            {
                _pendingByAddress[address] = count - 1;
            }
            else
            {
                _pendingByAddress.Remove(address);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, IPEndPoint remote, bool trusted)
    {
        var pending = true;
        using (client)
        {
            try
            {
                // Dentro do try: se a conexão já caiu, o finally ainda devolve a vaga de conexão esperando.
                client.NoDelay = true;
                using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(_handshakeTimeout);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, handshake.Token).ConfigureAwait(false);
                var request = await ReadRequestAsync(stream, remote, trusted, handshake.Token).ConfigureAwait(false);
                if (request is null || request.Header("upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) != true ||
                    request.Header("sec-websocket-key") is not { Length: > 0 } key)
                {
                    await WriteStatusAsync(stream, 400, handshake.Token).ConfigureAwait(false);
                    return;
                }

                if (!trusted && Guard.IsBlocked(remote.Address))
                {
                    await WriteStatusAsync(stream, 429, handshake.Token).ConfigureAwait(false);
                    return;
                }

                var route = _route(request);
                if (route.Handler is null)
                {
                    if (route.Status == 403 && !trusted && Guard.RecordFailure(remote.Address))
                    {
                        _logger.LogInformation(
                            "Endereço de fora da rede bloqueado por {Minutes} min depois de tentativas com chave ou token inválido.",
                            ConnectionGuard.BlockDuration.TotalMinutes);
                    }

                    await WriteStatusAsync(stream, route.Status, handshake.Token).ConfigureAwait(false);
                    return;
                }

                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
                var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                               $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), handshake.Token).ConfigureAwait(false);
                LeavePending(remote.Address);
                pending = false;

                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(15),
                });
                await route.Handler(socket, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or WebSocketException or ObjectDisposedException or AuthenticationException)
            {
                _logger.LogDebug("Conexão local encerrada ({ExceptionType}).", ex.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro numa conexão da sala local.");
            }
            finally
            {
                if (pending)
                {
                    LeavePending(remote.Address);
                }
            }
        }
    }

    /// <summary>Lê a linha do pedido e os cabeçalhos (até a linha em branco), sem consumir nada depois deles.</summary>
    private static async Task<LocalRequest?> ReadRequestAsync(Stream stream, IPEndPoint remote, bool trusted, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < 4 || !(buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n'))
        {
            if (length == buffer.Length)
            {
                return null;
            }

            // Um byte por vez: o que vier depois dos cabeçalhos já é do WebSocket.
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.1", StringComparison.Ordinal))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var path = parts[1];
        var query = path.IndexOf('?');
        return new LocalRequest(query >= 0 ? path[..query] : path, headers, remote, trusted);
    }

    private static Task WriteStatusAsync(Stream stream, int status, CancellationToken cancellationToken)
    {
        var text = status switch { 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 429 => "Too Many Requests", _ => "Bad Request" };
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {text}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).AsTask();
    }
}

/// <summary>Por que uma entrada na sala local falhou (o texto da exceção é o que se mostra).</summary>
public enum LocalFailure
{
    Other,

    /// <summary>Não respondeu (porta fechada no roteador, firewall, CGNAT) ou não é uma sala do ScreenCord.</summary>
    Unreachable,

    /// <summary>O certificado não é o esperado (do anúncio ou do convite).</summary>
    CertificateMismatch,

    /// <summary>Token ausente, errado ou trocado por um convite novo.</summary>
    TokenRejected,

    /// <summary>A sala do convite não existe mais.</summary>
    RoomClosed,

    /// <summary>Tentativas inválidas demais; o endereço está bloqueado por um tempo.</summary>
    Blocked,

    /// <summary>Versões diferentes do protocolo da sala.</summary>
    ProtocolMismatch,

    /// <summary>Link de convite malformado, adulterado ou de um formato desconhecido.</summary>
    InvalidInvite,

    /// <summary>O convite é da sala hospedada neste computador.</summary>
    OwnRoom,
}

/// <summary>Falha das salas locais com um texto próprio para exibição.</summary>
public sealed class LocalNetworkException(string message, Exception? inner = null, LocalFailure failure = LocalFailure.Other) : Exception(message, inner)
{
    public LocalFailure Failure { get; } = failure;
}

/// <summary>
/// Tentativas com chave ou token errado por endereço de fora das redes locais: depois de <see cref="MaxFailures"/>
/// em <see cref="FailureWindow"/>, o endereço fica <see cref="BlockDuration"/> sem poder tentar.
/// </summary>
public sealed class ConnectionGuard(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);

    /// <summary>Endereços guardados no máximo; acima disso os vencidos saem (muitos endereços tentando não esgotam a memória).</summary>
    private const int MaxTracked = 4096;

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];

    public bool IsBlocked(IPAddress address)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(address, out var entry) && entry.BlockedUntil > time.GetUtcNow();
        }
    }

    /// <summary>Conta uma tentativa inválida. Retorna true se o endereço acabou de ser bloqueado.</summary>
    public bool RecordFailure(IPAddress address)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_entries.Count >= MaxTracked)
            {
                foreach (var stale in _entries.Where(e => e.Value.BlockedUntil <= now && now - e.Value.WindowStart > FailureWindow).Select(e => e.Key).ToList())
                {
                    _entries.Remove(stale);
                }
            }

            if (!_entries.TryGetValue(address, out var entry) || now - entry.WindowStart > FailureWindow)
            {
                entry = new Entry(now, 0, DateTimeOffset.MinValue);
            }

            entry = entry with { Failures = entry.Failures + 1 };
            var blocked = entry.Failures >= MaxFailures;
            if (blocked)
            {
                // Recomeça a contagem: quando o bloqueio vence, são mais cinco tentativas até o próximo.
                entry = new Entry(now, 0, now + BlockDuration);
            }

            if (_entries.Count < MaxTracked || _entries.ContainsKey(address))
            {
                _entries[address] = entry;
            }

            return blocked;
        }
    }

    private sealed record Entry(DateTimeOffset WindowStart, int Failures, DateTimeOffset BlockedUntil);
}
