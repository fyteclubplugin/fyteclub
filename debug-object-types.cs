using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

// Debug script to investigate available object types and minion/mount detection
// This can be integrated into the main plugin for testing

public class ObjectTypeDebugger
{
    public static void LogAllObjectTypes(IObjectTable objectTable, IPluginLog pluginLog)
    {
        pluginLog.Info("=== OBJECT TABLE ANALYSIS ===");
        
        var objects = objectTable
            .Where(obj => obj != null)
            .GroupBy(obj => obj.ObjectKind)
            .ToList();

        foreach (var group in objects)
        {
            pluginLog.Info($"{group.Key}: {group.Count()} objects");
            
            // Log first few examples of each type
            foreach (var obj in group.Take(3))
            {
                var details = $"  - {obj.Name} (Index: {obj.ObjectIndex}, Address: {obj.Address:X})";
                
                // Check if it's a character and get additional info
                if (obj is ICharacter character)
                {
                    details += $" [Character - Level {character.Level}]";
                }
                
                // Check if it's a battle npc
                if (obj is IBattleNpc battleNpc)
                {
                    details += $" [BattleNPC - DataId: {battleNpc.DataId}]";
                }
                
                pluginLog.Info(details);
            }
        }

        // Specifically look for minions and mounts
        LogMinionsAndMounts(objectTable, pluginLog);
    }

    public static void LogMinionsAndMounts(IObjectTable objectTable, IPluginLog pluginLog)
    {
        pluginLog.Info("=== MINION AND MOUNT ANALYSIS ===");

        // Look for objects that might be minions (companions)
        var possibleMinions = objectTable
            .Where(obj => obj != null && 
                   (obj.ObjectKind == ObjectKind.Companion || 
                    obj.ObjectKind == ObjectKind.Pet ||
                    obj.Name.TextValue.Contains("minion", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        pluginLog.Info($"Found {possibleMinions.Count} possible minions:");
        foreach (var minion in possibleMinions)
        {
            pluginLog.Info($"  - {minion.Name} (Kind: {minion.ObjectKind}, DataId: {(minion as IBattleNpc)?.DataId ?? 0})");
        }

        // Look for mounts
        var possibleMounts = objectTable
            .Where(obj => obj != null && 
                   (obj.ObjectKind == ObjectKind.Mount ||
                    obj.Name.TextValue.Contains("mount", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        pluginLog.Info($"Found {possibleMounts.Count} possible mounts:");
        foreach (var mount in possibleMounts)
        {
            pluginLog.Info($"  - {mount.Name} (Kind: {mount.ObjectKind}, DataId: {(mount as IBattleNpc)?.DataId ?? 0})");
        }

        // Check for ownership relationships
        var localPlayer = objectTable.FirstOrDefault(obj => obj.ObjectKind == ObjectKind.Player && 
                                                     (obj as IPlayerCharacter)?.Address == /* local player address */);
        
        if (localPlayer != null)
        {
            pluginLog.Info($"Local player: {localPlayer.Name}");
            CheckForPlayerAssociatedObjects(objectTable, localPlayer, pluginLog);
        }
    }

    public static void CheckForPlayerAssociatedObjects(IObjectTable objectTable, IGameObject player, IPluginLog pluginLog)
    {
        pluginLog.Info($"=== OBJECTS ASSOCIATED WITH {player.Name} ===");

        // Look for objects that might be owned by this player
        // This is a heuristic approach since direct ownership might not be exposed
        var nearbyObjects = objectTable
            .Where(obj => obj != null && 
                   obj != player &&
                   Vector3.Distance(obj.Position, player.Position) < 10.0f) // Within 10 units
            .Where(obj => obj.ObjectKind == ObjectKind.Companion || 
                         obj.ObjectKind == ObjectKind.Pet ||
                         obj.ObjectKind == ObjectKind.Mount)
            .ToList();

        pluginLog.Info($"Found {nearbyObjects.Count} nearby companions/pets/mounts:");
        foreach (var obj in nearbyObjects)
        {
            var distance = Vector3.Distance(obj.Position, player.Position);
            pluginLog.Info($"  - {obj.Name} (Kind: {obj.ObjectKind}, Distance: {distance:F1})");
        }
    }
}

// Available ObjectKind values (from Dalamud documentation):
/*
public enum ObjectKind : byte
{
    None = 0,
    Player = 1,
    BattleNpc = 2,
    EventNpc = 3,
    Treasure = 4,
    Aetheryte = 5,
    GatheringPoint = 6,
    EventObj = 7,
    MountType = 8,
    Companion = 9,      // This is likely minions
    Pet = 10,           // This might be summoner pets
    Housing = 11,
    Cutscene = 12,
    CardStand = 13,
    Ornament = 14,
}
*/
