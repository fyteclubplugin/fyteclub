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
        public int TotalConnections { get; private set; }
        public long BytesRelayed { get; private set; }
        
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

        public async Task<bool> StartAsync(int configuredPort = 49000)
        {
            if (IsRunning) return true;

            try
            {
                // Force close any existing socket on this port first
                await ForceCloseExistingSocket(configuredPort);
                
                _udpServer = new UdpClient();
                
                // Enable socket reuse options to handle unclean shutdowns
                _udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
                
                // Bind to the port
                _udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, configuredPort));
                
                Port = configuredPort;
                ExternalIP = await GetExternalIP();
                _cancellationTokenSource = new CancellationTokenSource();
                
                _ = Task.Run(() => RunTurnServer(_cancellationTokenSource.Token));
                _ = Task.Run(() => BroadcastLoadInfo(_cancellationTokenSource.Token));
                
                IsRunning = true;
                _pluginLog?.Info($"[TURN] Server started on {ExternalIP}:{Port} with socket reuse enabled");
                
                return true;
            }
            catch (SocketException ex)
            {
                _pluginLog?.Error($"[TURN] Cannot bind to configured port {configuredPort}: {ex.Message}");
                _pluginLog?.Error($"[TURN] Port may be in use or blocked. Check port forwarding rules.");
                return false;
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

        private async Task<int> FindAvailablePort(int preferredPort)
        {
            // Get Windows reserved port ranges and active processes to avoid conflicts
            var reservedRanges = await GetWindowsReservedPorts();
            var usedPorts = await GetUsedPorts();
            
            // Try preferred port first if available
            if (!IsPortInReservedRange(preferredPort, reservedRanges) && !usedPorts.Contains(preferredPort))
            {
                try
                {
                    using var test = new UdpClient(preferredPort);
                    _pluginLog?.Info($"[TURN] ✅ Using preferred port: {preferredPort}");
                    return preferredPort;
                }
                catch (SocketException ex)
                {
                    _pluginLog?.Warning($"[TURN] Preferred port {preferredPort} unavailable: {ex.Message}");
                }
            }
            else if (IsPortInReservedRange(preferredPort, reservedRanges))
            {
                _pluginLog?.Warning($"[TURN] Preferred port {preferredPort} is in Windows reserved range: {string.Join(", ", reservedRanges.Where(r => preferredPort >= r.start && preferredPort <= r.end).Select(r => $"{r.start}-{r.end}"))}");
            }
            else if (usedPorts.Contains(preferredPort))
            {
                _pluginLog?.Warning($"[TURN] Preferred port {preferredPort} is already in use by another process");
            }
            
            // Smart port selection: avoid common service ports and reserved ranges
            var candidatePorts = GenerateSmartPortCandidates(reservedRanges, usedPorts);
            
            foreach (var port in candidatePorts)
            {
                try
                {
                    using var test = new UdpClient(port);
                    _pluginLog?.Info($"[TURN] ✅ Found available port: {port}");
                    return port;
                }
                catch (SocketException)
                {
                    continue;
                }
            }
            
            // Last resort: try standard TURN ports if not reserved
            var fallbackPorts = new[] { 3478, 3479, 5349, 5350 }.Where(p => !IsPortInReservedRange(p, reservedRanges) && !usedPorts.Contains(p));
            foreach (var port in fallbackPorts)
            {
                try
                {
                    using var test = new UdpClient(port);
                    _pluginLog?.Warning($"[TURN] ⚠️ Using standard TURN port: {port}");
                    return port;
                }
                catch (SocketException)
                {
                    continue;
                }
            }
            
            throw new InvalidOperationException($"Cannot bind to any available port. Reserved ranges: {string.Join(", ", reservedRanges.Select(r => $"{r.start}-{r.end}"))}");
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
            
            // Handle STUN/TURN protocol packets
            if (data.Length >= 20 && IsStunPacket(data))
            {
                HandleStunTurnPacket(data, remoteEndPoint);
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
                TotalConnections++;
                BytesRelayed += data.Length;
                
                // Only enforce limits if alternative servers are available
                var hasAlternatives = HasAvailableAlternativeServers();
                
                if (hasAlternatives && ActiveConnections >= 18) // Start redirecting at 18 when alternatives exist
                {
                    var betterServer = GetBestOverflowServer();
                    if (betterServer != null)
                    {
                        SendRedirect(remoteEndPoint, betterServer);
                        _pluginLog?.Info($"[TURN] Proximity overflow: redirecting to server with {betterServer.Load} users");
                        return;
                    }
                }
                
                // Hard limit only when alternatives exist, otherwise accept all connections
                if (hasAlternatives && ActiveConnections > MAX_CONNECTIONS)
                {
                    _pluginLog?.Warning($"[TURN] Hard connection limit reached ({ActiveConnections}/{MAX_CONNECTIONS}), rejecting {clientKey}");
                    return;
                }
                
                // Log when operating beyond normal capacity without alternatives
                if (!hasAlternatives && ActiveConnections > MAX_CONNECTIONS)
                {
                    _pluginLog?.Info($"[TURN] Operating at {ActiveConnections} connections (no alternatives available)");
                }
            }
            
            // Basic rate limiting - drop large packets during high load
            if (data.Length > 1024 && ActiveConnections > MAX_CONNECTIONS / 2)
            {
                return;
            }
        }
        
        private bool IsStunPacket(byte[] data)
        {
            // STUN packets start with 0x00 or 0x01 in first two bits
            return data.Length >= 20 && (data[0] & 0xC0) == 0x00;
        }
        
        private void HandleStunTurnPacket(byte[] data, IPEndPoint remoteEndPoint)
        {
            try
            {
                if (data.Length < 20) return;
                
                var messageType = (ushort)((data[0] << 8) | data[1]);
                var messageLength = (ushort)((data[2] << 8) | data[3]);
                
                _pluginLog?.Debug($"[TURN] STUN packet from {remoteEndPoint}: type=0x{messageType:X4}, length={messageLength}");
                
                switch (messageType)
                {
                    case 0x0001: // STUN Binding Request
                        var bindingResponse = CreateStunBindingResponse(data, remoteEndPoint);
                        _udpServer?.Send(bindingResponse, bindingResponse.Length, remoteEndPoint);
                        _pluginLog?.Debug($"[TURN] Sent STUN binding response to {remoteEndPoint}");
                        break;
                        
                    case 0x0003: // TURN Allocate Request
                        var allocateResponse = CreateTurnAllocateResponse(data, remoteEndPoint);
                        _udpServer?.Send(allocateResponse, allocateResponse.Length, remoteEndPoint);
                        _pluginLog?.Debug($"[TURN] Sent TURN allocate response to {remoteEndPoint}");
                        break;
                        
                    case 0x0004: // TURN Refresh Request
                        var refreshResponse = CreateTurnRefreshResponse(data, remoteEndPoint);
                        _udpServer?.Send(refreshResponse, refreshResponse.Length, remoteEndPoint);
                        _pluginLog?.Debug($"[TURN] Sent TURN refresh response to {remoteEndPoint}");
                        break;
                        
                    default:
                        _pluginLog?.Debug($"[TURN] Unhandled STUN/TURN message type: 0x{messageType:X4}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[TURN] Error handling STUN/TURN packet: {ex.Message}");
            }
        }
        
        private byte[] CreateStunBindingResponse(byte[] request, IPEndPoint clientEndpoint)
        {
            // Create STUN binding success response with proper attributes
            var response = new byte[32];
            
            // Message type: Binding Success Response (0x0101)
            response[0] = 0x01;
            response[1] = 0x01;
            
            // Message length: 12 bytes (XOR-MAPPED-ADDRESS attribute)
            response[2] = 0x00;
            response[3] = 0x0C;
            
            // Copy transaction ID from request (bytes 4-19)
            Array.Copy(request, 4, response, 4, 16);
            
            // XOR-MAPPED-ADDRESS attribute (0x0020)
            response[20] = 0x00;
            response[21] = 0x20;
            response[22] = 0x00;
            response[23] = 0x08; // Length: 8 bytes
            
            // Family: IPv4 (0x01)
            response[24] = 0x00;
            response[25] = 0x01;
            
            // Port (XORed with magic cookie)
            var port = (ushort)clientEndpoint.Port;
            var xorPort = (ushort)(port ^ 0x2112);
            response[26] = (byte)(xorPort >> 8);
            response[27] = (byte)(xorPort & 0xFF);
            
            // IP address (XORed with magic cookie)
            var ipBytes = clientEndpoint.Address.GetAddressBytes();
            var magicCookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
            for (int i = 0; i < 4; i++)
            {
                response[28 + i] = (byte)(ipBytes[i] ^ magicCookie[i]);
            }
            
            return response;
        }
        
        private byte[] CreateTurnAllocateResponse(byte[] request, IPEndPoint clientEndpoint)
        {
            // Create basic TURN allocate success response
            var response = new byte[48];
            
            // Message type: Allocate Success Response (0x0103)
            response[0] = 0x01;
            response[1] = 0x03;
            
            // Message length: 28 bytes
            response[2] = 0x00;
            response[3] = 0x1C;
            
            // Copy transaction ID from request
            Array.Copy(request, 4, response, 4, 16);
            
            // XOR-RELAYED-ADDRESS attribute (0x0016)
            response[20] = 0x00;
            response[21] = 0x16;
            response[22] = 0x00;
            response[23] = 0x08;
            
            // Family: IPv4
            response[24] = 0x00;
            response[25] = 0x01;
            
            // Allocated port (XORed)
            var allocatedPort = (ushort)(Port + 1); // Use next port for relay
            var xorPort = (ushort)(allocatedPort ^ 0x2112);
            response[26] = (byte)(xorPort >> 8);
            response[27] = (byte)(xorPort & 0xFF);
            
            // Allocated IP (XORed with magic cookie)
            var ipBytes = IPAddress.Parse(ExternalIP).GetAddressBytes();
            var magicCookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
            for (int i = 0; i < 4; i++)
            {
                response[28 + i] = (byte)(ipBytes[i] ^ magicCookie[i]);
            }
            
            // LIFETIME attribute (0x000D)
            response[32] = 0x00;
            response[33] = 0x0D;
            response[34] = 0x00;
            response[35] = 0x04;
            
            // Lifetime: 600 seconds (10 minutes)
            response[36] = 0x00;
            response[37] = 0x00;
            response[38] = 0x02;
            response[39] = 0x58;
            
            return response;
        }
        
        private byte[] CreateTurnRefreshResponse(byte[] request, IPEndPoint clientEndpoint)
        {
            // Create basic TURN refresh success response
            var response = new byte[32];
            
            // Message type: Refresh Success Response (0x0104)
            response[0] = 0x01;
            response[1] = 0x04;
            
            // Message length: 12 bytes
            response[2] = 0x00;
            response[3] = 0x0C;
            
            // Copy transaction ID from request
            Array.Copy(request, 4, response, 4, 16);
            
            // LIFETIME attribute (0x000D)
            response[20] = 0x00;
            response[21] = 0x0D;
            response[22] = 0x00;
            response[23] = 0x04;
            
            // Lifetime: 600 seconds
            response[24] = 0x00;
            response[25] = 0x00;
            response[26] = 0x02;
            response[27] = 0x58;
            
            return response;
        }

        // Removed automatic network configuration - firewall rules should only be created with user consent
        
        public async Task ConfigureWindowsFirewall()
        {
            try
            {
                // Check if Windows Firewall rule exists for our port
                var checkCommand = $"netsh advfirewall firewall show rule name=\"FyteClub TURN {Port}\"";
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {checkCommand}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (!output.Contains("FyteClub TURN"))
                {
                    _pluginLog?.Info($"[TURN] Creating Windows Firewall rule for port {Port}");
                    
                    // Create UDP firewall rule
                    var udpCommand = $"netsh advfirewall firewall add rule name=\"FyteClub TURN {Port}\" dir=in action=allow protocol=UDP localport={Port}";
                    var udpProcess = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c {udpCommand}",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    
                    udpProcess.Start();
                    var udpOutput = await udpProcess.StandardOutput.ReadToEndAsync();
                    await udpProcess.WaitForExitAsync();
                    
                    if (udpProcess.ExitCode == 0)
                    {
                        _pluginLog?.Info($"[TURN] Windows Firewall rule created successfully (UDP only)");
                    }
                    else
                    {
                        _pluginLog?.Error($"[TURN] ❌ FIREWALL BLOCKED - WebRTC will fail!");
                        _pluginLog?.Error($"[TURN] Run as Administrator OR manually execute:");
                        _pluginLog?.Error($"[TURN] netsh advfirewall firewall add rule name=\"FyteClub TURN {Port}\" dir=in action=allow protocol=UDP localport={Port}");
                    }
                }
                else
                {
                    _pluginLog?.Info($"[TURN] Windows Firewall rule already exists for port {Port}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"[TURN] Could not configure Windows Firewall: {ex.Message}");
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

        private async Task<List<(int start, int end)>> GetWindowsReservedPorts()
        {
            var ranges = new List<(int start, int end)>();
            
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = "int ipv4 show excludedportrange protocol=udp",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var end))
                    {
                        ranges.Add((start, end));
                        _pluginLog?.Debug($"[TURN] Reserved range: {start}-{end}");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"[TURN] Could not get reserved port ranges: {ex.Message}");
            }
            
            return ranges;
        }
        
        private bool IsPortInReservedRange(int port, List<(int start, int end)> ranges)
        {
            return ranges.Any(range => port >= range.start && port <= range.end);
        }
        
        private async Task<HashSet<int>> GetUsedPorts()
        {
            var usedPorts = new HashSet<int>();
            
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "netstat",
                        Arguments = "-an",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("UDP") && line.Contains(":"))
                    {
                        var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var address = parts[1];
                            var colonIndex = address.LastIndexOf(':');
                            if (colonIndex > 0 && int.TryParse(address.Substring(colonIndex + 1), out var port))
                            {
                                usedPorts.Add(port);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"[TURN] Could not get used ports: {ex.Message}");
            }
            
            return usedPorts;
        }
        
        private List<int> GenerateSmartPortCandidates(List<(int start, int end)> reservedRanges, HashSet<int> usedPorts)
        {
            var candidates = new List<int>();
            
            // Preferred ranges for P2P applications (avoiding common services)
            var preferredRanges = new[] 
            {
                (49000, 49500),  // High ephemeral range
                (48000, 48999),  // Less common range
                (47000, 47999),  // Gaming/P2P range
                (46000, 46999)   // Alternative range
            };
            
            foreach (var (start, end) in preferredRanges)
            {
                for (int port = start; port <= end; port += 100) // Sample every 100 ports
                {
                    if (!IsPortInReservedRange(port, reservedRanges) && 
                        !usedPorts.Contains(port) && 
                        !IsCommonServicePort(port))
                    {
                        candidates.Add(port);
                        if (candidates.Count >= 20) break; // Limit candidates
                    }
                }
                if (candidates.Count >= 20) break;
            }
            
            return candidates;
        }
        
        private bool IsCommonServicePort(int port)
        {
            // Avoid well-known service ports
            var commonPorts = new HashSet<int> 
            {
                80, 443, 22, 21, 25, 53, 110, 143, 993, 995, // Web, SSH, FTP, SMTP, DNS, Mail
                3389, 5900, 5901, // RDP, VNC
                1433, 3306, 5432, // SQL Server, MySQL, PostgreSQL
                6379, 27017, // Redis, MongoDB
                8080, 8443, 9000, 9001 // Common alt-HTTP ports
            };
            
            return commonPorts.Contains(port);
        }

        private string GenerateCredential()
        {
            return Guid.NewGuid().ToString("N")[..12];
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false; // Mark as stopped immediately
            
            try
            {
                // Cancel all background tasks immediately
                _cancellationTokenSource?.Cancel();
                
                // Non-blocking UDP server disposal
                Task.Run(() => {
                    try
                    {
                        _udpServer?.Close();
                        _udpServer?.Dispose();
                    }
                    catch { }
                });
                _udpServer = null;
                
                // Best effort client notification without blocking
                Task.Run(() => {
                    try { NotifyClientsOfShutdown(); } catch { }
                });
            }
            catch { }
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
                    
                    var peersToNotify = new List<(string host, int port)>();
                    lock (_peerServers)
                    {
                        foreach (var peer in _peerServers.Values)
                        {
                            try
                            {
                                var parts = peer.ServerInfo.Url.Replace("turn:", "").Split(':');
                                if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                                {
                                    peersToNotify.Add((parts[0], port));
                                }
                            }
                            catch { /* Ignore failed broadcasts */ }
                        }
                    }
                    
                    foreach (var (host, port) in peersToNotify)
                    {
                        try
                        {
                            if (_udpServer != null) await _udpServer.SendAsync(loadPacket, loadPacket.Length, host, port);
                        }
                        catch { /* Ignore failed broadcasts */ }
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
            
            var peersToQuery = new List<(string host, int port)>();
            lock (_peerServers)
            {
                foreach (var peer in _peerServers.Values)
                {
                    try
                    {
                        var parts = peer.ServerInfo.Url.Replace("turn:", "").Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                        {
                            peersToQuery.Add((parts[0], port));
                        }
                    }
                    catch { /* Ignore failed lookups */ }
                }
            }
            
            foreach (var (host, port) in peersToQuery)
            {
                try
                {
                    if (_udpServer != null) await _udpServer.SendAsync(lookupPacket, lookupPacket.Length, host, port);
                }
                catch { /* Ignore failed lookups */ }
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
        
        private bool HasAvailableAlternativeServers()
        {
            lock (_peerServers)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-2);
                return _peerServers.Values.Any(p => p.LastSeen > cutoff && p.Load < MAX_CONNECTIONS);
            }
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

        public void ConfigureFirewall()
        {
            _ = Task.Run(ConfigureWindowsFirewall);
        }

        private async Task ForceCloseExistingSocket(int port)
        {
            try
            {
                // Kill any processes using this port
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "netstat",
                        Arguments = $"-ano | findstr :{port}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains($":{port}") && line.Contains("UDP"))
                    {
                        var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
                        {
                            try
                            {
                                var existingProcess = System.Diagnostics.Process.GetProcessById(pid);
                                if (existingProcess.ProcessName.Contains("FyteClub") || existingProcess.ProcessName.Contains("XIVLauncher"))
                                {
                                    _pluginLog?.Info($"[TURN] Killing previous FyteClub process using port {port} (PID: {pid})");
                                    existingProcess.Kill();
                                    existingProcess.WaitForExit(1000);
                                }
                            }
                            catch { /* Ignore if process already gone */ }
                        }
                    }
                }
                
                // Wait a moment for the port to be released
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _pluginLog?.Debug($"[TURN] Could not force close existing socket: {ex.Message}");
            }
        }
        
        public void Dispose()
        {
            try
            {
                IsRunning = false;
                _cancellationTokenSource?.Cancel();
                
                // Immediate socket closure with proper cleanup
                try
                {
                    _udpServer?.Client?.Shutdown(SocketShutdown.Both);
                    _udpServer?.Close();
                    _udpServer?.Dispose();
                }
                catch { }
                
                _udpServer = null;
                _cancellationTokenSource?.Dispose();
            }
            catch { }
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