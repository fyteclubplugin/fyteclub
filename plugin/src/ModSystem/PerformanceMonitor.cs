using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.ModSystem
{
    public class PerformanceMonitor : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly ConcurrentDictionary<string, PerformanceMetrics> _metrics = new();
        private readonly Timer _reportTimer;

        public PerformanceMonitor(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _reportTimer = new Timer(ReportMetrics, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public async Task<T> LogPerformance<T>(object source, string operation, Func<Task<T>> action)
        {
            var key = $"{source.GetType().Name}.{operation}";
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                var result = await action();
                stopwatch.Stop();
                RecordSuccess(key, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                RecordFailure(key, stopwatch.ElapsedMilliseconds, ex);
                throw;
            }
        }

        public void LogPerformance(object source, string operation, Action action)
        {
            var key = $"{source.GetType().Name}.{operation}";
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                action();
                stopwatch.Stop();
                RecordSuccess(key, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                RecordFailure(key, stopwatch.ElapsedMilliseconds, ex);
                throw;
            }
        }

        private void RecordSuccess(string key, long elapsedMs)
        {
            _metrics.AddOrUpdate(key, 
                new PerformanceMetrics { TotalCalls = 1, TotalTime = elapsedMs, SuccessfulCalls = 1 },
                (k, existing) => new PerformanceMetrics
                {
                    TotalCalls = existing.TotalCalls + 1,
                    TotalTime = existing.TotalTime + elapsedMs,
                    SuccessfulCalls = existing.SuccessfulCalls + 1,
                    FailedCalls = existing.FailedCalls
                });
        }

        private void RecordFailure(string key, long elapsedMs, Exception ex)
        {
            _metrics.AddOrUpdate(key,
                new PerformanceMetrics { TotalCalls = 1, TotalTime = elapsedMs, FailedCalls = 1 },
                (k, existing) => new PerformanceMetrics
                {
                    TotalCalls = existing.TotalCalls + 1,
                    TotalTime = existing.TotalTime + elapsedMs,
                    SuccessfulCalls = existing.SuccessfulCalls,
                    FailedCalls = existing.FailedCalls + 1
                });

            _pluginLog.Warning($"Performance: {key} failed in {elapsedMs}ms - {ex.Message}");
        }

        private void ReportMetrics(object? state)
        {
            if (_metrics.IsEmpty) return;

            _pluginLog.Info("=== Performance Report ===");
            foreach (var kvp in _metrics)
            {
                var metrics = kvp.Value;
                var avgTime = metrics.TotalCalls > 0 ? metrics.TotalTime / metrics.TotalCalls : 0;
                var successRate = metrics.TotalCalls > 0 ? (metrics.SuccessfulCalls * 100.0 / metrics.TotalCalls) : 0;
                
                _pluginLog.Info($"{kvp.Key}: {metrics.TotalCalls} calls, {avgTime}ms avg, {successRate:F1}% success");
            }
        }

        public void Dispose()
        {
            _reportTimer?.Dispose();
            _metrics.Clear();
        }
    }

    public class PerformanceMetrics
    {
        public long TotalCalls { get; set; }
        public long TotalTime { get; set; }
        public long SuccessfulCalls { get; set; }
        public long FailedCalls { get; set; }
    }
}