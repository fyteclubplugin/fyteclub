# FFXIV Plugin Development References

## Essential Projects for FyteClub Development

### 1. **Dalamud Plugin Framework** ⭐ PRIMARY REFERENCE
- **GitHub**: https://github.com/goatcorp/Dalamud
- **Purpose**: Official FFXIV plugin framework
- **Language**: C# (.NET 7)
- **Key Features**:
  - Safe memory access patterns
  - Object table for player detection
  - IPC system for plugin communication
  - Automatic game version compatibility
  - Built-in safety mechanisms

**Why Essential**: Dalamud is the gold standard for FFXIV plugins. Using it means:
- ✅ Automatic game compatibility updates
- ✅ Built-in safety and anti-cheat protection
- ✅ Large user base and community support
- ✅ Easy distribution through plugin installer

### 2. **Penumbra Mod Manager** ⭐ CORE INTEGRATION
- **GitHub**: https://github.com/xivdev/Penumbra
- **Purpose**: Texture and model modding system
- **Integration**: Dalamud plugin
- **Key Features**:
  - Real-time mod switching
  - Conflict resolution
  - Collection management
  - IPC API for external control

**Why Essential**: Penumbra handles all the complex mod injection. FyteClub just needs to:
- ✅ Tell Penumbra which mods to enable/disable
- ✅ Manage mod collections per player
- ✅ Handle mod downloads and installation

### 3. **Mare Synchronos** 📚 ARCHITECTURE REFERENCE
- **GitHub**: https://github.com/Penumbra-Sync/client (archived)
- **Purpose**: Real-time mod synchronization (discontinued)
- **Key Learnings**:
  - Player detection methods
  - Mod synchronization patterns
  - Performance optimization techniques
  - Common pitfalls and solutions

**Why Important**: Mare solved the exact problem FyteClub is solving. Study their approach for:
- ✅ Player detection algorithms
- ✅ Mod conflict resolution
- ✅ Network synchronization patterns
- ❌ Learn from their mistakes and limitations

### 4. **XIVLauncher** 🚀 DISTRIBUTION PLATFORM
- **GitHub**: https://github.com/goatcorp/XIVLauncher
- **Purpose**: Alternative FFXIV launcher with plugin support
- **Key Features**:
  - Dalamud injection
  - Plugin repository
  - Auto-updates
  - User-friendly interface

**Why Important**: XIVLauncher is how most users install Dalamud plugins:
- ✅ Built-in plugin installer
- ✅ Automatic updates
- ✅ Large user base (~500k users)
- ✅ Trusted by community

## Plugin Architecture Comparison

### Option A: Dalamud Plugin (RECOMMENDED)
```csharp
[Plugin("FyteClub")]
public class FyteClubPlugin : IDalamudPlugin
{
    // Pros:
    // ✅ Safe, tested framework
    // ✅ Automatic compatibility
    // ✅ Easy distribution
    // ✅ Penumbra integration
    
    // Cons:
    // ❌ XIVLauncher dependency
    // ❌ Plugin approval process
}
```

### Option B: Standalone DLL Injection
```cpp
// Pros:
// ✅ No dependencies
// ✅ Full control
// ✅ Custom distribution

// Cons:
// ❌ Manual memory management
// ❌ Game version compatibility
// ❌ Anti-cheat risks
// ❌ Complex DirectX hooking
```

## Similar Projects for Reference

### 1. **Glamourer** - Character Appearance
- **GitHub**: https://github.com/Ottermandias/Glamourer
- **Relevance**: Player appearance modification
- **Key Techniques**: Character data manipulation, appearance sync

### 2. **Customize+** - Advanced Character Editing
- **GitHub**: https://github.com/XIV-Tools/CustomizePlus
- **Relevance**: Real-time character modification
- **Key Techniques**: Bone scaling, model adjustments

### 3. **Brio** - Posing and Animation
- **GitHub**: https://github.com/AsgardXIV/Brio
- **Relevance**: Character manipulation and control
- **Key Techniques**: Animation overrides, pose management

### 4. **Ktisis** - Scene Creation
- **GitHub**: https://github.com/ktisis-tools/Ktisis
- **Relevance**: Multi-character scene management
- **Key Techniques**: Object spawning, character control

## Technical Implementation Plan

### Phase 1: Basic Dalamud Plugin
```csharp
// 1. Player Detection
var players = DalamudApi.ObjectTable.Where(obj => obj is PlayerCharacter);

// 2. Distance Filtering  
var nearbyPlayers = players.Where(p => Vector3.Distance(localPlayer.Position, p.Position) < 50f);

// 3. FyteClub API Communication
await fyteClubClient.GetPlayerMods(player.Name, player.HomeWorld.Id);
```

### Phase 2: Penumbra Integration
```csharp
// 1. IPC with Penumbra
var penumbra = DalamudApi.PluginInterface.GetIpcSubscriber<string, bool>("Penumbra.Enable");

// 2. Mod Collection Management
penumbra.InvokeAction($"FyteClub_{playerName}");

// 3. Dynamic Mod Switching
await SwitchToPlayerMods(playerName, modList);
```

### Phase 3: FyteClub Client Communication
```csharp
// 1. Named Pipe IPC
var pipeClient = new NamedPipeClientStream("FyteClub");

// 2. HTTP API Calls
var httpClient = new HttpClient();
var mods = await httpClient.GetAsync($"{apiEndpoint}/players/{playerId}/mods");

// 3. Real-time Updates
var signalR = new HubConnectionBuilder().WithUrl($"{apiEndpoint}/hub").Build();
```

## Development Environment Setup

### Required Tools
- **Visual Studio 2022** with .NET 7 SDK
- **XIVLauncher** for testing
- **Dalamud** development environment
- **FFXIV** (obviously)

### Development Workflow
1. **Clone Dalamud plugin template**
2. **Set up debugging** with XIVLauncher
3. **Test in-game** with development builds
4. **Submit to plugin repository** when ready

## Safety and ToS Considerations

### ✅ Safe Practices (Following Dalamud Standards)
- Use Dalamud's object table (no direct memory access)
- No gameplay automation or botting
- No server communication spoofing
- Cosmetic modifications only

### ❌ Risky Practices (Avoid These)
- Direct memory manipulation
- Game logic modification
- Automated actions
- Server-side data modification

## Next Steps for FyteClub Plugin

1. **Set up Dalamud development environment**
2. **Create basic plugin template**
3. **Implement player detection**
4. **Add FyteClub client communication**
5. **Integrate with Penumbra**
6. **Test with real mod synchronization**

The Dalamud approach is definitely the way to go - it's safer, easier, and has a proven track record! 🎮