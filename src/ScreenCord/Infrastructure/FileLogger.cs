using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Grava o log num arquivo por dia em <c>%LOCALAPPDATA%\ScreenCord\logs</c>, guardando os últimos
/// <see cref="KeepDays"/> dias. É o que dá para pedir a quem usa a versão instalada quando algo falha.
/// Falhas de escrita são ignoradas: o log nunca derruba o aplicativo.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int KeepDays = 7;

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _writerDay;
    private bool _disposed;

    public FileLoggerProvider(string folder, TimeProvider? time = null)
    {
        _folder = folder;
        _time = time ?? TimeProvider.System;
    }

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenCord", "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = _time.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(Level(level)).Append(' ')
            .Append(category).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                WriterFor(DateOnly.FromDateTime(now.DateTime)).WriteLine(line.ToString());
            }
            catch (Exception)
            {
            }
        }
    }

    private StreamWriter WriterFor(DateOnly day)
    {
        if (_writer is not null && _writerDay == day)
        {
            return _writer;
        }

        _writer?.Dispose();
        _writer = null;
        Directory.CreateDirectory(_folder);
        DeleteOld(day);
        var path = Path.Combine(_folder, $"screencord-{day:yyyyMMdd}.log");
        // Outra instância do app pode estar escrevendo no mesmo arquivo.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _writerDay = day;
        return _writer;
    }

    private void DeleteOld(DateOnly today)
    {
        var oldest = today.AddDays(-(KeepDays - 1));
        foreach (var file in Directory.EnumerateFiles(_folder, "screencord-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file)["screencord-".Length..];
            if (DateOnly.TryParseExact(name, "yyyyMMdd", out var day) && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
