using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Local;

/// <summary>Pedido de conexão recebido pelo <see cref="LocalServer"/>.</summary>
public sealed record LocalRequest(string Path, IReadOnlyDictionary<string, string> Headers, IPEndPoint Remote)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Resposta da rota: 101 com quem cuida do WebSocket, ou um status de recusa (404, 403...).</summary>
public sealed record LocalRoute(int Status, Func<WebSocket, CancellationToken, Task>? Handler = null)
{
    public static LocalRoute NotFound { get; } = new(404);

    public static LocalRoute Forbidden { get; } = new(403);

    public static LocalRoute Accept(Func<WebSocket, CancellationToken, Task> handler) => new(101, handler);
}

/// <summary>
/// Servidor WebSocket mínimo das salas locais, sobre <see cref="TcpListener"/>: escutar fora do localhost com o
/// <c>HttpListener</c> exigiria administrador (urlacl). Só aceita o upgrade para WebSocket; o resto recebe 400.
/// </summary>
public sealed class LocalServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHeaderBytes = 8 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<LocalRequest, LocalRoute> _route;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;

    public LocalServer(Func<LocalRequest, LocalRoute> route, ILogger logger)
    {
        _route = route;
        _logger = logger;
    }

    /// <summary>Porta em que está escutando (útil quando foi pedida a porta 0, nos testes).</summary>
    public int Port { get; private set; }

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
                $"A porta {port} deste computador está em uso por outro programa, e o ScreenCord não consegue abrir a sala local.", ex);
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

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(HandshakeTimeout);
                var request = await ReadRequestAsync(stream, (IPEndPoint)client.Client.RemoteEndPoint!, handshake.Token).ConfigureAwait(false);
                if (request is null || request.Header("upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) != true ||
                    request.Header("sec-websocket-key") is not { Length: > 0 } key)
                {
                    await WriteStatusAsync(stream, 400, handshake.Token).ConfigureAwait(false);
                    return;
                }

                var route = _route(request);
                if (route.Handler is null)
                {
                    await WriteStatusAsync(stream, route.Status, handshake.Token).ConfigureAwait(false);
                    return;
                }

                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
                var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                               $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), handshake.Token).ConfigureAwait(false);

                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(15),
                });
                await route.Handler(socket, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                _logger.LogDebug("Conexão local encerrada ({ExceptionType}).", ex.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro numa conexão da sala local.");
            }
        }
    }

    /// <summary>Lê a linha do pedido e os cabeçalhos (até a linha em branco), sem consumir nada depois deles.</summary>
    private static async Task<LocalRequest?> ReadRequestAsync(NetworkStream stream, IPEndPoint remote, CancellationToken cancellationToken)
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
        return new LocalRequest(query >= 0 ? path[..query] : path, headers, remote);
    }

    private static Task WriteStatusAsync(NetworkStream stream, int status, CancellationToken cancellationToken)
    {
        var text = status switch { 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", _ => "Bad Request" };
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {text}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).AsTask();
    }
}

/// <summary>Falha das salas locais com um texto próprio para exibição.</summary>
public sealed class LocalNetworkException(string message, Exception? inner = null) : Exception(message, inner);
