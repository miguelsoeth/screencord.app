using System.Net;
using System.Net.Http;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Services;

public enum RealtimeState
{
    Disconnected,
    Connecting,
    Connected,
    /// <summary>A conexão caiu e está sendo refeita; a presença nas salas foi perdida no servidor.</summary>
    Reconnecting,
}

/// <summary>
/// Conexão persistente com o hub de grupos. Os eventos chegam em threads do pool; quem atualiza a
/// interface precisa levá-los para a thread da UI.
/// </summary>
public interface IGroupsRealtime
{
    RealtimeState State { get; }

    event EventHandler<RealtimeState>? StateChanged;

    event EventHandler<GroupPresenceDto>? PresenceChanged;

    event EventHandler<GroupDto>? GroupChanged;

    event EventHandler<Guid>? GroupRemoved;

    /// <summary>Abre a conexão em segundo plano, tentando de novo até conseguir ou até <see cref="StopAsync"/>.</summary>
    void Start();

    /// <summary>Entra na sala do grupo (saindo da anterior) e retorna quem está conectado.</summary>
    Task<GroupPresenceDto> JoinAsync(Guid groupId, CancellationToken cancellationToken = default);

    Task LeaveAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// Cliente SignalR (WebSocket, com fallback automático) autenticado com o token de sessão da API central.
/// A reconexão é feita aqui, e não pelo <c>WithAutomaticReconnect</c>, para que um 401 (sessão da API
/// vencida) descarte o token e a próxima tentativa faça login de novo.
/// </summary>
public sealed class GroupsRealtimeClient : IGroupsRealtime
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    private readonly CentralApiClient _api;
    private readonly ILogger<GroupsRealtimeClient> _logger;
    private readonly Lock _lock = new();

    private HubConnection? _connection;
    private CancellationTokenSource? _lifetime;
    private string? _lastToken;
    private RealtimeState _state;

    public GroupsRealtimeClient(CentralApiClient api, ILogger<GroupsRealtimeClient> logger)
    {
        _api = api;
        _logger = logger;
    }

    public event EventHandler<RealtimeState>? StateChanged;

    public event EventHandler<GroupPresenceDto>? PresenceChanged;

    public event EventHandler<GroupDto>? GroupChanged;

    public event EventHandler<Guid>? GroupRemoved;

    public RealtimeState State => _state;

    public void Start()
    {
        HubConnection connection;
        CancellationToken lifetime;
        lock (_lock)
        {
            if (_lifetime is not null)
            {
                return;
            }

            var hubUri = new Uri(_api.BaseUri, GroupsHubContract.Path.TrimStart('/'));
            connection = new HubConnectionBuilder()
                .WithUrl(hubUri, options =>
                {
                    // Enviado no cabeçalho Authorization (nunca na URL) em todos os transportes.
                    options.AccessTokenProvider = async () => _lastToken = await _api.GetApiTokenAsync().ConfigureAwait(false);
                })
                .Build();

            connection.On<GroupPresenceDto>(GroupsHubContract.PresenceChanged, presence => PresenceChanged?.Invoke(this, presence));
            connection.On<GroupDto>(GroupsHubContract.GroupChanged, group =>
            {
                if (CentralGroupsService.IsValid(group))
                {
                    GroupChanged?.Invoke(this, group);
                }
            });
            connection.On<Guid>(GroupsHubContract.GroupRemoved, groupId => GroupRemoved?.Invoke(this, groupId));

            _lifetime = new CancellationTokenSource();
            lifetime = _lifetime.Token;
            connection.Closed += error =>
            {
                if (!lifetime.IsCancellationRequested)
                {
                    _logger.LogWarning("Conexão em tempo real perdida ({Error}); reconectando.", error?.GetType().Name ?? "sem erro");
                    _ = ConnectLoopAsync(connection, RealtimeState.Reconnecting, lifetime);
                }

                return Task.CompletedTask;
            };
            _connection = connection;
        }

        _ = ConnectLoopAsync(connection, RealtimeState.Connecting, lifetime);
    }

    public async Task<GroupPresenceDto> JoinAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var presence = await InvokeAsync(
            connection => connection.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, groupId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return presence ?? throw new DiscordException(DiscordErrorKind.Rejected, "Você não faz parte deste grupo.") { Source = ErrorSource.CentralApi };
    }

    public Task LeaveAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>(
            async connection =>
            {
                await connection.InvokeAsync(GroupsHubContract.LeaveGroup, cancellationToken).ConfigureAwait(false);
                return null;
            },
            cancellationToken);

    public async Task StopAsync()
    {
        HubConnection? connection;
        lock (_lock)
        {
            connection = _connection;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
            _connection = null;
        }

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao encerrar a conexão em tempo real.");
            }
        }

        SetState(RealtimeState.Disconnected);
    }

    private async Task ConnectLoopAsync(HubConnection connection, RealtimeState pendingState, CancellationToken lifetime)
    {
        for (var attempt = 0; !lifetime.IsCancellationRequested; attempt++)
        {
            SetState(pendingState);
            try
            {
                await connection.StartAsync(lifetime).ConfigureAwait(false);
                _logger.LogInformation("Conexão em tempo real estabelecida.");
                SetState(RealtimeState.Connected);
                return;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Sessão da API vencida: a próxima tentativa faz login de novo.
                if (_lastToken is { } token)
                {
                    _api.InvalidateToken(token);
                }
            }
            catch (DiscordException ex) when (ex.Source == ErrorSource.Discord && ex.Kind == DiscordErrorKind.Unauthorized)
            {
                _logger.LogWarning("Sessão do Discord indisponível; a conexão em tempo real não será aberta.");
                SetState(RealtimeState.Disconnected);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Falha ao conectar em tempo real ({ExceptionType}).", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)], lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<T> InvokeAsync<T>(Func<HubConnection, Task<T>> invoke, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            throw NotConnected();
        }

        try
        {
            return await invoke(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not DiscordException)
        {
            _logger.LogWarning("Chamada ao hub falhou ({ExceptionType}).", ex.GetType().Name);
            throw NotConnected(ex);
        }
    }

    private void SetState(RealtimeState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private static DiscordException NotConnected(Exception? inner = null) =>
        CentralApiClient.Error(DiscordErrorKind.Network, "Sem conexão em tempo real com a API central.", inner);
}
