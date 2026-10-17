using Microsoft.Extensions.Logging;

namespace Glamour.Host.Telemetry;

/// <summary>
/// Which log entries become Sentry errors: Error and Critical (a feature failed, a save failed), and script errors from the web UI.
/// Warnings stay in the log file: most carry a site being down or a cache write, and would fill the error quota in one outage.
/// Event lines are only remembered, for the scrubber.
/// </summary>
public sealed class TelemetryLog(Telemetry telemetry) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(telemetry, categoryName);

    public void Dispose() { }

    private sealed class Logger(Telemetry telemetry, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (category == "Glamour.Events") { telemetry.Saw(category + ": " + formatter(state, exception)); return; }
            var web = category == "Glamour.Web" && logLevel >= LogLevel.Warning;
            if (logLevel < LogLevel.Error && !web) return;
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            string Value(string key) => values.FirstOrDefault(p => p.Key == key).Value?.ToString() ?? "";
            if (web)
            {
                // A script error: its kind and message group it; the device's name and the page's address stay out.
                var text = Value("Kind") + ": " + Value("Message");
                telemetry.Error(category, text, text + Value("Stack"), null);
                return;
            }
            var message = formatter(state, exception);
            // The template groups one error however its values differ.
            telemetry.Error(category, Value("{OriginalFormat}") is { Length: > 0 } t ? t : message, message, exception, fatal: logLevel == LogLevel.Critical);
        }
    }
}
