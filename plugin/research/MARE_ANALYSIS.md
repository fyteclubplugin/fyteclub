# Mare-Style FFXIV Plugin Analysis

## Mare's Approach

Mare used client-server architecture with real-time mod synchronization:

### 1. Player Detection
- Memory scanning of FFXIV's object table
- Extract player names, world servers, unique IDs
- Zone awareness and distance filtering

### 2. Mod Application
- DirectX texture replacement hooks
- Real-time model swapping
- Conflict resolution for overlapping mods

### 3. Memory Safety
- Pattern scanning for memory addresses
- Version compatibility updates
- Anti-cheat evasion techniques

## Similar Open Source Projects

### Penumbra (Texture Modding)
- **GitHub**: https://github.com/xivdev/Penumbra
- **Approach**: DirectX hook + file redirection
- **Language**: C# (.NET)

### Dalamud (Plugin Framework)
- **GitHub**: https://github.com/goatcorp/Dalamud
- **Approach**: Process injection with .NET hosting
- **Language**: C# (.NET)

### XIVLauncher (Game Launcher)
- **GitHub**: https://github.com/goatcorp/XIVLauncher
- **Approach**: Process injection during startup
- **Integration**: Hosts Dalamud plugins

## Implementation Strategy

### Option A: Dalamud Plugin (Recommended)
```csharp
[Plugin("StallionSync")]
public class StallionSyncPlugin : IDalamudPlugin
{
    // Use Dalamud's object table for player detection
    // Integrate with Penumbra for mod management
    // IPC with Node.js client via named pipes
}
```

### Option B: Standalone DLL
```cpp
// Direct memory manipulation
// DirectX hooking for texture replacement
// Manual pattern scanning
```

## Memory Patterns (FFXIV 6.5+)

### Player Object Table
```cpp
const char* PLAYER_TABLE_PATTERN = "48 8B 05 ?? ?? ?? ?? 48 8B 0C C8";
```

### Character Structure
```cpp
struct FFXIVCharacter {
    char name[32];
    uint16_t worldId;
    uint32_t contentId;
    float position[3];
    uint32_t modelId;
};
```

### Texture Hook
```cpp
HRESULT WINAPI HookedCreateTexture2D(ID3D11Device* device, 
    const D3D11_TEXTURE2D_DESC* desc, 
    const D3D11_SUBRESOURCE_DATA* data, 
    ID3D11Texture2D** texture) {
    
    if (ShouldReplaceTexture(desc, data)) {
        return LoadModTexture(device, desc, texture);
    }
    return OriginalCreateTexture2D(device, desc, data, texture);
}
```

## Recommended Architecture

**Dalamud Plugin Approach:**
```
FFXIV → XIVLauncher → Dalamud → StallionSync Plugin → Named Pipe → Node.js Client
```

**Pros:**
- Existing safety framework
- Automatic game compatibility
- Penumbra integration
- Proven stability

**Cons:**
- XIVLauncher dependency
- Plugin approval process

## Next Steps

1. Build Dalamud plugin prototype
2. Player detection POC
3. IPC communication setup
4. Penumbra mod integration
5. Safety testing