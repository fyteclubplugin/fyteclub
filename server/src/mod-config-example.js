// Example demonstrating how mod configurations are handled correctly

// Scenario: 100 players with the same "Skateboard Mod" but different color configs

const skateboardModConfigs = [
    // Player 1: Red skateboard
    {
        name: "Cool Skateboard Mod",
        path: "/mods/skateboard",
        enabled: true,
        config: {
            color: "red",
            style: "classic"
        },
        settings: {
            brightness: 1.0,
            metallic: 0.5
        }
    },
    
    // Player 2: White skateboard  
    {
        name: "Cool Skateboard Mod",
        path: "/mods/skateboard", 
        enabled: true,
        config: {
            color: "white",
            style: "classic"
        },
        settings: {
            brightness: 1.2,
            metallic: 0.3
        }
    },
    
    // Player 3: Polka dot skateboard
    {
        name: "Cool Skateboard Mod",
        path: "/mods/skateboard",
        enabled: true, 
        config: {
            color: "polka_dot",
            style: "funky"
        },
        settings: {
            brightness: 0.8,
            metallic: 0.1
        }
    },
    
    // Player 4: Another red skateboard (EXACT match with Player 1)
    {
        name: "Cool Skateboard Mod",
        path: "/mods/skateboard",
        enabled: true,
        config: {
            color: "red", 
            style: "classic"
        },
        settings: {
            brightness: 1.0,
            metallic: 0.5
        }
    }
];

// What happens with our new deduplication system:

/*
Storage Results:

mod-store/
├── a1b2c3d4.json  ← Red skateboard config (Players 1 & 4 reference this)
├── e5f6g7h8.json  ← White skateboard config (Player 2 references this)  
└── i9j0k1l2.json  ← Polka dot skateboard config (Player 3 references this)

Hash Map:
{
  "a1b2c3d4": 2,  // Red config used by 2 players
  "e5f6g7h8": 1,  // White config used by 1 player  
  "i9j0k1l2": 1   // Polka dot config used by 1 player
}

Base Hash Groups:
{
  "skateboard_base_xyz": [
    { fullHash: "a1b2c3d4", variant: "red", refs: 2 },
    { fullHash: "e5f6g7h8", variant: "white", refs: 1 }, 
    { fullHash: "i9j0k1l2", variant: "polka_dot", refs: 1 }
  ]
}

Player Manifests:
player1.json: { modHashes: ["a1b2c3d4"] }  ← Gets red skateboard
player2.json: { modHashes: ["e5f6g7h8"] }  ← Gets white skateboard
player3.json: { modHashes: ["i9j0k1l2"] }  ← Gets polka dot skateboard  
player4.json: { modHashes: ["a1b2c3d4"] }  ← Gets red skateboard (same as player 1)

Result:
✅ Each player gets their correct skateboard color/config
✅ Players 1 & 4 share the same storage (true deduplication)
✅ Only 3 mod files stored instead of 4
✅ Configuration is preserved perfectly
*/

// Summary of the solution:
console.log(`
🎯 SOLUTION SUMMARY:

❌ OLD PROBLEM: 
   - Hash only mod identity, ignore config
   - All skateboards would get same hash
   - Everyone gets polka dot (or whatever was stored first)

✅ NEW SOLUTION:
   - Hash includes BOTH mod identity AND configuration
   - Red skateboard ≠ White skateboard ≠ Polka dot skateboard
   - Each config gets its own hash and storage
   - True deduplication only when mod + config are identical
   - Players get exactly what they configured

🔍 BENEFITS:
   - Perfect configuration preservation
   - Intelligent deduplication (same mod+config = shared storage)
   - Variant tracking (shows how many configs exist for each mod)
   - Space savings without losing player customizations
`);

module.exports = { skateboardModConfigs };
