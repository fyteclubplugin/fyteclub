# FyteClub GPose Compatibility Testing

## **GPose Testing Scenarios**

### **Scenario 1: Enter GPose with Friends Nearby**
```
1. Stand near friend with different mods
2. Enter GPose (/gpose)
3. Check if friend's mods are still applied
4. Check if new friends entering range are detected
```

### **Scenario 2: Mod Sync While in GPose**
```
1. Enter GPose alone
2. Friend approaches with new mods
3. Check if FyteClub detects friend and applies mods
4. Verify mods appear correctly in GPose
```

### **Scenario 3: GPose Zone Handling**
```
1. Check if GPose creates new zone ID
2. Verify player detection still works
3. Test distance calculations with GPose camera
```

## **Potential GPose Issues**

### **Player Detection Problems**
- GPose might hide other players from ObjectTable
- Camera position might affect distance calculations
- Zone ID might change when entering GPose

### **Mod Application Issues**
- Some mods might not update while in GPose
- Penumbra collections might not refresh in GPose
- Glamourer changes might require GPose restart

## **GPose-Specific Enhancements**

### **Enhanced Player Detection**
```csharp
private PlayerInfo[] GetNearbyPlayers()
{
    var localPlayer = ClientState.LocalPlayer;
    if (localPlayer == null) return Array.Empty<PlayerInfo>();
    
    // Special handling for GPose mode
    var isInGPose = IsInGPoseMode();
    
    var players = new List<PlayerInfo>();
    
    foreach (var obj in ObjectTable)
    {
        if (obj is not PlayerCharacter player || player.ObjectId == 0)
            continue;
            
        if (player.ObjectId == localPlayer.ObjectId)
            continue; // Skip self
            
        // In GPose, use different distance calculation
        var distance = isInGPose 
            ? CalculateGPoseDistance(localPlayer, player)
            : Vector3.Distance(localPlayer.Position, player.Position);
            
        if (distance > PROXIMITY_RANGE)
            continue;
            
        players.Add(new PlayerInfo
        {
            Name = player.Name.TextValue,
            WorldId = player.HomeWorld.Id,
            ContentId = player.ObjectId,
            Position = new float[] { player.Position.X, player.Position.Y, player.Position.Z },
            Distance = distance,
            ZoneId = GetEffectiveZoneId(), // Handle GPose zone changes
            IsInGPose = isInGPose
        });
    }
    
    return players.ToArray();
}

private bool IsInGPoseMode()
{
    // Check if player is in GPose mode
    // This might require checking game state or specific conditions
    return ClientState.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene78];
}

private float CalculateGPoseDistance(PlayerCharacter localPlayer, PlayerCharacter otherPlayer)
{
    // In GPose, camera might be far from player
    // Use actual player positions, not camera position
    return Vector3.Distance(localPlayer.Position, otherPlayer.Position);
}

private uint GetEffectiveZoneId()
{
    // GPose might change zone ID, use base zone
    return ClientState.TerritoryType;
}
```

## **Testing Checklist**

### **Basic GPose Functionality**
- [ ] Enter GPose with friend nearby - mods still applied?
- [ ] Friend enters range while in GPose - mods applied?
- [ ] Exit GPose - mods still work normally?
- [ ] Multiple friends in GPose - all mods applied?

### **Plugin-Specific Testing**
- [ ] **Penumbra**: Texture mods visible in GPose screenshots?
- [ ] **Glamourer**: Face/body changes visible in GPose?
- [ ] **Customize+**: Body scaling works in GPose?
- [ ] **SimpleHeels**: Height adjustments work in GPose?
- [ ] **Honorific**: Custom titles visible in GPose?

### **Edge Cases**
- [ ] Enter GPose in different zone than friend
- [ ] Friend enters GPose while you're in normal mode
- [ ] Both players in GPose simultaneously
- [ ] Rapid GPose enter/exit with mod changes

## **Expected Results**

### **✅ Should Work**
- Mods applied before GPose should remain visible
- Static appearance changes (Glamourer, Customize+) should work
- Height adjustments should work in GPose

### **❓ Might Need Special Handling**
- Real-time mod detection while in GPose
- Zone ID changes when entering GPose
- Camera position affecting distance calculations

### **❌ Known Limitations**
- Some Penumbra mods might require GPose restart to appear
- Dynamic mod changes might not update until GPose exit

## **Implementation Priority**

### **Phase 1: Basic Testing**
Test current FyteClub implementation in GPose scenarios

### **Phase 2: GPose Detection**
Add detection for when players are in GPose mode

### **Phase 3: GPose Optimizations**
Optimize mod sync specifically for GPose usage

### **Phase 4: GPose-Specific Features**
Add features specifically for GPose users (screenshot coordination, etc.)

## **Community Feedback Needed**

Since GPose is heavily used by:
- **Screenshot enthusiasts**
- **Roleplay communities** 
- **Content creators**

We need real-world testing from these communities to ensure FyteClub works perfectly in GPose scenarios.