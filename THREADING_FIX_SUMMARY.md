# FyteClub Threading Fix Summary

## Problem
The plugin was experiencing `System.InvalidOperationException: Not on main thread!` errors when trying to access Dalamud's ClientState from background threads.

## Root Cause
Dalamud's game state objects (like `_clientState.LocalPlayer`, `_objectTable`) can only be accessed from the main game thread. The plugin was using `Task.Run()` to create background tasks that attempted to access these objects, causing threading violations.

## Fixes Applied

### 1. OnFrameworkUpdate Method
**Before**: Direct access to LocalPlayer in background tasks
```csharp
_ = Task.Run(async () => {
    // This would fail - accessing LocalPlayer from background thread
    await UploadPlayerModsToAllServers(_clientState.LocalPlayer.Name.TextValue);
});
```

**After**: Capture data on main thread first
```csharp
var localPlayer = _clientState.LocalPlayer;
var localPlayerName = localPlayer?.Name?.TextValue;
var capturedPlayerName = localPlayerName!;
_ = Task.Run(async () => {
    // Safe - using captured data
    await UploadPlayerModsToAllServers(capturedPlayerName);
});
```

### 2. OnPlayerDetected Method
**Before**: Direct ClientState access in event handler
```csharp
var localPlayerName = _clientState.LocalPlayer?.Name?.TextValue; // Threading violation
```

**After**: Framework thread callback
```csharp
_framework.RunOnFrameworkThread(() => {
    var localPlayer = _clientState.LocalPlayer; // Safe on framework thread
    // ... rest of logic
});
```

### 3. Appearance Checking
**Before**: Background tasks accessing ObjectTable
```csharp
_ = Task.Run(async () => {
    var nearbyPlayers = _objectTable.Where(...); // Threading violation
});
```

**After**: Capture snapshots on main thread
```csharp
var nearbyPlayers = _objectTable.Where(...).Select(c => new { 
    Name = c.Name.ToString(), 
    ObjectIndex = c.ObjectIndex 
}).ToList();
_ = Task.Run(async () => {
    // Use snapshot data - no game object access
    await CheckPlayersForChanges(nearbyPlayers);
});
```

### 4. Manual Actions
**Before**: Direct LocalPlayer access in UI callbacks
```csharp
public void ForceChangeCheck() {
    var playerName = _clientState.LocalPlayer?.Name?.TextValue; // Unsafe
}
```

**After**: Framework thread safety
```csharp
public void ForceChangeCheck() {
    _framework.RunOnFrameworkThread(() => {
        var playerName = _clientState.LocalPlayer?.Name?.TextValue; // Safe
        // Start background task with captured data
    });
}
```

## Key Principles

1. **Never access game state from background threads**
   - `_clientState.LocalPlayer`
   - `_objectTable` 
   - Any `ICharacter` objects

2. **Use framework callbacks for safe access**
   - `_framework.RunOnFrameworkThread()`
   - Capture data on main thread, pass to background tasks

3. **Create data snapshots**
   - Extract primitive data (strings, numbers) from game objects
   - Pass snapshots to background tasks instead of live objects

4. **Avoid Timer callbacks accessing game state**
   - Use framework update loop instead of separate timers
   - Framework update runs on the correct thread

## Testing
After applying these fixes:
- No more "Not on main thread!" exceptions
- Plugin functionality preserved
- Background tasks work safely with captured data
- UI remains responsive

## Prevention
- Always check if code runs on background thread before accessing Dalamud APIs
- Use `Task.Run()` only for CPU-intensive work, not game state access
- Capture all needed game data on main thread before starting background tasks