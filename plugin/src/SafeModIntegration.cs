using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;

namespace FyteClub
{
    /// <summary>
    /// Mare-style safe API integration with proper error handling and rate limiting
    /// </summary>
    public class SafeModIntegration
    {
        private readonly IPluginLog _pluginLog;
        private readonly IDalamudPluginInterface _pluginInterface;
        
        // Mare-style API helpers instead of raw IPC
        private readonly SafeGetEnabledState? _penumbraGetEnabledState;
        private readonly SafeCreateCollection? _penumbraCreateCollection;
        private readonly SafeAssignCollection? _penumbraAssignCollection;
        
        // Rate limiter for mod operations
        private readonly SafeRateLimiter _modOperationLimiter;
        private readonly Random _random = new();
        
        public SafeModIntegration(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
        {
            _pluginInterface = pluginInterface;
            _pluginLog = pluginLog;
            
            // Initialize rate limiter: max 5 operations per 10 seconds
            _modOperationLimiter = new SafeRateLimiter(5, TimeSpan.FromSeconds(10));
            
            // Initialize Mare-style API helpers with proper error handling
            try
            {
                _penumbraGetEnabledState = new SafeGetEnabledState(pluginInterface);
                _penumbraCreateCollection = new SafeCreateCollection(pluginInterface);
                _penumbraAssignCollection = new SafeAssignCollection(pluginInterface);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to initialize mod API helpers: {ex.Message}");
            }
        }
        
        public async Task<bool> SafelyApplyModsAsync(string playerName, object modData)
        {
            // Rate limiting check
            if (!_modOperationLimiter.CanAttempt(playerName))
            {
                _pluginLog.Debug($"Rate limited mod application for {playerName}");
                return false;
            }
            
            try
            {
                // Record attempt for rate limiting
                _modOperationLimiter.RecordAttempt(playerName);
                
                // Add randomized delay to avoid synchronized timing
                var delay = _modOperationLimiter.GetRandomizedDelay(TimeSpan.FromMilliseconds(100));
                await Task.Delay(delay);
                
                // Use Mare-style API helpers instead of raw IPC
                if (_penumbraGetEnabledState?.Invoke() == true)
                {
                    var collectionId = _penumbraCreateCollection?.Invoke($"FyteClub_{playerName}");
                    if (collectionId.HasValue)
                    {
                        var success = _penumbraAssignCollection?.Invoke(collectionId.Value, 0) == true;
                        if (success)
                        {
                            _pluginLog.Debug($"Successfully applied mods for {playerName}");
                            return true;
                        }
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Safe mod application failed for {playerName}: {ex.Message}");
                return false;
            }
        }
        
        public bool IsPenumbraAvailable()
        {
            try
            {
                return _penumbraGetEnabledState?.Invoke() == true;
            }
            catch
            {
                return false;
            }
        }
    }
    
    // Mare-style API helper classes
    public class SafeGetEnabledState
    {
        private readonly ICallGateSubscriber<bool>? _subscriber;
        
        public SafeGetEnabledState(IDalamudPluginInterface pluginInterface)
        {
            try
            {
                _subscriber = pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
            }
            catch { }
        }
        
        public bool Invoke()
        {
            try
            {
                return _subscriber?.InvokeFunc() == true;
            }
            catch
            {
                return false;
            }
        }
    }
    
    public class SafeCreateCollection
    {
        private readonly ICallGateSubscriber<string, Guid>? _subscriber;
        
        public SafeCreateCollection(IDalamudPluginInterface pluginInterface)
        {
            try
            {
                _subscriber = pluginInterface.GetIpcSubscriber<string, Guid>("Penumbra.CreateNamedTemporaryCollection");
            }
            catch { }
        }
        
        public Guid? Invoke(string name)
        {
            try
            {
                return _subscriber?.InvokeFunc(name);
            }
            catch
            {
                return null;
            }
        }
    }
    
    public class SafeAssignCollection
    {
        private readonly ICallGateSubscriber<Guid, int, bool>? _subscriber;
        
        public SafeAssignCollection(IDalamudPluginInterface pluginInterface)
        {
            try
            {
                _subscriber = pluginInterface.GetIpcSubscriber<Guid, int, bool>("Penumbra.AssignTemporaryCollection");
            }
            catch { }
        }
        
        public bool Invoke(Guid collectionId, int objectIndex)
        {
            try
            {
                return _subscriber?.InvokeFunc(collectionId, objectIndex) == true;
            }
            catch
            {
                return false;
            }
        }
    }
    
    public class SafeRateLimiter
    {
        private readonly Dictionary<string, Queue<DateTime>> _attempts = new();
        private readonly int _maxAttempts;
        private readonly TimeSpan _window;
        private readonly Random _random = new();

        public SafeRateLimiter(int maxAttempts, TimeSpan window)
        {
            _maxAttempts = maxAttempts;
            _window = window;
        }

        public bool CanAttempt(string key)
        {
            var now = DateTime.UtcNow;
            
            if (!_attempts.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTime>();
                _attempts[key] = queue;
            }

            // Remove old attempts outside window
            while (queue.Count > 0 && now - queue.Peek() > _window)
                queue.Dequeue();

            return queue.Count < _maxAttempts;
        }

        public void RecordAttempt(string key)
        {
            var now = DateTime.UtcNow;
            
            if (!_attempts.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTime>();
                _attempts[key] = queue;
            }

            queue.Enqueue(now);
        }

        public TimeSpan GetRandomizedDelay(TimeSpan baseDelay)
        {
            // Add 0-50% random jitter to avoid synchronized timing
            var jitter = _random.NextDouble() * 0.5;
            return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * (1 + jitter));
        }
    }
}