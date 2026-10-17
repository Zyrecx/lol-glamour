using Microsoft.Extensions.Logging;

namespace Glamour.Host.Logging;

/// <summary>
/// The host's log file, %LOCALAPPDATA%\Glamour\logs\glamour.log, for both the console host and the desktop app (which has no console). One file of at most ~5 MB plus the one before it.
/// The desktop app makes one and shares it with every host it builds (phone access restarts the host), so it outlives them; the levels come from the host's Logging config.
/// </summary>
public sealed class FileLog : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly string _path, _old;
    private StreamWriter? _writer;
    private readonly long _maxBytes;
    private bool _disposed;

    public FileLog(string dir, long maxBytes = 5_000_000)
    {
        _maxBytes = maxBytes;
        _path = Path.Combine(dir, "glamour.log");
        _old = Path.Combine(dir, "glamour.1.log");
        try { Directory.CreateDirectory(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* then nothing is logged */ }
    }

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "logs");

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Short(level)} {category}: {message}";
        if (ex is not null) line += Environment.NewLine + ex;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_writer is null || _writer.BaseStream.Length > _maxBytes) Open();
                _writer!.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _writer?.Dispose(); _writer = null; } // logging never stops the app
        }
    }

    /// <summary>Opens the log for appending; a full one becomes glamour.1.log first (replacing the older one).</summary>
    private void Open()
    {
        _writer?.Dispose();
        _writer = null;
        if (File.Exists(_path) && new FileInfo(_path).Length > _maxBytes) File.Move(_path, _old, overwrite: true);
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
    }

    private static string Short(LogLevel l) => l switch
    {
        LogLevel.Trace => "trce", LogLevel.Debug => "dbug", LogLevel.Information => "info", LogLevel.Warning => "warn", LogLevel.Error => "fail", _ => "crit",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class Logger(FileLog file, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) file.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
