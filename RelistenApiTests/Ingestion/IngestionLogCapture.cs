using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RelistenApiTests.Ingestion;

internal sealed class IngestionLogCapture : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    internal ConcurrentQueue<Entry> Entries { get; } = new();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose() { }

    internal record Entry(
        string Category,
        EventId Event,
        LogLevel Level,
        IReadOnlyDictionary<string, object?> Fields
    );

    private sealed class CaptureLogger(IngestionLogCapture capture, string category) : ILogger
    {
        public bool IsEnabled(LogLevel level) =>
            category.StartsWith("Relisten.Import") || category.StartsWith("Relisten.Ingestion");

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => capture.scopes.Push(state);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
                return;
            var fields = new Dictionary<string, object?>();
            capture.scopes.ForEachScope((scope, values) => AddFields(scope, values), fields);
            AddFields(state, fields);
            capture.Entries.Enqueue(new Entry(category, eventId, logLevel, fields));
        }

        private static void AddFields(object? state, Dictionary<string, object?> fields)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var pair in values)
                    fields[pair.Key] = pair.Value;
        }
    }
}
