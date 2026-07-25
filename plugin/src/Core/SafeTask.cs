using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FyteClub.Core.Logging;

namespace FyteClub.Core
{
    /// <summary>
    /// Wrapper for fire-and-forget background work (docs/PLAN.md AD-6: "No naked
    /// `_ = Task.Run(...)`"). Catches any exception the work throws, logs it with module context
    /// via FyteLog, and tracks a fault count plus the most recent faults so a background failure
    /// is visible in the UI (Logging tab) instead of disappearing into an unobserved task
    /// exception.
    /// </summary>
    public static class SafeTask
    {
        private const int MaxRecentFaults = 20;
        private static int _faultCount;
        private static readonly object _recentLock = new();
        private static readonly LinkedList<FaultRecord> _recent = new();

        public readonly record struct FaultRecord(DateTime When, LogModule Module, string Context, string Message);

        public static int FaultCount => _faultCount;

        public static IReadOnlyList<FaultRecord> RecentFaults
        {
            get { lock (_recentLock) { return new List<FaultRecord>(_recent); } }
        }

        public static Task Run(Func<Task> work, LogModule module, [CallerMemberName] string context = "")
        {
            return Task.Run(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on cooperative shutdown - not a fault.
                }
                catch (Exception ex)
                {
                    RecordFault(module, context, ex);
                }
            });
        }

        public static Task Run(Action work, LogModule module, [CallerMemberName] string context = "")
        {
            return Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (OperationCanceledException)
                {
                    // Expected on cooperative shutdown - not a fault.
                }
                catch (Exception ex)
                {
                    RecordFault(module, context, ex);
                }
            });
        }

        public static Task Run(Func<Task> work, CancellationToken cancellationToken, LogModule module, [CallerMemberName] string context = "")
        {
            return Task.Run(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on cooperative shutdown - not a fault.
                }
                catch (Exception ex)
                {
                    RecordFault(module, context, ex);
                }
            }, cancellationToken);
        }

        public static Task Run(Action work, CancellationToken cancellationToken, LogModule module, [CallerMemberName] string context = "")
        {
            return Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (OperationCanceledException)
                {
                    // Expected on cooperative shutdown - not a fault.
                }
                catch (Exception ex)
                {
                    RecordFault(module, context, ex);
                }
            }, cancellationToken);
        }

        private static void RecordFault(LogModule module, string context, Exception ex)
        {
            Interlocked.Increment(ref _faultCount);
            FyteLog.Error(module, "Unhandled exception in background task '{0}': {1}", context, ex.Message);

            lock (_recentLock)
            {
                _recent.AddFirst(new FaultRecord(DateTime.UtcNow, module, context, ex.Message));
                while (_recent.Count > MaxRecentFaults)
                {
                    _recent.RemoveLast();
                }
            }
        }
    }
}
