using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using ScreenCord.Contracts;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Envia à API os avisos e erros do log (com a pilha das exceções e o trace da requisição que falhou),
/// para o painel de observabilidade. Vai em lotes, em segundo plano; sem rede, tenta de novo depois.
/// Não leva tokens nem dados de conta: a instalação é identificada por um id aleatório.
/// Falhas no envio são ignoradas: o relatório nunca derruba o aplicativo.
/// </summary>
public sealed class ErrorReporter : IDisposable
{
    /// <summary>Entradas guardadas esperando envio; acima disso as mais novas são descartadas.</summary>
    public const int MaxQueued = 200;

    /// <summary>Total enviado por execução: um erro em laço não vira uma avalanche.</summary>
    public const int MaxPerRun = 500;

    /// <summary>Repetições do mesmo erro enviadas por execução.</summary>
    public const int MaxRepeats = 5;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _environment;
    private readonly Guid _installId;
    private readonly ConcurrentQueue<ClientLogEntry> _queue = new();
    private readonly ConcurrentDictionary<string, int> _repeats = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly Timer _timer;
    private int _queued;
    private int _accepted;
    private bool _disposed;

    public ErrorReporter(Uri apiBase, string environment, Guid installId, HttpMessageHandler? handler = null, TimeSpan? interval = null)
    {
        _endpoint = new Uri(apiBase, TelemetryContract.ErrorsPath.TrimStart('/'));
        _environment = environment;
        _installId = installId;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
        var period = interval ?? TimeSpan.FromSeconds(30);
        _timer = new Timer(_ => _ = FlushAsync(), null, period, period);
    }

    public Guid RunId { get; } = Guid.NewGuid();

    /// <summary>Entradas esperando envio.</summary>
    public int Pending => Volatile.Read(ref _queued);

    public void Add(LogLevel level, string category, string message, Exception? exception, string? traceId)
    {
        if (level < LogLevel.Warning || level == LogLevel.None)
        {
            return;
        }

        var text = exception?.ToString();
        var key = $"{category}|{message}|{exception?.GetType().FullName}";
        if (_repeats.AddOrUpdate(key, 1, (_, n) => n + 1) > MaxRepeats ||
            Interlocked.Increment(ref _accepted) > MaxPerRun)
        {
            return;
        }

        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            return;
        }

        _queue.Enqueue(new ClientLogEntry(
            DateTimeOffset.UtcNow,
            level.ToString(),
            category,
            Truncate(message, 4000)!,
            Truncate(text, 16000),
            traceId ?? TraceIdOf(exception) ?? CurrentTraceId()));
    }

    /// <summary>Envia o que estiver na fila. Sem sucesso, as entradas voltam para a próxima tentativa.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_queue.IsEmpty || !await _sending.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (!_queue.IsEmpty && !_disposed)
            {
                var batch = new List<ClientLogEntry>();
                while (batch.Count < TelemetryContract.MaxEntries && _queue.TryDequeue(out var entry))
                {
                    batch.Add(entry);
                }

                var report = new ClientErrorReport(AppVersion.Current, Environment.OSVersion.VersionString, _environment, _installId, RunId, batch);
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(report) };
                request.Headers.TryAddWithoutValidation(ClientVersionContract.Header, AppVersion.Current);
                try
                {
                    using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    // 4xx: o relatório não serve para a API, não adianta repetir. 429 e 5xx: tenta depois.
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                    {
                        Requeue(batch);
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
                {
                    Requeue(batch);
                    return;
                }

                Interlocked.Add(ref _queued, -batch.Count);
            }
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Envio rápido no encerramento (inclusive numa queda): espera no máximo <paramref name="timeout"/>.</summary>
    public void Flush(TimeSpan timeout)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            Task.Run(() => FlushAsync(cancellation.Token)).Wait(timeout);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _http.Dispose();
    }

    /// <summary>Id aleatório desta instalação, criado na primeira execução (sem ligação com a conta).</summary>
    public static Guid LoadInstallId(string folder)
    {
        var path = Path.Combine(folder, "install-id");
        try
        {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
            {
                return existing;
            }

            var created = Guid.NewGuid();
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, created.ToString());
            return created;
        }
        catch (Exception)
        {
            return Guid.NewGuid();
        }
    }

    private void Requeue(List<ClientLogEntry> batch)
    {
        foreach (var entry in batch)
        {
            _queue.Enqueue(entry);
        }
    }

    private static string? TraceIdOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DiscordException { TraceId: { } traceId })
            {
                return traceId;
            }
        }

        return null;
    }

    private static string? CurrentTraceId() =>
        Activity.Current is { } activity && activity.TraceId != default ? activity.TraceId.ToHexString() : null;

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + "…";
}

/// <summary>Leva ao <see cref="ErrorReporter"/> os avisos e erros de todas as categorias do log.</summary>
public sealed class ErrorReporterLoggerProvider(ErrorReporter reporter) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ReportingLogger(reporter, categoryName);

    public void Dispose()
    {
    }

    private sealed class ReportingLogger(ErrorReporter reporter, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Logs que citam o trace da requisição ("... trace {TraceId}") o levam para o painel.
            var traceId = state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(v => v.Key == "TraceId").Value as string
                : null;
            reporter.Add(logLevel, category, formatter(state, exception), exception, traceId);
        }
    }
}
