# FyteClub Plugin Development Setup

## Prerequisites

### 1. Development Environment
- **Visual Studio 2022** (Community Edition is fine)
- **.NET 7 SDK** (latest version)
- **Git** for version control
- **XIVLauncher** for testing

### 2. FFXIV Setup
- **Final Fantasy XIV** (Steam or standalone)
- **XIVLauncher** installed and configured
- **Dalamud** enabled in XIVLauncher settings

## Quick Start

### 1. Clone Dalamud Plugin Template
```bash
git clone https://github.com/goatcorp/DalamudPluginProjectTemplate.git FyteClubPlugin
cd FyteClubPlugin
```

### 2. Update Project Configuration
Edit `FyteClubPlugin.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net7.0-windows</TargetFramework>
    <AssemblyName>FyteClub</AssemblyName>
    <AssemblyTitle>FyteClub</AssemblyTitle>
    <Product>FyteClub</Product>
  </PropertyGroup>
</Project>
```

### 3. Update Plugin Manifest
Edit `FyteClub.json`:
```json
{
  "Author": "FyteClub Team",
  "Name": "FyteClub",
  "Punchline": "Automatic mod sharing for FFXIV",
  "Description": "Share character mods automatically with nearby players",
  "InternalName": "FyteClub",
  "AssemblyVersion": "1.0.0.0",
  "RepoUrl": "https://github.com/chrisdemartin/fyteclub",
  "ApplicableVersion": "any",
  "DalamudApiLevel": 9
}
```

## Development Workflow

### 1. Build and Test
```bash
# Build plugin
dotnet build

# Copy to Dalamud dev plugins folder
copy bin\Debug\net7.0-windows\FyteClub.dll "%APPDATA%\XIVLauncher\devPlugins\FyteClub\"
copy FyteClub.json "%APPDATA%\XIVLauncher\devPlugins\FyteClub\"
```

### 2. Enable Development Mode
1. Open XIVLauncher
2. Go to Settings → Dalamud Settings
3. Enable "Plugin Development Mode"
4. Restart FFXIV through XIVLauncher

### 3. Load Plugin In-Game
1. Open Dalamud console (`/xldev`)
2. Type `/xlplugins` to open plugin installer
3. Go to "Dev Tools" tab
4. Click "Load Plugin" and select your DLL

## Key Development References

### Essential Dalamud Services
```csharp
// Inject these services in your plugin constructor
[PluginService] public static DalamudPluginInterface PluginInterface { get; private set; } = null!;
[PluginService] public static CommandManager CommandManager { get; private set; } = null!;
[PluginService] public static ObjectTable ObjectTable { get; private set; } = null!;
[PluginService] public static ClientState ClientState { get; private set; } = null!;
[PluginService] public static ChatGui ChatGui { get; private set; } = null!;
```

### Player Detection Pattern
```csharp
private void ScanForPlayers()
{
    var localPlayer = ClientState.LocalPlayer;
    if (localPlayer == null) return;

    foreach (var obj in ObjectTable)
    {
        if (obj is not PlayerCharacter player) continue;
        if (player.ObjectId == localPlayer.ObjectId) continue; // Skip self
        
        var distance = Vector3.Distance(localPlayer.Position, player.Position);
        if (distance > 50f) continue; // Only nearby players
        
        // Process player for mod sync
        ProcessPlayer(player);
    }
}
```

### Penumbra Integration
```csharp
// IPC with Penumbra for mod management
private void SetupPenumbraIPC()
{
    try
    {
        var penumbraEnabled = PluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
        var setCollection = PluginInterface.GetIpcSubscriber<string, string, bool>("Penumbra.SetCollection");
        
        if (penumbraEnabled.InvokeFunc())
        {
            // Penumbra is available
            ChatGui.Print("FyteClub: Penumbra integration active");
        }
    }
    catch (Exception ex)
    {
        ChatGui.PrintError($"FyteClub: Penumbra not available - {ex.Message}");
    }
}
```

## Testing Strategy

### 1. Local Testing
- Use `/fyteclub` command to verify plugin loads
- Test player detection with friends in-game
- Verify IPC communication with FyteClub client

### 2. Integration Testing
- Test with actual mod files
- Verify Penumbra integration
- Test network communication with FyteClub servers

### 3. Performance Testing
- Monitor memory usage during long sessions
- Test with many nearby players
- Verify no FPS impact during mod switching

## Common Issues and Solutions

### Plugin Won't Load
```
Error: Could not load plugin FyteClub
```
**Solution**: Check .NET version, ensure all dependencies are included

### Penumbra IPC Fails
```
Error: IPC subscriber not found
```
**Solution**: Ensure Penumbra is installed and enabled, check IPC method names

### Memory Access Violations
```
Error: AccessViolationException
```
**Solution**: Use Dalamud's safe object table, avoid direct memory access

## Distribution

### 1. Plugin Repository Submission
- Submit to official Dalamud plugin repository
- Requires code review and approval
- Automatic updates for users

### 2. Manual Distribution
- Provide DLL and JSON files
- Users manually copy to devPlugins folder
- Requires manual updates

## Next Development Steps

1. **Complete basic plugin structure**
2. **Implement player detection**
3. **Add FyteClub client IPC**
4. **Integrate Penumbra mod management**
5. **Add configuration UI**
6. **Test with real mod synchronization**

The Dalamud framework handles all the complex memory management and game compatibility - we just focus on the FyteClub-specific logic! 🎮