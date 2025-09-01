using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class FyteClubConnectionManager : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly HttpClient _httpClient;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly string _daemonUrl;
        
        private bool _isConnected = false;
        private int _reconnectAttempts = 0;
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        
        public bool IsConnected => _isConnected;
        public event Action<bool>? ConnectionStateChanged;

        public FyteClubConnectionManager(IPluginLog pluginLog, string daemonUrl = "http://localhost:8080")
        {
            _pluginLog = pluginLog;
            _daemonUrl = daemonUrl;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _cancellationTokenSource = new CancellationTokenSource();
            
            _ = Task.Run(MaintainConnection);
        }

        private async Task MaintainConnection()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var wasConnected = _isConnected;
                    
                    if (!_isConnected)
                    {
                        await AttemptConnection();
                    }
                    else
                    {
                        await VerifyConnection();
                    }
                    
                    if (wasConnected != _isConnected)
                    {
                        ConnectionStateChanged?.Invoke(_isConnected);
                    }
                    
                    var delay = _isConnected ? 10000 : GetReconnectDelay();
                    await Task.Delay(delay, _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _pluginLog.Error($"FyteClub: Connection maintenance error - {ex.Message}");
                    await Task.Delay(5000, _cancellationTokenSource.Token);
                }
            }
        }

        private async Task AttemptConnection()
        {
            if (DateTime.UtcNow - _lastConnectionAttempt < TimeSpan.FromSeconds(2)) return;
                
            _lastConnectionAttempt = DateTime.UtcNow;
            _reconnectAttempts++;
            
            try
            {
                var response = await _httpClient.GetAsync($"{_daemonUrl}/api/health", _cancellationTokenSource.Token);
                if (response.IsSuccessStatusCode)
                {
                    _isConnected = true;
                    _reconnectAttempts = 0;
                    _pluginLog.Information($"FyteClub: Connected to daemon");
                }
            }
            catch (Exception ex)
            {
                _isConnected = false;
                if (_reconnectAttempts <= 3)
                {
                    _pluginLog.Information($"FyteClub: Connection attempt {_reconnectAttempts} failed: {ex.Message}");
                }
            }
        }

        private async Task VerifyConnection()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_daemonUrl}/api/health", _cancellationTokenSource.Token);
                _isConnected = response.IsSuccessStatusCode;
            }
            catch { _isConnected = false; }
        }

        private int GetReconnectDelay()
        {
            // exponential backoff with jitter
            var baseDelay = Math.Min(1000 * Math.Pow(2, Math.Min(_reconnectAttempts - 1, 6)), 30000);
            var jitter = new Random().Next(0, 1000);
            return (int)(baseDelay + jitter);
        }

        public async Task<HttpResponseMessage?> SendRequest(HttpRequestMessage request)
        {
            if (!_isConnected) return null;

            try
            {
                return await _httpClient.SendAsync(request, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: Request failed - {ex.Message}");
                _isConnected = false;
                return null;
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _httpClient.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }
}