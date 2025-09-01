using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class FyteClubPerformanceCollector
    {
        private readonly IPluginLog _pluginLog;
        private readonly ConcurrentDictionary<string, PerformanceMetrics> _metrics = new();

        public FyteClubPerformanceCollector(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        public PerformanceScope BeginScope(string operation)
        {
            return new PerformanceScope(operation, this);
        }

        internal void RecordOperation(string operation, long elapsedMs)
        {
            _metrics.AddOrUpdate(operation, 
                new PerformanceMetrics { TotalMs = elapsedMs, Count = 1 },
                (key, existing) => new PerformanceMetrics 
                { 
                    TotalMs = existing.TotalMs + elapsedMs, 
                    Count = existing.Count + 1 
                });

            if (elapsedMs > 100)
            {
                _pluginLog.Warning($"FyteClub: Slow operation '{operation}': {elapsedMs}ms");
            }
        }

        public void LogMetrics()
        {
            foreach (var kvp in _metrics)
            {
                var avg = kvp.Value.Count > 0 ? kvp.Value.TotalMs / kvp.Value.Count : 0;
                _pluginLog.Information($"FyteClub Perf: {kvp.Key} - {kvp.Value.Count} calls, {avg}ms avg");
            }
        }

        private class PerformanceMetrics
        {
            public long TotalMs { get; set; }
            public int Count { get; set; }
        }
    }

    public class PerformanceScope : IDisposable
    {
        private readonly string _operation;
        private readonly FyteClubPerformanceCollector _collector;
        private readonly Stopwatch _stopwatch;

        public PerformanceScope(string operation, FyteClubPerformanceCollector collector)
        {
            _operation = operation;
            _collector = collector;
            _stopwatch = Stopwatch.StartNew();
        }

        public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

        public void Dispose()
        {
            _stopwatch.Stop();
            _collector.RecordOperation(_operation, _stopwatch.ElapsedMilliseconds);
        }
    }
}