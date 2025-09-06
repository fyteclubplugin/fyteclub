const path = require('path');
const fs = require('fs');
const crypto = require('crypto');

class ModDeduplicationService {
    constructor(dataDir) {
        this.dataDir = dataDir;
        this.modStoreDir = path.join(dataDir, 'mod-store');
        this.playerManifestsDir = path.join(dataDir, 'player-manifests');
        this.modHashMapFile = path.join(dataDir, 'mod-hashes.json');
        
        this.ensureDirectories();
        this.loadModHashMap();
    }

    ensureDirectories() {
        if (!fs.existsSync(this.modStoreDir)) {
            fs.mkdirSync(this.modStoreDir, { recursive: true });
        }
        if (!fs.existsSync(this.playerManifestsDir)) {
            fs.mkdirSync(this.playerManifestsDir, { recursive: true });
        }
    }

    loadModHashMap() {
        try {
            if (fs.existsSync(this.modHashMapFile)) {
                const data = fs.readFileSync(this.modHashMapFile, 'utf8');
                this.modHashMap = JSON.parse(data);
            } else {
                this.modHashMap = {};
            }
        } catch (error) {
            console.error('Error loading mod hash map:', error);
            this.modHashMap = {};
        }
    }

    saveModHashMap() {
        try {
            fs.writeFileSync(this.modHashMapFile, JSON.stringify(this.modHashMap, null, 2));
        } catch (error) {
            console.error('Error saving mod hash map:', error);
        }
    }

    // Generate hash for individual mod INCLUDING configuration
    generateModHash(mod) {
        // Create a comprehensive hash that includes BOTH mod identity AND configuration
        // This ensures that the same mod with different configs gets different hashes
        const modContent = {
            // Core mod identity
            name: mod.name,
            path: mod.path,
            enabled: mod.enabled,
            
            // Configuration data (this is crucial!)
            settings: mod.settings || {},
            config: mod.config || {},
            options: mod.options || {},
            variant: mod.variant || null,
            selectedOption: mod.selectedOption || null,
            
            // Penumbra-specific configuration
            penumbraConfig: mod.penumbraConfig || {},
            manipulations: mod.manipulations || [],
            
            // Any other configuration fields that affect the mod's appearance/behavior
            // Add more fields as needed based on your mod structure
        };
        
        return crypto.createHash('sha256')
            .update(JSON.stringify(modContent, Object.keys(modContent).sort())) // Sort keys for consistent hashing
            .digest('hex');
    }

    // Alternative: Generate separate hashes for mod base and full config
    generateModHashes(mod) {
        // Base mod hash (for mod identity without config)
        const baseModContent = {
            name: mod.name,
            path: mod.path,
            // Don't include config-dependent fields here
        };
        
        // Full mod hash (including all configuration)
        const fullModContent = {
            name: mod.name,
            path: mod.path,
            enabled: mod.enabled,
            settings: mod.settings || {},
            config: mod.config || {},
            options: mod.options || {},
            variant: mod.variant || null,
            selectedOption: mod.selectedOption || null,
            penumbraConfig: mod.penumbraConfig || {},
            manipulations: mod.manipulations || [],
        };
        
        return {
            baseHash: crypto.createHash('sha256').update(JSON.stringify(baseModContent, Object.keys(baseModContent).sort())).digest('hex'),
            fullHash: crypto.createHash('sha256').update(JSON.stringify(fullModContent, Object.keys(fullModContent).sort())).digest('hex')
        };
    }

    // Store individual mod with configuration-aware deduplication
    async storeIndividualMod(mod) {
        // Generate both base and full hashes
        const hashes = this.generateModHashes(mod);
        const fullHash = hashes.fullHash;
        const baseHash = hashes.baseHash;
        
        const modPath = path.join(this.modStoreDir, `${fullHash}.json`);
        
        // Check if this exact mod+config combination already exists
        if (fs.existsSync(modPath)) {
            // Exact match (same mod, same config), increment reference count
            this.modHashMap[fullHash] = (this.modHashMap[fullHash] || 0) + 1;
            this.saveModHashMap();
            
            return {
                hash: fullHash,
                baseHash: baseHash,
                isDuplicate: true,
                refs: this.modHashMap[fullHash],
                deduplicationType: 'exact' // Same mod, same config
            };
        }

        // Check if base mod exists with different config
        const sameBaseMods = this.findModsByBaseHash(baseHash);
        
        // Store new mod configuration
        try {
            const modData = {
                ...mod,
                baseHash: baseHash,
                fullHash: fullHash,
                storedAt: Date.now()
            };
            
            fs.writeFileSync(modPath, JSON.stringify(modData, null, 2));
            this.modHashMap[fullHash] = 1;
            this.saveModHashMap();
            
            const deduplicationType = sameBaseMods.length > 0 ? 'variant' : 'new';
            console.log(`💾 Stored ${deduplicationType} mod: ${mod.name} (${fullHash.substring(0, 8)}...)`);
            if (deduplicationType === 'variant') {
                console.log(`   🎨 Different config from ${sameBaseMods.length} existing variant(s)`);
            }
            
            return {
                hash: fullHash,
                baseHash: baseHash,
                isDuplicate: false,
                refs: 1,
                deduplicationType, // 'new' or 'variant'
                sameBaseModCount: sameBaseMods.length
            };
        } catch (error) {
            console.error('Error storing mod:', error);
            throw error;
        }
    }

    // Find all mods that share the same base hash (same mod, different configs)
    findModsByBaseHash(baseHash) {
        const matchingMods = [];
        
        try {
            const modFiles = fs.readdirSync(this.modStoreDir).filter(f => f.endsWith('.json'));
            
            for (const file of modFiles) {
                try {
                    const modPath = path.join(this.modStoreDir, file);
                    const modData = JSON.parse(fs.readFileSync(modPath, 'utf8'));
                    
                    if (modData.baseHash === baseHash) {
                        matchingMods.push({
                            fullHash: modData.fullHash,
                            variant: modData.variant || 'default',
                            config: modData.config || {},
                            settings: modData.settings || {}
                        });
                    }
                } catch (error) {
                    // Skip corrupted files
                }
            }
        } catch (error) {
            console.error('Error finding mods by base hash:', error);
        }
        
        return matchingMods;
    }

    // Store player's mod collection with individual mod deduplication
    async storePlayerMods(playerId, modCollection) {
        try {
            const modHashes = [];
            const modResults = [];

            // Process each mod individually
            for (const mod of modCollection.mods || []) {
                const storeResult = await this.storeIndividualMod(mod);
                modHashes.push(storeResult.hash);
                modResults.push({
                    modName: mod.name,
                    hash: storeResult.hash,
                    isDuplicate: storeResult.isDuplicate,
                    refs: storeResult.refs
                });
            }

            // Create player manifest
            const playerManifest = {
                playerId,
                playerName: modCollection.playerName,
                modHashes,
                glamourerDesign: modCollection.glamourerDesign,
                customizePlusProfile: modCollection.customizePlusProfile,
                simpleHeelsOffset: modCollection.simpleHeelsOffset,
                honorificTitle: modCollection.honorificTitle,
                totalMods: modHashes.length,
                updatedAt: Date.now()
            };

            // Save player manifest
            const manifestPath = path.join(this.playerManifestsDir, `${playerId}.json`);
            fs.writeFileSync(manifestPath, JSON.stringify(playerManifest, null, 2));

            console.log(`📋 Updated manifest for ${modCollection.playerName || playerId}: ${modHashes.length} mods`);
            
            const duplicateCount = modResults.filter(r => r.isDuplicate).length;
            const newCount = modResults.length - duplicateCount;
            
            console.log(`   📊 Breakdown: ${newCount} new mods, ${duplicateCount} deduplicated`);

            return {
                success: true,
                modHashes,
                duplicateCount,
                newCount,
                results: modResults
            };

        } catch (error) {
            console.error('Error storing player mods:', error);
            throw error;
        }
    }

    // Retrieve player's mods by rebuilding from manifest
    async getPlayerMods(playerId) {
        try {
            const manifestPath = path.join(this.playerManifestsDir, `${playerId}.json`);
            
            if (!fs.existsSync(manifestPath)) {
                return null;
            }

            const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
            
            // Check if manifest is stale (older than 24 hours)
            const maxAge = 24 * 60 * 60 * 1000; // 24 hours
            if (Date.now() - manifest.updatedAt > maxAge) {
                await this.removePlayerMods(playerId);
                return null;
            }

            // Rebuild mod collection from individual mod hashes
            const mods = [];
            for (const modHash of manifest.modHashes) {
                const modPath = path.join(this.modStoreDir, `${modHash}.json`);
                if (fs.existsSync(modPath)) {
                    const mod = JSON.parse(fs.readFileSync(modPath, 'utf8'));
                    mods.push(mod);
                } else {
                    console.warn(`⚠️ Missing mod file for hash: ${modHash.substring(0, 8)}...`);
                }
            }

            return {
                playerId: manifest.playerId,
                playerName: manifest.playerName,
                mods,
                glamourerDesign: manifest.glamourerDesign,
                customizePlusProfile: manifest.customizePlusProfile,
                simpleHeelsOffset: manifest.simpleHeelsOffset,
                honorificTitle: manifest.honorificTitle
            };

        } catch (error) {
            console.error('Error getting player mods:', error);
            return null;
        }
    }

    // Remove player and decrement mod references
    async removePlayerMods(playerId) {
        try {
            const manifestPath = path.join(this.playerManifestsDir, `${playerId}.json`);
            
            if (!fs.existsSync(manifestPath)) {
                return false;
            }

            const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

            // Decrement reference count for each mod
            for (const modHash of manifest.modHashes) {
                await this.removeModReference(modHash);
            }

            // Remove player manifest
            fs.unlinkSync(manifestPath);
            console.log(`🗑️ Removed player manifest: ${playerId}`);

            return true;
        } catch (error) {
            console.error('Error removing player mods:', error);
            return false;
        }
    }

    // Remove reference to a mod (garbage collection)
    async removeModReference(modHash) {
        if (!this.modHashMap[modHash]) {
            return false;
        }

        this.modHashMap[modHash]--;
        
        // If no more references, delete the mod file
        if (this.modHashMap[modHash] <= 0) {
            const modPath = path.join(this.modStoreDir, `${modHash}.json`);
            try {
                if (fs.existsSync(modPath)) {
                    const mod = JSON.parse(fs.readFileSync(modPath, 'utf8'));
                    fs.unlinkSync(modPath);
                    console.log(`🗑️ Removed unreferenced mod: ${mod.name} (${modHash.substring(0, 8)}...)`);
                }
                delete this.modHashMap[modHash];
            } catch (error) {
                console.error('Error removing mod:', error);
            }
        }

        this.saveModHashMap();
        return true;
    }

    // Get comprehensive statistics with configuration awareness
    getStats() {
        const totalUniqueMods = Object.keys(this.modHashMap).length;
        const totalReferences = Object.values(this.modHashMap).reduce((sum, count) => sum + count, 0);
        
        let totalModSize = 0;
        const modFiles = fs.readdirSync(this.modStoreDir).filter(f => f.endsWith('.json'));
        
        // Analyze mod variants and configurations
        const baseHashGroups = {};
        
        for (const file of modFiles) {
            try {
                const filePath = path.join(this.modStoreDir, file);
                const stats = fs.statSync(filePath);
                totalModSize += stats.size;
                
                // Group by base hash to find variants
                const modData = JSON.parse(fs.readFileSync(filePath, 'utf8'));
                if (modData.baseHash) {
                    if (!baseHashGroups[modData.baseHash]) {
                        baseHashGroups[modData.baseHash] = [];
                    }
                    baseHashGroups[modData.baseHash].push({
                        fullHash: modData.fullHash,
                        name: modData.name,
                        variant: modData.variant || 'default',
                        refs: this.modHashMap[modData.fullHash] || 0
                    });
                }
            } catch (error) {
                // File might have been deleted, skip
            }
        }

        const manifestFiles = fs.readdirSync(this.playerManifestsDir).filter(f => f.endsWith('.json'));
        
        // Calculate variant statistics
        const uniqueBaseMods = Object.keys(baseHashGroups).length;
        const modsWithVariants = Object.values(baseHashGroups).filter(group => group.length > 1).length;
        
        const duplicateReferences = totalReferences - totalUniqueMods;
        const estimatedDuplicateSize = duplicateReferences > 0 ? 
            (duplicateReferences * (totalModSize / totalUniqueMods)) : 0;

        // Find most popular mods
        const popularMods = modFiles
            .map(file => {
                try {
                    const modData = JSON.parse(fs.readFileSync(path.join(this.modStoreDir, file), 'utf8'));
                    return {
                        name: modData.name,
                        refs: this.modHashMap[modData.fullHash] || 0,
                        variant: modData.variant || 'default'
                    };
                } catch {
                    return null;
                }
            })
            .filter(mod => mod && mod.refs > 1)
            .sort((a, b) => b.refs - a.refs)
            .slice(0, 5);

        return {
            uniqueMods: totalUniqueMods,
            uniqueBaseMods: uniqueBaseMods,
            modsWithVariants: modsWithVariants,
            totalReferences: totalReferences,
            duplicateReferences,
            activePlayers: manifestFiles.length,
            totalModSizeMB: (totalModSize / (1024 * 1024)).toFixed(2),
            estimatedSavedMB: (estimatedDuplicateSize / (1024 * 1024)).toFixed(2),
            efficiency: totalReferences > 0 ? 
                ((duplicateReferences / totalReferences) * 100).toFixed(1) + '% deduplication' :
                'No duplicates found',
            popularMods: popularMods,
            variantBreakdown: Object.entries(baseHashGroups)
                .filter(([_, group]) => group.length > 1)
                .map(([baseHash, group]) => ({
                    modName: group[0].name,
                    variants: group.length,
                    totalRefs: group.reduce((sum, mod) => sum + mod.refs, 0)
                }))
                .slice(0, 5)
        };
    }

    // Cleanup orphaned mods (run periodically)
    async cleanup() {
        const modFiles = fs.readdirSync(this.modStoreDir).filter(f => f.endsWith('.json'));
        let cleanedCount = 0;

        for (const file of modFiles) {
            const modHash = file.replace('.json', '');
            
            // If hash not in map or has 0 references, remove
            if (!this.modHashMap[modHash] || this.modHashMap[modHash] <= 0) {
                try {
                    const filePath = path.join(this.modStoreDir, file);
                    const mod = JSON.parse(fs.readFileSync(filePath, 'utf8'));
                    fs.unlinkSync(filePath);
                    delete this.modHashMap[modHash];
                    cleanedCount++;
                    console.log(`🧹 Cleaned up orphaned mod: ${mod.name}`);
                } catch (error) {
                    console.error(`Error cleaning up ${file}:`, error);
                }
            }
        }

        if (cleanedCount > 0) {
            this.saveModHashMap();
            console.log(`🧹 Cleaned up ${cleanedCount} orphaned mods`);
        }

        return cleanedCount;
    }

    // Migration helper: convert old format to new format
    async migrateFromOldFormat(oldModSyncService) {
        console.log('🔄 Starting migration from old mod storage format...');
        
        try {
            const allPlayers = await oldModSyncService.getAllRegisteredPlayers();
            let migratedCount = 0;
            
            for (const player of allPlayers) {
                const oldMods = await oldModSyncService.getPlayerMods(player.playerId);
                if (oldMods && oldMods.mods) {
                    await this.storePlayerMods(player.playerId, oldMods);
                    migratedCount++;
                    console.log(`✅ Migrated ${player.playerName || player.playerId}`);
                }
            }
            
            console.log(`🎉 Migration complete! Migrated ${migratedCount} players`);
            return migratedCount;
            
        } catch (error) {
            console.error('Migration failed:', error);
            throw error;
        }
    }
}

module.exports = ModDeduplicationService;
