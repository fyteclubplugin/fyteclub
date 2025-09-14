# Phone Book + P2P Implementation Plan

## Phase 1: Phone Book Server (Week 1)

### Server Modifications
**Remove existing functionality:**
- Delete mod storage endpoints (`/api/register-mods`, `/api/mods/*`)
- Remove file upload/download handlers
- Delete mod deduplication services
- Remove SQLite mod storage schema

**Add phone book functionality:**
```javascript
// New endpoints
app.post('/api/register', registerPlayer);
app.get('/api/lookup/:playerName', lookupPlayer);
app.delete('/api/unregister', unregisterPlayer);
app.get('/api/health', getHealth);

// In-memory storage
const playerRegistry = new Map();
const TTL_SECONDS = 3600; // 1 hour

function registerPlayer(req, res) {
  const { playerName, port, publicKey } = req.body;
  const ip = req.ip || req.connection.remoteAddress;
  
  playerRegistry.set(playerName, {
    ip,
    port,
    publicKey,
    lastSeen: Date.now(),
    ttl: TTL_SECONDS
  });
  
  res.json({ success: true });
}
```

**Add cleanup service:**
```javascript
setInterval(() => {
  const now = Date.now();
  for (const [playerName, data] of playerRegistry) {
    if (now - data.lastSeen > data.ttl * 1000) {
      playerRegistry.delete(playerName);
    }
  }
}, 60000); // Clean every minute
```

## Phase 2: Plugin QUIC Integration (Week 2-3)

### Add QUIC Dependencies
```xml
<PackageReference Include="Microsoft.AspNetCore.Server.Kestrel.Transport.Quic" Version="8.0.0" />
<PackageReference Include="System.Security.Cryptography.Algorithms" Version="4.3.1" />
```

### QUIC Listener Implementation
```csharp
public class QuicPeerListener : IDisposable
{
    private QuicListener _listener;
    private readonly int _port;
    private readonly X509Certificate2 _certificate;
    
    public async Task StartAsync()
    {
        _certificate = GenerateSelfSignedCertificate();
        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Any, _port),
            ApplicationProtocols = new[] { new SslApplicationProtocol("fyteclub") },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate
                }
            })
        };
        
        _listener = await QuicListener.ListenAsync(options);
        _ = Task.Run(AcceptConnectionsAsync);
    }
    
    private async Task AcceptConnectionsAsync()
    {
        while (true)
        {
            var connection = await _listener.AcceptConnectionAsync();
            _ = Task.Run(() => HandleConnectionAsync(connection));
        }
    }
}
```

### Phone Book Integration
```csharp
public class PhoneBookClient
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    
    public async Task RegisterAsync(string playerName, int port, string publicKey)
    {
        var request = new
        {
            playerName,
            port,
            publicKey
        };
        
        await _httpClient.PostAsJsonAsync($"{_serverUrl}/api/register", request);
    }
    
    public async Task<PeerInfo?> LookupAsync(string playerName)
    {
        var response = await _httpClient.GetAsync($"{_serverUrl}/api/lookup/{playerName}");
        if (!response.IsSuccessStatusCode) return null;
        
        return await response.Content.ReadFromJsonAsync<PeerInfo>();
    }
}
```

### Peer Connection Manager
```csharp
public class PeerConnectionManager
{
    private readonly ConcurrentDictionary<string, QuicConnection> _connections = new();
    private readonly PhoneBookClient _phoneBook;
    
    public async Task<QuicConnection> GetConnectionAsync(string playerName)
    {
        if (_connections.TryGetValue(playerName, out var existing))
            return existing;
            
        var peerInfo = await _phoneBook.LookupAsync(playerName);
        if (peerInfo == null) return null;
        
        var connection = await ConnectToPeerAsync(peerInfo);
        _connections.TryAdd(playerName, connection);
        return connection;
    }
    
    private async Task<QuicConnection> ConnectToPeerAsync(PeerInfo peer)
    {
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse(peer.Ip), peer.Port),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new[] { new SslApplicationProtocol("fyteclub") },
                RemoteCertificateValidationCallback = ValidatePeerCertificate
            }
        };
        
        return await QuicConnection.ConnectAsync(options);
    }
}
```

## Phase 3: P2P Data Transfer (Week 4)

### Mod Transfer Protocol
```csharp
public class P2PModTransfer
{
    public async Task SendModsAsync(QuicConnection connection, AdvancedPlayerInfo playerInfo)
    {
        var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        
        // Send header
        var header = new ModTransferHeader
        {
            PlayerName = playerInfo.PlayerName,
            ModCount = playerInfo.Mods?.Count ?? 0,
            Timestamp = DateTime.UtcNow
        };
        
        await WriteJsonAsync(stream, header);
        
        // Send each mod
        foreach (var mod in playerInfo.Mods ?? new List<string>())
        {
            var modData = await GetModDataAsync(mod);
            await WriteModAsync(stream, mod, modData);
        }
    }
    
    public async Task<AdvancedPlayerInfo> ReceiveModsAsync(QuicConnection connection)
    {
        var stream = await connection.AcceptInboundStreamAsync();
        
        var header = await ReadJsonAsync<ModTransferHeader>(stream);
        var playerInfo = new AdvancedPlayerInfo
        {
            PlayerName = header.PlayerName,
            Mods = new List<string>()
        };
        
        for (int i = 0; i < header.ModCount; i++)
        {
            var (modName, modData) = await ReadModAsync(stream);
            await StoreModAsync(modName, modData);
            playerInfo.Mods.Add(modName);
        }
        
        return playerInfo;
    }
}
```

### Integration with Existing Plugin
```csharp
// In FyteClubPlugin.cs
private QuicPeerListener _quicListener;
private PeerConnectionManager _peerManager;
private PhoneBookClient _phoneBook;

public async Task InitializeP2PAsync()
{
    _quicListener = new QuicPeerListener();
    await _quicListener.StartAsync();
    
    _phoneBook = new PhoneBookClient(_httpClient, serverAddress);
    _peerManager = new PeerConnectionManager(_phoneBook);
    
    // Register with phone book
    var localPlayer = _clientState.LocalPlayer;
    if (localPlayer != null)
    {
        await _phoneBook.RegisterAsync(
            $"{localPlayer.Name}@{localPlayer.HomeWorld.Value.Name}",
            _quicListener.Port,
            _publicKey
        );
    }
}

// Replace existing RequestPlayerMods
private async Task RequestPlayerModsP2P(string playerName)
{
    try
    {
        var connection = await _peerManager.GetConnectionAsync(playerName);
        if (connection == null)
        {
            // Fallback to HTTP
            await RequestPlayerMods(playerName);
            return;
        }
        
        var transfer = new P2PModTransfer();
        var playerInfo = await transfer.ReceiveModsAsync(connection);
        await _modSystemIntegration.ApplyPlayerMods(playerInfo, playerName);
    }
    catch (Exception ex)
    {
        _pluginLog.Warning($"P2P transfer failed for {playerName}, falling back to HTTP: {ex.Message}");
        await RequestPlayerMods(playerName);
    }
}
```

## Phase 4: Testing & Deployment

### Testing Strategy
1. **Local Testing**: Two plugin instances on same machine
2. **LAN Testing**: Multiple machines on same network
3. **Internet Testing**: Machines on different networks
4. **Load Testing**: Phone book server with simulated users

### Deployment Steps
1. Deploy phone book server alongside existing server
2. Update plugin with P2P support (feature flag)
3. Enable P2P for beta testers
4. Gradual rollout to all users
5. Monitor performance and connection success rates
6. Retire full server once stable

### Monitoring
- Phone book server: Active users, lookup rates, cleanup stats
- Plugin: P2P connection success rate, fallback usage, transfer speeds
- Performance: Memory usage, CPU usage, network bandwidth

## File Structure Changes

### New Server Files
```
server/
├── src/
│   ├── phone-book-service.js     # New phone book implementation
│   ├── player-registry.js        # In-memory player storage
│   └── cleanup-service.js        # TTL cleanup
```

### New Plugin Files
```
plugin/src/
├── P2P/
│   ├── QuicPeerListener.cs       # QUIC server
│   ├── PeerConnectionManager.cs  # Connection management
│   ├── PhoneBookClient.cs        # Server communication
│   ├── P2PModTransfer.cs         # Mod transfer protocol
│   └── PeerInfo.cs               # Data models
```

This implementation plan provides a clear path from current server-based architecture to hybrid phone book + P2P system while maintaining backward compatibility and providing fallback mechanisms.