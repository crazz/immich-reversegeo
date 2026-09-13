using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

internal sealed class RecordingLifecycleLogs : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private readonly ConcurrentQueue<Entry> _entries = new();

    internal Entry[] Entries => _entries.ToArray();
    internal void DrainEntries()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }
    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    internal sealed record Entry(string Category, EventId Event, LogLevel Level,
        KeyValuePair<string, object?>[] State, object?[] Scopes, string Rendered, Exception? Exception)
    {
        internal object? this[string name] => State.Single(item => item.Key == name).Value;
        internal string Template => (string)this["{OriginalFormat}"]!;
    }

    private sealed class Recorder(RecordingLifecycleLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<object?>();
            owner._scopes.ForEachScope(static (scope, items) => items.Add(scope), scopes);
            owner._entries.Enqueue(new(category, eventId, logLevel,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray(), scopes.ToArray(),
                formatter(state, exception), exception));
        }
    }
}
