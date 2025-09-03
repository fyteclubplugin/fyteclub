# Companion (Minion/Mount) Testing Guide

## Testing Setup

1. **Load the updated plugin** with the clientcache branch changes
2. **Find an area with various object types** (players, minions, mounts, NPCs)
3. **Use the debug commands** to analyze what's happening

## Debug Commands Available

### 1. Object Type Analysis
```
/fyteclub debug
```
**What it does**: Logs all objects in your current area and their types
**What to look for**: 
- `ObjectKind.Player` objects (other players)
- `ObjectKind.Companion` objects (minions/pets)
- `ObjectKind.Mount` objects (mounts)
- Any objects that implement `ICharacter` interface

### 2. Companion Mod Analysis  
```
/fyteclub companions
```
**What it does**: Specifically checks minions/mounts for mod support
**What to look for**:
- Whether `GetCurrentPlayerMods()` works on companion objects
- If appearance hashes can be generated for companions
- Any errors when trying to access mod data

### 3. Cache Statistics
```
/fyteclub cache
```
**What it does**: Shows cache usage statistics
**What to look for**:
- Component cache statistics
- Client cache usage
- Performance metrics

## What We're Testing

### Core Questions:
1. **Do minions/mounts appear as `ICharacter` objects?**
   - If yes, our existing appearance hash system should work
   - If no, we need a separate system

2. **Do mod systems (Penumbra, Glamourer, etc.) apply to companions?**
   - Check if `GetCurrentPlayerMods()` returns data for companions
   - Test if appearance changes are detected

3. **Are companions included in Mare-style polling?**
   - Check if `CheckCompanionsForChanges()` finds any objects
   - Verify that appearance hashes can be generated

### Expected Behavior:

#### If Companions Support Mods:
```
FyteClub: Found Companion object: Carbuncle (ObjectId: 12345)
FyteClub: Companion appearance hash: A1B2C3D4E5F6...
FyteClub: Companion mod data detected: 3 mods applied
```

#### If Companions Don't Support Mods:
```
FyteClub: Found Companion object: Carbuncle (ObjectId: 12345)  
FyteClub: Companion appearance hash: (empty or error)
FyteClub: No mod data available for companions
```

## Testing Scenarios

### Scenario 1: Basic Object Detection
1. Go to a busy area (Limsa Lominsa plaza)
2. Run `/fyteclub debug`
3. Check if you see various object types in logs

### Scenario 2: Companion Analysis
1. Summon a minion
2. Run `/fyteclub companions`
3. Check if your minion appears in the analysis

### Scenario 3: Mod Application Test
1. Find someone with visible mods and a minion
2. Run `/fyteclub companions`
3. Check if both player and companion show mod data

### Scenario 4: Performance Check
1. After running tests, use `/fyteclub cache`
2. Check cache statistics
3. Verify system is working efficiently

## Expected Results

### Best Case (Companions Support Mods):
- Minions/mounts implement `ICharacter`
- Mod systems recognize and apply to companions
- Our existing polling system works for all object types
- Cache efficiency benefits apply to companions

### Likely Case (Companions Partially Supported):
- Minions/mounts implement `ICharacter` but limited mod support
- Some mod systems work, others don't
- May need targeted fixes for specific mod types

### Worst Case (Companions Unsupported):
- Minions/mounts don't implement `ICharacter` properly
- Mod systems don't apply to companions
- Need separate detection system or exclude companions

## What to Report

After testing, report:
1. **Object types found** in debug output
2. **Whether companions appear** in companion analysis
3. **Any errors or warnings** in logs
4. **Performance impact** from cache statistics
5. **Specific mod systems** that work/don't work with companions

This will help us determine the next steps for companion mod support integration!
