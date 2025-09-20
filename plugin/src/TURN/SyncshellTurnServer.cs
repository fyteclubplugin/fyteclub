using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.TURN
{
    public class SyncshellTurnServer : IDisposable
    {
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public string ExternalIP { get; private set; } = "";
        public string Username { get; private set; }
        public string Password { get; private set; }
        public int ActiveConnections { get; private set; }
        
        private UdpClient? _udpServer;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly HashSet<string> _allowedSyncshells = new();
        private readonly Dictionary<string, DateTime> _activeClients = new();
        private readonly Dictionary<string, TurnPeerInfo> _peerServers = new();
        private readonly IPluginLog? _pluginLog;
        
        private const int MAX_CONNECTIONS = 20;
        private const int MAX_BANDWIDTH_MBPS = 10;

        public SyncshellTurnServer(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
            Username = GenerateCredential();
            Password = GenerateCredential();
        }

        public async Task<bool> StartAsync(int preferredPort = 3478)
        {
            if (IsRunning) return true;

            try
            {
                Port = await FindAvailablePort(preferredPort);
                _udpServer = new UdpClient(Port);
                ExternalIP = await GetExternalIP();
                _cancellationTokenSource = new CancellationTokenSource();
                
                _ = Task.Run(() => RunTurnServer(_cancellationTokenSource.Token));
                _ = Task.Run(() => AutoConfigureNetwork());
                _ = Task.Run(() => BroadcastLoadInfo(_cancellationTokenSource.Token));
                
                IsRunning = true;
                _pluginLog?.Info($"[TURN] Server started on {ExternalIP}:{Port}");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[TURN] Failed to start server: {ex.Message}");
                return false;
            }
        }

        public void AddAllowedSyncshell(string syncshellId)
        {
            lock (_allowedSyncshells)
            {
                _allowedSyncshells.Add(syncshellId);
            }
        }

        private async Task<int> FindAvailablePort(int startPort)
        {
            for (int port = startPort; port < startPort + 100; port++)
            {
                try
                {
                    using var test = new UdpClient(port);
                    return port;
                }
                catch (SocketException)
                {
                    continue;
                }
            }
            throw new InvalidOperationException("No available ports found");
        }

        private async Task<string> GetExternalIP()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var ip = await client.GetStringAsync("https://api.ipify.org");
                return ip.Trim();
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private async Task RunTurnServer(CancellationToken cancellationToken)
        {
            if (_udpServer == null) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpServer.ReceiveAsync();
                    _ = Task.Run(() => HandleTurnRequest(result.Buffer, result.RemoteEndPoint));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[TURN] Error: {ex.Message}");
                }
            }
        }

        private void HandleTurnRequest(byte[] data, IPEndPoint remoteEndPoint)
        {
            var clientKey = remoteEndPoint.ToString();
            
            // Handle peer server load broadcasts
            if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xFE)
            {
                HandlePeerLoadBroadcast(data, remoteEndPoint);
                return;
            }
            
            // Handle peer lookup requests (find which server has a specific user)
            if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xFC)
            {
                HandlePeerLookupRequest(data, remoteEndPoint);
                return;
            }
            
            // Update active client tracking
            lock (_activeClients)
            {
                _activeClients[clientKey] = DateTime.UtcNow;
                
                // Clean up old clients (5 minute timeout)
                var cutoff = DateTime.UtcNow.AddMinutes(-5);
                var expiredClients = _activeClients.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
                foreach (var expired in expiredClients)
                {
                    _activeClients.Remove(expired);
                }
                
                ActiveConnections = _activeClients.Count;
                
                // Proximity-aware overflow handling
                if (ActiveConnections >= 18) // Start redirecting at 18 to prevent hard limit
                {
                    var betterServer = GetBestOverflowServer();
                    if (betterServer != null)
                    {
                        SendRedirect(remoteEndPoint, betterServer);
                        _pluginLog?.Info($"[TURN] Proximity overflow: redirecting to server with {betterServer.Load} users");
                        return;
                    }
                }
                
                if (ActiveConnections > MAX_CONNECTIONS)
                {
                    _pluginLog?.Warning($"[TURN] Hard connection limit reached ({ActiveConnections}/{MAX_CONNECTIONS}), rejecting {clientKey}");
                    return;
                }
            }
            
            // Basic rate limiting - drop large packets during high load
            if (data.Length > 1024 && ActiveConnections > MAX_CONNECTIONS / 2)
            {
                return;
            }
        }

        private async Task AutoConfigureNetwork()
        {
            await Task.Delay(100);
            _pluginLog?.Info($"[TURN] Auto-configuring network for port {Port}");
            
            // Test external accessibility
            var isExternallyAccessible = await TestExternalConnectivity();
            if (!isExternallyAccessible)
            {
                _pluginLog?.Warning($"[TURN] Port {Port} may not be externally accessible - check router port forwarding");
            }
        }
        
        public async Task<bool> TestExternalConnectivity()
        {
            try
            {
                // Test with multiple STUN servers to verify external UDP connectivity
                var stunServers = new[]
                {
                    "stun.l.google.com:19302",
                    "stun1.l.google.com:19302",
                    "stun.cloudflare.com:3478"
                };
                
                foreach (var stunServer in stunServers)
                {
                    if (await TestStunConnectivity(stunServer))
                    {
                        _pluginLog?.Info($"[TURN] External UDP connectivity confirmed via {stunServer}");
                        return true;
                    }
                }
                
                _pluginLog?.Warning($"[TURN] Could not confirm external UDP connectivity");
                return false;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[TURN] External connectivity test failed: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> TestStunConnectivity(string stunServer)
        {
            try
            {
                var parts = stunServer.Split(':');
                var host = parts[0];
                var port = int.Parse(parts[1]);
                
                using var testClient = new UdpClient();
                testClient.Client.ReceiveTimeout = 3000;
                
                // Simple STUN binding request
                var stunRequest = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42, 
                                             0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
                                             0x00, 0x00, 0x00, 0x00 };
                
                await testClient.SendAsync(stunRequest, stunRequest.Length, host, port);
                var response = await testClient.ReceiveAsync();
                
                return response.Buffer.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private string GenerateCredential()
        {
            return Guid.NewGuid().ToString("N")[..12];
        }

        public void Stop()
        {
            if (!IsRunning) return;
            
            try
            {
                _pluginLog?.Info("[TURN] Shutting down server gracefully...");
                
                // Notify all active clients to migrate to alternative servers
                NotifyClientsOfShutdown();
                
                // Brief delay to allow migration messages to be sent
                Task.Delay(500).Wait();
                
                // Cancel all background tasks
                _cancellationTokenSource?.Cancel();
                
                // Close UDP server to stop accepting new connections
                _udpServer?.Close();
                _udpServer?.Dispose();
                _udpServer = null;
                
                // Wait briefly for tasks to complete
                Task.Delay(500).Wait();
                
                IsRunning = false;
                _pluginLog?.Info("[TURN] Server shutdown complete");
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[TURN] Error during shutdown: {ex.Message}");
                IsRunning = false;
            }ncel();
            _udpServer?.Close();
            _udpServer?.Dispose();
            IsRunning = false;
        }

        public void AddPeerServer(TurnServerInfo peerInfo)
        {
            lock (_peerServers)
            {
                _peerServers[peerInfo.Url] = new TurnPeerInfo
                {
                    ServerInfo = peerInfo,
                    LastSeen = DateTime.UtcNow,
                    Load = peerInfo.UserCount
                };
            }
        }
        
        private async Task BroadcastLoadInfo(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var loadPacket = CreateLoadBroadcast();
                    
                    lock (_peerServers)
                    {
                        foreach (var peer in _peerServers.Values)
                        {
                            try
                            {
                                var parts = peer.ServerInfo.Url.Replace("turn:", "").Split(':');
                                if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                                {
                                    await _udpServer?.SendAsync(loadPacket, loadPacket.Length, parts[0], port);
                                }
                            }
                            catch { /* Ignore failed broadcasts */ }
                        }
                    }
                    
                    await Task.Delay(30000, cancellationToken); // Broadcast every 30 seconds
                }
                catch (OperationCanceledException) { break; }
                catch { /* Continue on errors */ }
            }
        }
        
        private byte[] CreateLoadBroadcast()
        {
            var packet = new byte[8];
            packet[0] = 0xFF; // Peer broadcast marker
            packet[1] = 0xFE;
            packet[2] = (byte)(ActiveConnections & 0xFF);
            packet[3] = (byte)((ActiveConnections >> 8) & 0xFF);
            packet[4] = (byte)(Port & 0xFF);
            packet[5] = (byte)((Port >> 8) & 0xFF);
            return packet;
        }
        
        private void HandlePeerLoadBroadcast(byte[] data, IPEndPoint remoteEndPoint)
        {
            if (data.Length < 6) return;
            
            var peerLoad = data[2] | (data[3] << 8);
            var peerPort = data[4] | (data[5] << 8);
            var peerUrl = $"turn:{remoteEndPoint.Address}:{peerPort}";
            
            lock (_peerServers)
            {
                if (_peerServers.ContainsKey(peerUrl))
                {
                    _peerServers[peerUrl].Load = peerLoad;
                    _peerServers[peerUrl].LastSeen = DateTime.UtcNow;
                }
            }
        }
        
        private TurnPeerInfo? GetBestOverflowServer()
        {
            lock (_peerServers)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-2);
                var availablePeers = _peerServers.Values.Where(p => p.LastSeen > cutoff).ToList();
                
                // Prefer servers with some users (proximity clustering) but not full
                var proximityServers = availablePeers.Where(p => p.Load >= 5 && p.Load < 15).ToList();
                if (proximityServers.Count > 0)
                {
                    return proximityServers.OrderByDescending(p => p.Load).First();
                }
                
                // Fall back to any server with capacity
                return availablePeers.Where(p => p.Load < 18).OrderBy(p => p.Load).FirstOrDefault();
            }
        }
        
        public async Task<string?> FindPeerServer(string targetUserId)
        {
            var lookupPacket = new byte[64];
            lookupPacket[0] = 0xFF; // Peer lookup marker
            lookupPacket[1] = 0xFC;
            var userIdBytes = System.Text.Encoding.UTF8.GetBytes(targetUserId);
            Array.Copy(userIdBytes, 0, lookupPacket, 2, Math.Min(userIdBytes.Length, 62));
            
            lock (_peerServers)
            {
                foreach (var peer in _peerServers.Values)
                {
                    try
                    {
                        var parts = peer.ServerInfo.Url.Replace("turn:", "").Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                        {
                            await _udpServer?.SendAsync(lookupPacket, lookupPacket.Length, parts[0], port);
                        }
                    }
                    catch { /* Ignore failed lookups */ }
                }
            }
            
            // Wait briefly for responses (simple implementation)
            await Task.Delay(500);
            
            // Check if any peer responded with the user location
            lock (_peerServers)
            {
                return _peerServers.Values
                    .Where(p => p.HasUser?.Contains(targetUserId) == true)
                    .FirstOrDefault()?.ServerInfo.Url;
            }
        }
        
        private void HandlePeerLookupRequest(byte[] data, IPEndPoint remoteEndPoint)
        {
            if (data.Length < 4) return;
            
            var targetUserId = System.Text.Encoding.UTF8.GetString(data, 2, data.Length - 2).TrimEnd('\0');
            
            // Check if we have this user
            bool hasUser;
            lock (_activeClients)
            {
                hasUser = _activeClients.ContainsKey(targetUserId) || 
                         _activeClients.Keys.Any(k => k.Contains(targetUserId));
            }
            
            if (hasUser)
            {
                // Send response that we have this user
                var responsePacket = new byte[64];
                responsePacket[0] = 0xFF;
                responsePacket[1] = 0xFB; // Lookup response
                var urlBytes = System.Text.Encoding.UTF8.GetBytes($"turn:{ExternalIP}:{Port}");
                Array.Copy(urlBytes, 0, responsePacket, 2, Math.Min(urlBytes.Length, 62));
                
                try
                {
                    _udpServer?.Send(responsePacket, responsePacket.Length, remoteEndPoint);
                }
                catch { /* Ignore response failures */ }
            }
        }
        
        private void NotifyClientsOfShutdown()
        {
            var alternativeServer = GetBestAlternativeServer();
            if (alternativeServer == null)
            {
                _pluginLog?.Warning("[TURN] No alternative servers available for client migration");
                return;
            }
            
            var shutdownPacket = new byte[64];
            shutdownPacket[0] = 0xFF; // Server message marker
            shutdownPacket[1] = 0xFA; // Shutdown notification
            var urlBytes = System.Text.Encoding.UTF8.GetBytes(alternativeServer.ServerInfo.Url);
            Array.Copy(urlBytes, 0, shutdownPacket, 2, Math.Min(urlBytes.Length, 62));
            
            lock (_activeClients)
            {
                foreach (var clientEndpoint in _activeClients.Keys)
                {
                    try
                    {
                        var parts = clientEndpoint.Split(':');
                        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out var port))
                        {
                            var endpoint = new IPEndPoint(ip, port);
                            _udpServer?.Send(shutdownPacket, shutdownPacket.Length, endpoint);
                        }
                    }
                    catch { /* Ignore individual client notification failures */ }
                }
            }
            
            _pluginLog?.Info($"[TURN] Notified {_activeClients.Count} clients to migrate to: {alternativeServer.ServerInfo.Url}");
        }
        
        private TurnPeerInfo? GetBestAlternativeServer()
        {
            lock (_peerServers)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-2);
                return _peerServers.Values
                    .Where(p => p.LastSeen > cutoff && p.Load < 15)
                    .OrderBy(p => p.Load)
                    .FirstOrDefault();
            }
        }
        
        private void SendRedirect(IPEndPoint client, TurnPeerInfo betterServer)
        {
            var redirectPacket = new byte[64];
            redirectPacket[0] = 0xFF; // Redirect marker
            redirectPacket[1] = 0xFD;
            var urlBytes = System.Text.Encoding.UTF8.GetBytes(betterServer.ServerInfo.Url);
            Array.Copy(urlBytes, 0, redirectPacket, 2, Math.Min(urlBytes.Length, 62));
            
            try
            {
                _udpServer?.Send(redirectPacket, redirectPacket.Length, client);
                _pluginLog?.Info($"[TURN] Redirected client to less loaded server: {betterServer.ServerInfo.Url}");
            }
            catch { /* Ignore redirect failures */ }
        }

        public void Dispose()
        {
            try
            {
                Stop();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[TURN] Error during dispose: {ex.Message}");
            }
        }
    }
    
    public class TurnPeerInfo
    {
        public TurnServerInfo ServerInfo { get; set; } = new();
        public DateTime LastSeen { get; set; }
        public int Load { get; set; }
        public HashSet<string>? HasUser { get; set; }
    }
    }
}