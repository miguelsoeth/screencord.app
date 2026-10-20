using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenCord.Contracts;

namespace ScreenCord.Local;

/// <summary>
/// Integrante de uma sala local. <see cref="DiscordId"/> e <see cref="AvatarHash"/> vêm da conta do Discord, quando
/// quem entrou tem uma; o perfil local não tem nenhum dos dois.
/// </summary>
public sealed record LocalMember(Guid Id, string Name, string? DiscordId = null, string? AvatarHash = null)
{
    public const int MaxNameLength = 32;

    /// <summary>Os dados vêm de outro computador: nome limpo e IDs do Discord só numéricos.</summary>
    public LocalMember? Sanitize()
    {
        var name = string.Join(' ', (Name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (Id == Guid.Empty || name.Length == 0)
        {
            return null;
        }

        return new LocalMember(
            Id,
            name.Length > MaxNameLength ? name[..MaxNameLength] : name,
            ulong.TryParse(DiscordId, out _) ? DiscordId : null,
            AvatarHash is { Length: <= 66 } hash && hash.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? AvatarHash : null);
    }

    public PresenceUserDto ToPresence() => new(Id, DiscordId ?? "0", Name, Name, "0", AvatarHash);
}

/// <summary>Compartilhamento de tela numa sala local: a mídia vai direto do computador de quem compartilha.</summary>
/// <param name="MediaId">Caminho do canal de mídia (<c>/media/{id}</c>) no computador de quem compartilha.</param>
/// <param name="Endpoints">Endereços (<c>ip:porta</c>) em que quem assiste tenta conectar, em paralelo.</param>
/// <param name="Key">Chave aleatória do compartilhamento; sem ela, quem compartilha recusa a conexão.</param>
public sealed record LocalShare(
    Guid UserId,
    int Width,
    int Height,
    int FrameRate,
    int BitrateKbps,
    bool HasAudio,
    Guid MediaId,
    IReadOnlyList<string> Endpoints,
    string Key)
{
    public ScreenShareDto ToDto() => new(UserId, Width, Height, FrameRate, BitrateKbps, HasAudio);

    public bool IsValid =>
        UserId != Guid.Empty && MediaId != Guid.Empty && Key is { Length: >= 16 and <= 128 } &&
        Endpoints is { Count: > 0 and <= 16 } &&
        ScreenShareLimits.IsValid(new ScreenShareStart(Width, Height, FrameRate, BitrateKbps, HasAudio), StreamLimits.Direct);
}

/// <summary>Estado de uma sala local, enviado pelo anfitrião a todos a cada mudança.</summary>
public sealed record LocalRoomState(
    Guid Id,
    string Name,
    Guid HostId,
    long Version,
    IReadOnlyList<LocalMember> Members,
    IReadOnlyList<LocalShare> Shares)
{
    public GroupPresenceDto ToPresence() =>
        new(Id, Members.Select(m => m.ToPresence()).ToList(), Version, Shares.Select(s => s.ToDto()).ToList());

    public GroupDto ToGroup() =>
        new(Id, Name, Members.Count, Members.Select(m => m.ToPresence()).ToList(), Version, Shares.Select(s => s.ToDto()).ToList());
}

/// <summary>
/// Mensagens da sala local, em JSON, uma por mensagem de texto do WebSocket.
/// Quem entra envia <c>hello</c>; o anfitrião responde com <c>state</c> (e de novo a cada mudança) ou <c>closed</c>.
/// Depois, quem entrou envia <c>share</c> e <c>unshare</c>.
/// </summary>
public sealed record LocalMessage(
    string Type,
    int Protocol = LocalMessage.CurrentProtocol,
    LocalMember? Member = null,
    LocalRoomState? Room = null,
    LocalShare? Share = null,
    string? Reason = null)
{
    public const int CurrentProtocol = 1;

    public const string Hello = "hello";
    public const string State = "state";
    public const string ShareStart = "share";
    public const string ShareStop = "unshare";
    public const string Closed = "closed";

    /// <summary>Maior mensagem aceita (uma sala cheia com todos compartilhando fica bem abaixo disso).</summary>
    public const int MaxBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task SendAsync(WebSocket socket, LocalMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lê a próxima mensagem; null quando a conexão fecha. Mensagens inválidas viram <see cref="InvalidDataException"/>.</summary>
    public static async Task<LocalMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= MaxBytes)
                {
                    throw new InvalidDataException("Mensagem grande demais.");
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxBytes));
            }

            result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            length += result.Count;
        }
        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LocalMessage>(buffer.AsSpan(0, length), Json) ?? throw new InvalidDataException("Mensagem vazia.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Mensagem inválida.", ex);
        }
    }

    public static async Task CloseAsync(WebSocket socket, string? reason = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var description = reason is null ? "" : Encoding.UTF8.GetByteCount(reason) <= 120 ? reason : "";
                // Só o envio do fechamento: pode haver uma leitura em andamento na outra ponta do laço.
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, description, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException)
        {
        }
    }
}
