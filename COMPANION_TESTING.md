# Companion Object Testing

## Setup

1. Load plugin with clientcache changes
2. Find area with players, minions, mounts
3. Run debug commands, check logs

## Commands

### Object Analysis
```
/fyteclub debug
```
Logs all objects in area with their ObjectKind types.

### Companion Analysis  
```
/fyteclub companions
```
Tests if minions/mounts support mod systems.

### Cache Stats
```
/fyteclub cache
```
Shows cache usage and performance data.

## Testing Questions

1. Do minions/mounts implement `ICharacter`?
2. Do mod systems work on companion objects?
3. Can we generate appearance hashes for them?

## Test Scenarios

1. **Basic**: Go to Limsa, run `/fyteclub debug`, check object types in logs
2. **Minion**: Summon minion, run `/fyteclub companions`, see if it appears
3. **Mods**: Find player with mods + minion, test if both show mod data
4. **Performance**: Run `/fyteclub cache` after testing

## Expected Outcomes

- **Full support**: Companions work like players, existing system handles them
- **Partial support**: Some mod systems work, others don't, need fixes  
- **No support**: Mod systems ignore companions, focus on players only

## Report

Log what object types appear, whether companions support mods, any errors, and which mod systems work.
