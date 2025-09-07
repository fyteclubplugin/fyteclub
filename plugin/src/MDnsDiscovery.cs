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

                SecureLogger.LogInfo("mDNS discovery started");

                // Start listening for mDNS responses
                _ = Task.Run(ListenForResponses);
                
                // Start periodic announcements
                _ = Task.Run(PeriodicAnnouncement);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to start mDNS discovery: {0}", InputValidator.SanitizeForLog(ex.Message));
            }
            
            return Task.CompletedTask;
        }

        public async Task AnnounceSyncshells(List<SyncshellInfo> syncshells, string? playerName = null)
        {
            try
            {
                var effectivePlayerName = InputValidator.SanitizeForLog(playerName ?? Environment.UserName);
                SecureLogger.LogInfo("Announcing {0} syncshells as player '{1}'", syncshells.Count, effectivePlayerName);
                
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
                    
                    SecureLogger.LogInfo("Announced syncshell '{0}' with ID '{1}' as player '{2}'", InputValidator.SanitizeForLog(syncshell.Name), InputValidator.SanitizeForLog(syncshell.Id), effectivePlayerName);
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to announce syncshells: {0}", InputValidator.SanitizeForLog(ex.Message));
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
                            SecureLogger.LogInfo("Received announcement from {0} at {1} for syncshell {2}", InputValidator.SanitizeForLog(peer.PlayerName), InputValidator.SanitizeForLog(result.RemoteEndPoint.Address.ToString()), InputValidator.SanitizeForLog(peer.SyncshellId));
                            
                            // Don't discover ourselves - check if this is our own announcement
                            var isLoopback = result.RemoteEndPoint.Address.Equals(IPAddress.Loopback) || 
                                           result.RemoteEndPoint.Address.Equals(IPAddress.IPv6Loopback);
                            var isLocalIP = IsLocalIPAddress(result.RemoteEndPoint.Address);
                            
                            SecureLogger.LogInfo("IP filtering: {0} - Loopback: {1}, LocalIP: {2}", InputValidator.SanitizeForLog(result.RemoteEndPoint.Address.ToString()), isLoopback, isLocalIP);
                            
                            // Only filter out our own IP, allow other computers on the network
                            var isOwnAddress = isLoopback || isLocalIP;
                            
                            if (!isOwnAddress)
                            {
                                _discoveredPeers[peer.PlayerName] = peer;
                                PeerDiscovered?.Invoke(peer);
                                SecureLogger.LogInfo("Discovered peer: {0} in syncshell {1} from {2}", InputValidator.SanitizeForLog(peer.PlayerName), InputValidator.SanitizeForLog(peer.SyncshellId), InputValidator.SanitizeForLog(peer.IPAddress));
                            }
                            else
                            {
                                SecureLogger.LogInfo("Ignoring self-announcement from {0} at {1}", InputValidator.SanitizeForLog(peer.PlayerName), InputValidator.SanitizeForLog(result.RemoteEndPoint.Address.ToString()));
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
                SecureLogger.LogError("Error in mDNS listener: {0}", InputValidator.SanitizeForLog(ex.Message));
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
                SecureLogger.LogInfo("Local IPs: {0}", InputValidator.SanitizeForLog(string.Join(", ", localIPs.Select(ip => ip.ToString()))));
                SecureLogger.LogInfo("Checking if {0} matches any local IP", InputValidator.SanitizeForLog(address.ToString()));
                
                var isLocal = localIPs.Any(ip => ip.Equals(address));
                SecureLogger.LogInfo("Result: {0} is {1}", InputValidator.SanitizeForLog(address.ToString()), isLocal ? "local" : "external");
                
                return isLocal;
            }
            catch (Exception ex)
            {
                SecureLogger.LogWarning("Failed to check if {0} is local: {1}", InputValidator.SanitizeForLog(address.ToString()), InputValidator.SanitizeForLog(ex.Message));
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