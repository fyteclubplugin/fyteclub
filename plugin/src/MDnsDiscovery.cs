using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class MDnsDiscovery : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly UdpClient _udpClient;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly Dictionary<string, DiscoveredPeer> _discoveredPeers = new();
        
        private const int MDNS_PORT = 5353;
        private const string FYTECLUB_SERVICE = "_fyteclub._tcp.local";

        public event Action<DiscoveredPeer>? PeerDiscovered;
        public event Action<string>? PeerLost;

        public MDnsDiscovery(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        public Task StartDiscovery()
        {
            try
            {
                // Bind to multicast address for mDNS
                var multicastEndpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), MDNS_PORT);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, MDNS_PORT));
                _udpClient.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));

                _pluginLog.Info("mDNS discovery started");

                // Start listening for mDNS responses
                _ = Task.Run(ListenForResponses);
                
                // Start periodic announcements
                _ = Task.Run(PeriodicAnnouncement);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to start mDNS discovery: {ex.Message}");
            }
            
            return Task.CompletedTask;
        }

        public async Task AnnounceSyncshells(List<SyncshellInfo> syncshells, string? playerName = null)
        {
            try
            {
                var effectivePlayerName = playerName ?? Environment.UserName;
                _pluginLog.Info($"Announcing {syncshells.Count} syncshells as player '{effectivePlayerName}'");
                
                foreach (var syncshell in syncshells.Where(s => s.IsActive))
                {
                    var announcement = new SyncshellAnnouncement
                    {
                        SyncshellId = syncshell.Id,
                        PlayerName = effectivePlayerName,
                        Port = 7777, // Default P2P port
                        Timestamp = DateTime.UtcNow
                    };

                    var json = JsonSerializer.Serialize(announcement);
                    var data = Encoding.UTF8.GetBytes(json);
                    
                    var multicastEndpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), MDNS_PORT);
                    await _udpClient.SendAsync(data, multicastEndpoint);
                    
                    _pluginLog.Info($"Announced syncshell '{syncshell.Name}' with ID '{syncshell.Id}' as player '{effectivePlayerName}'");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to announce syncshells: {ex.Message}");
            }
        }

        private async Task ListenForResponses()
        {
            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await _udpClient.ReceiveAsync();
                    var data = Encoding.UTF8.GetString(result.Buffer);
                    
                    try
                    {
                        var announcement = JsonSerializer.Deserialize<SyncshellAnnouncement>(data);
                        if (announcement != null)
                        {
                            var peer = new DiscoveredPeer
                            {
                                SyncshellId = announcement.SyncshellId,
                                PlayerName = announcement.PlayerName,
                                IPAddress = result.RemoteEndPoint.Address.ToString(),
                                Port = announcement.Port,
                                LastSeen = DateTime.UtcNow
                            };

                            // Log all received announcements for debugging
                            _pluginLog.Info($"Received announcement from {peer.PlayerName} at {result.RemoteEndPoint.Address} for syncshell {peer.SyncshellId}");
                            
                            // Don't discover ourselves - check if this is our own announcement
                            var isLoopback = result.RemoteEndPoint.Address.Equals(IPAddress.Loopback) || 
                                           result.RemoteEndPoint.Address.Equals(IPAddress.IPv6Loopback);
                            var isLocalIP = IsLocalIPAddress(result.RemoteEndPoint.Address);
                            
                            _pluginLog.Info($"IP filtering: {result.RemoteEndPoint.Address} - Loopback: {isLoopback}, LocalIP: {isLocalIP}");
                            
                            // Only filter out our own IP, allow other computers on the network
                            var isOwnAddress = isLoopback || isLocalIP;
                            
                            if (!isOwnAddress)
                            {
                                _discoveredPeers[peer.PlayerName] = peer;
                                PeerDiscovered?.Invoke(peer);
                                _pluginLog.Info($"✅ Discovered peer: {peer.PlayerName} in syncshell {peer.SyncshellId} from {peer.IPAddress}");
                            }
                            else
                            {
                                _pluginLog.Info($"❌ Ignoring self-announcement from {peer.PlayerName} at {result.RemoteEndPoint.Address}");
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Not a FyteClub announcement, ignore
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error in mDNS listener: {ex.Message}");
            }
        }

        private async Task PeriodicAnnouncement()
        {
            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    // Announce every 30 seconds
                    await Task.Delay(30000, _cancellationTokenSource.Token);
                    
                    // Clean up old peers (not seen for 2 minutes)
                    var cutoff = DateTime.UtcNow.AddMinutes(-2);
                    var expiredPeers = _discoveredPeers.Where(kvp => kvp.Value.LastSeen < cutoff).ToList();
                    
                    foreach (var expired in expiredPeers)
                    {
                        _discoveredPeers.Remove(expired.Key);
                        PeerLost?.Invoke(expired.Key);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down
            }
        }

        public List<DiscoveredPeer> GetDiscoveredPeers() => _discoveredPeers.Values.ToList();

        private bool IsLocalIPAddress(IPAddress address)
        {
            try
            {
                // Only filter out our own IP addresses, not the entire local network
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var localIPs = host.AddressList.ToList();
                
                // Log our local IPs for debugging
                _pluginLog.Info($"Local IPs: {string.Join(", ", localIPs.Select(ip => ip.ToString()))}");
                _pluginLog.Info($"Checking if {address} matches any local IP");
                
                var isLocal = localIPs.Any(ip => ip.Equals(address));
                _pluginLog.Info($"Result: {address} is {(isLocal ? "local" : "external")}");
                
                return isLocal;
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to check if {address} is local: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _udpClient?.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }

    public class SyncshellAnnouncement
    {
        public string SyncshellId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public int Port { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class DiscoveredPeer
    {
        public string SyncshellId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public DateTime LastSeen { get; set; }
    }
}