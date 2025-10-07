# Recipe System Cleanup Plan

## Files to Remove (Unused Cache Systems)
- `plugin/src/ModSystem/ClientModCache.cs` - Legacy recipe cache
- `plugin/src/ModSystem/ModComponentCache.cs` - Component storage
- `plugin/src/Core/FyteClubPlugin.CacheManagement.cs` - Cache management

## Files to Simplify
- `plugin/src/UI/ConfigWindow.cs` - Remove cache tab complexity
- `plugin/src/Syncshells/Models/PlayerModEntry.cs` - Remove unused properties

## Keep (Working P2P System)
- `plugin/src/ModSystem/P2PModProtocol.cs` - Core P2P messaging
- `plugin/src/ModSystem/EnhancedP2PModSyncOrchestrator.cs` - P2P orchestration
- `plugin/src/ModSystem/FyteClubModIntegration.cs` - Mod application

## Result
- Simpler codebase
- Fewer warnings
- Focus on working P2P system
- Remove 3 complex, unused systems