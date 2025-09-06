const path = require('path');
const fs = require('fs');
const crypto = require('crypto');

class OptimalModDeduplicationService {
    constructor(dataDir) {
        this.dataDir = dataDir;
        
        // Storage directories
        this.modContentDir = path.join(dataDir, 'mod-content');      // Raw mod files
        this.configContentDir = path.join(dataDir, 'config-content'); // Configurations
        this.playerManifestsDir = path.join(dataDir, 'player-manifests'); // User associations
        
        // Hash tracking files
        this.modHashMapFile = path.join(dataDir, 'mod-hashes.json');
        this.configHashMapFile = path.join(dataDir, 'config-hashes.json');
        
        this.ensureDirectories();
        this.loadHashMaps();
    }

    ensureDirectories() {
        [this.modContentDir, this.configContentDir, this.playerManifestsDir].forEach(dir => {
            if (!fs.existsSync(dir)) {
                fs.mkdirSync(dir, { recursive: true });
            }
        });
    }

    loadHashMaps() {
        try {
            this.modHashMap = fs.existsSync(this.modHashMapFile) 
                ? JSON.parse(fs.readFileSync(this.modHashMapFile, 'utf8'))
                : {};
            this.configHashMap = fs.existsSync(this.configHashMapFile)
                ? JSON.parse(fs.readFileSync(this.configHashMapFile, 'utf8'))
                : {};
        } catch (error) {
            console.error('Error loading hash maps:', error);
            this.modHashMap = {};
            this.configHashMap = {};
        }
    }

    saveHashMaps() {
        try {
            fs.writeFileSync(this.modHashMapFile, JSON.stringify(this.modHashMap, null, 2));
            fs.writeFileSync(this.configHashMapFile, JSON.stringify(this.configHashMap, null, 2));
        } catch (error) {
            console.error('Error saving hash maps:', error);
        }
    }

    // Step 1: Store mod content (without configuration)
    async storeModContent(modPath, modData) {
        // Hash only the mod file content, not config
        const modHash = crypto.createHash('sha256')
            .update(modData)
            .digest('hex');
        
        const modStorePath = path.join(this.modContentDir, `${modHash}.mod`);
        
        if (fs.existsSync(modStorePath)) {
            // Mod already exists, increment reference
            this.modHashMap[modHash] = (this.modHashMap[modHash] || 0) + 1;
            console.log(`📦 Mod deduplicated: ${modPath} (${this.modHashMap[modHash]} refs)`);
            
            return {
                modHash,
                isDuplicate: true,
                refs: this.modHashMap[modHash]
            };
        }

        // Store new mod
        fs.writeFileSync(modStorePath, modData);
        this.modHashMap[modHash] = 1;
        
        console.log(`💾 New mod stored: ${modPath}`);
        return {
            modHash,
            isDuplicate: false,
            refs: 1
        };
    }

    // Step 2: Store configuration (separate from mod)
    async storeConfiguration(configType, configData) {
        // Hash the configuration data
        const configHash = crypto.createHash('sha256')
            .update(JSON.stringify(configData))
            .digest('hex');
        
        const configStorePath = path.join(this.configContentDir, `${configHash}.json`);
        
        if (fs.existsSync(configStorePath)) {
            // Config already exists, increment reference
            this.configHashMap[configHash] = (this.configHashMap[configHash] || 0) + 1;
            console.log(`⚙️ Config deduplicated: ${configType} (${this.configHashMap[configHash]} refs)`);
            
            return {
                configHash,
                isDuplicate: true,
                refs: this.configHashMap[configHash]
            };
        }

        // Store new config
        const configEntry = {
            type: configType,
            data: configData,
            storedAt: Date.now()
        };
        
        fs.writeFileSync(configStorePath, JSON.stringify(configEntry, null, 2));
        this.configHashMap[configHash] = 1;
        
        console.log(`💾 New config stored: ${configType}`);
        return {
            configHash,
            isDuplicate: false,
            refs: 1
        };
    }

    // Step 3: Create association between user, mod, and config
    async storePlayerModAssociations(playerId, modAssociations) {
        const playerManifest = {
            playerId,
            associations: modAssociations, // Array of {modHash, configHashes: {...}}
            updatedAt: Date.now()
        };

        const manifestPath = path.join(this.playerManifestsDir, `${playerId}.json`);
        fs.writeFileSync(manifestPath, JSON.stringify(playerManifest, null, 2));
        
        console.log(`📋 Updated associations for ${playerId}: ${modAssociations.length} mod-config pairs`);
    }

    // Process complete player mod data (your exact workflow)
    async processPlayerMods(playerId, playerModData) {
        const results = {
            modResults: [],
            configResults: [],
            associations: []
        };

        try {
            // Process Penumbra mods (individual mod files)
            for (const modPath of playerModData.mods || []) {
                // In real implementation, modData would come from the actual mod file
                const modData = Buffer.from(`mod-content-for-${modPath}`); // Placeholder
                
                const modResult = await this.storeModContent(modPath, modData);
                results.modResults.push({
                    modPath,
                    ...modResult
                });

                // Create base association (mod without specific config)
                results.associations.push({
                    modHash: modResult.modHash,
                    modPath: modPath,
                    configHashes: {}
                });
            }

            // Process configurations separately
            const configTypes = ['glamourer', 'customizePlus', 'simpleHeels', 'honorific'];
            
            for (const configType of configTypes) {
                const configData = playerModData[configType];
                if (configData) {
                    const configResult = await this.storeConfiguration(configType, configData);
                    results.configResults.push({
                        configType,
                        ...configResult
                    });

                    // Add config to ALL mod associations (configs apply globally to character)
                    results.associations.forEach(assoc => {
                        assoc.configHashes[configType] = configResult.configHash;
                    });
                }
            }

            // Store the complete associations
            await this.storePlayerModAssociations(playerId, results.associations);
            this.saveHashMaps();

            console.log(`✅ Processed ${playerId}: ${results.modResults.length} mods, ${results.configResults.length} configs`);
            
            return results;

        } catch (error) {
            console.error('Error processing player mods:', error);
            throw error;
        }
    }

    // Step 7: Package mod + correct config for delivery
    async packagePlayerMods(requestingPlayerId, targetPlayerId) {
        try {
            const manifestPath = path.join(this.playerManifestsDir, `${targetPlayerId}.json`);
            
            if (!fs.existsSync(manifestPath)) {
                return null;
            }

            const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
            const packagedMods = [];
            
            // Collect global configs (same for all mods of a player)
            let glamourerDesign = null;
            let customizePlusProfile = null;
            let simpleHeelsOffset = null;
            let honorificTitle = null;

            for (const association of manifest.associations) {
                // Get mod content
                const modContentPath = path.join(this.modContentDir, `${association.modHash}.mod`);
                const modContent = fs.readFileSync(modContentPath);

                // Get all configs for this mod (and extract global ones)
                const configs = {};
                for (const [configType, configHash] of Object.entries(association.configHashes)) {
                    const configPath = path.join(this.configContentDir, `${configHash}.json`);
                    const configEntry = JSON.parse(fs.readFileSync(configPath, 'utf8'));
                    configs[configType] = configEntry.data;
                    
                    // Extract global configs (first occurrence wins)
                    if (configType === 'glamourer' && !glamourerDesign) {
                        glamourerDesign = configEntry.data;
                    } else if (configType === 'customizePlus' && !customizePlusProfile) {
                        customizePlusProfile = configEntry.data;
                    } else if (configType === 'simpleHeels' && !simpleHeelsOffset) {
                        simpleHeelsOffset = configEntry.data;
                    } else if (configType === 'honorific' && !honorificTitle) {
                        honorificTitle = configEntry.data;
                    }
                }

                packagedMods.push({
                    modPath: association.modPath,
                    modContent: modContent.toString('base64'), // Encoded for transfer
                    configs: configs
                });
            }

            console.log(`📦 Packaged ${packagedMods.length} mods with configs for ${requestingPlayerId}`);
            
            // Return in the format expected by server.js
            return {
                mods: packagedMods,
                glamourerDesign: glamourerDesign,
                customizePlusProfile: customizePlusProfile,
                simpleHeelsOffset: simpleHeelsOffset,
                honorificTitle: honorificTitle,
                targetPlayerId,
                packagedAt: Date.now(),
                lastModified: manifest.updatedAt || Date.now() // Use manifest timestamp for conditional requests
            };

        } catch (error) {
            console.error('Error packaging player mods:', error);
            return null;
        }
    }

    // Get comprehensive statistics
    getOptimizationStats() {
        const totalUniqueMods = Object.keys(this.modHashMap).length;
        const totalModReferences = Object.values(this.modHashMap).reduce((sum, count) => sum + count, 0);
        
        const totalUniqueConfigs = Object.keys(this.configHashMap).length;
        const totalConfigReferences = Object.values(this.configHashMap).reduce((sum, count) => sum + count, 0);

        // Calculate storage savings
        let totalModSize = 0;
        let totalConfigSize = 0;
        
        try {
            const modFiles = fs.readdirSync(this.modContentDir);
            const configFiles = fs.readdirSync(this.configContentDir);
            
            modFiles.forEach(file => {
                const stats = fs.statSync(path.join(this.modContentDir, file));
                totalModSize += stats.size;
            });
            
            configFiles.forEach(file => {
                const stats = fs.statSync(path.join(this.configContentDir, file));
                totalConfigSize += stats.size;
            });
        } catch (error) {
            console.error('Error calculating storage stats:', error);
        }

        const modDeduplicationRatio = totalModReferences > 0 ? 
            ((totalModReferences - totalUniqueMods) / totalModReferences * 100).toFixed(1) : 0;
        
        const configDeduplicationRatio = totalConfigReferences > 0 ? 
            ((totalConfigReferences - totalUniqueConfigs) / totalConfigReferences * 100).toFixed(1) : 0;

        const estimatedSavedSpace = (
            (totalModReferences - totalUniqueMods) * (totalModSize / Math.max(totalUniqueMods, 1)) +
            (totalConfigReferences - totalUniqueConfigs) * (totalConfigSize / Math.max(totalUniqueConfigs, 1))
        ) / (1024 * 1024);

        return {
            mods: {
                unique: totalUniqueMods,
                totalReferences: totalModReferences,
                deduplicationRatio: `${modDeduplicationRatio}%`,
                totalSizeMB: (totalModSize / (1024 * 1024)).toFixed(2)
            },
            configs: {
                unique: totalUniqueConfigs,
                totalReferences: totalConfigReferences,
                deduplicationRatio: `${configDeduplicationRatio}%`,
                totalSizeMB: (totalConfigSize / (1024 * 1024)).toFixed(2)
            },
            overall: {
                estimatedSavedMB: estimatedSavedSpace.toFixed(2),
                storageEfficiency: `${((modDeduplicationRatio + configDeduplicationRatio) / 2).toFixed(1)}%`
            }
        };
    }

    // Cleanup specific player data
    async cleanupPlayerData(playerId) {
        try {
            const manifestPath = path.join(this.playerManifestsDir, `${playerId}.json`);
            
            if (!fs.existsSync(manifestPath)) {
                return 0; // No data to clean
            }
            
            const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
            let spaceSaved = 0;
            
            // Decrement reference counts for all associated mods and configs
            for (const association of manifest.associations || []) {
                // Decrement mod reference
                if (this.modHashMap[association.modHash]) {
                    this.modHashMap[association.modHash]--;
                    if (this.modHashMap[association.modHash] <= 0) {
                        const modPath = path.join(this.modContentDir, `${association.modHash}.mod`);
                        if (fs.existsSync(modPath)) {
                            const stats = fs.statSync(modPath);
                            spaceSaved += stats.size;
                            fs.unlinkSync(modPath);
                        }
                        delete this.modHashMap[association.modHash];
                    }
                }
                
                // Decrement config references
                for (const [configType, configHash] of Object.entries(association.configHashes || {})) {
                    if (this.configHashMap[configHash]) {
                        this.configHashMap[configHash]--;
                        if (this.configHashMap[configHash] <= 0) {
                            const configPath = path.join(this.configContentDir, `${configHash}.json`);
                            if (fs.existsSync(configPath)) {
                                const stats = fs.statSync(configPath);
                                spaceSaved += stats.size;
                                fs.unlinkSync(configPath);
                            }
                            delete this.configHashMap[configHash];
                        }
                    }
                }
            }
            
            // Remove player manifest
            const manifestStats = fs.statSync(manifestPath);
            spaceSaved += manifestStats.size;
            fs.unlinkSync(manifestPath);
            
            this.saveHashMaps();
            return spaceSaved;
            
        } catch (error) {
            console.error(`Error cleaning up player data for ${playerId}:`, error);
            return 0;
        }
    }

    // Run maintenance operations
    async runMaintenance() {
        const result = await this.cleanup();
        return 0; // Space saved already calculated in cleanup
    }

    // Get comprehensive statistics
    async getStats() {
        const totalUniqueMods = Object.keys(this.modHashMap).length;
        const totalModReferences = Object.values(this.modHashMap).reduce((sum, count) => sum + count, 0);
        
        const totalUniqueConfigs = Object.keys(this.configHashMap).length;
        const totalConfigReferences = Object.values(this.configHashMap).reduce((sum, count) => sum + count, 0);

        // Get player stats
        const manifests = await this.getAllPlayerManifests();
        const totalPlayers = Object.keys(manifests).length;
        
        const now = Date.now();
        const oneDayAgo = now - (24 * 60 * 60 * 1000);
        const activePlayers = Object.values(manifests).filter(m => m.updatedAt > oneDayAgo).length;
        
        const playersWithMods = Object.values(manifests).filter(m => m.associations && m.associations.length > 0).length;
        
        const avgModsPerPlayer = totalPlayers > 0 ? Math.round(totalModReferences / totalPlayers) : 0;

        // Calculate storage sizes
        let totalModSize = 0;
        let totalConfigSize = 0;
        let largestModSize = 0;
        
        try {
            const modFiles = fs.readdirSync(this.modContentDir);
            const configFiles = fs.readdirSync(this.configContentDir);
            
            modFiles.forEach(file => {
                const stats = fs.statSync(path.join(this.modContentDir, file));
                totalModSize += stats.size;
                if (stats.size > largestModSize) {
                    largestModSize = stats.size;
                }
            });
            
            configFiles.forEach(file => {
                const stats = fs.statSync(path.join(this.configContentDir, file));
                totalConfigSize += stats.size;
            });
        } catch (error) {
            console.error('Error calculating storage stats:', error);
        }

        // Calculate space savings
        const estimatedOriginalSize = (totalModReferences * (totalModSize / Math.max(totalUniqueMods, 1))) + 
                                     (totalConfigReferences * (totalConfigSize / Math.max(totalUniqueConfigs, 1)));
        const actualSize = totalModSize + totalConfigSize;
        const spaceSaved = Math.max(0, estimatedOriginalSize - actualSize);
        const savingsPercentage = estimatedOriginalSize > 0 ? ((spaceSaved / estimatedOriginalSize) * 100) : 0;

        return {
            totalPlayers,
            activePlayers,
            playersWithMods,
            uniqueMods: totalUniqueMods,
            totalConfigs: totalUniqueConfigs,
            avgModsPerPlayer,
            largestModSize,
            totalContentSize: actualSize,
            spaceSaved,
            savingsPercentage: Math.round(savingsPercentage),
            duplicatesFound: (totalModReferences - totalUniqueMods) + (totalConfigReferences - totalUniqueConfigs),
            mostPopularMod: this.getMostPopularMod(),
            contentDirSize: totalModSize,
            configDirSize: totalConfigSize,
            manifestDirSize: this.getDirectorySize(this.playerManifestsDir),
            totalOptimalSize: actualSize,
            avgUploadTime: 0, // Would need to track this
            avgDownloadTime: 0, // Would need to track this
            lastCleanup: new Date().toISOString()
        };
    }

    getMostPopularMod() {
        let mostPopular = null;
        let maxRefs = 0;
        
        for (const [hash, refs] of Object.entries(this.modHashMap)) {
            if (refs > maxRefs) {
                maxRefs = refs;
                mostPopular = hash;
            }
        }
        
        return mostPopular ? `${mostPopular.substring(0, 8)}... (${maxRefs} refs)` : 'None';
    }

    getDirectorySize(dirPath) {
        try {
            if (!fs.existsSync(dirPath)) {
                return 0;
            }
            
            let totalSize = 0;
            const files = fs.readdirSync(dirPath);
            
            for (const file of files) {
                const filePath = path.join(dirPath, file);
                const stats = fs.statSync(filePath);
                
                if (stats.isDirectory()) {
                    totalSize += this.getDirectorySize(filePath);
                } else {
                    totalSize += stats.size;
                }
            }
            
            return totalSize;
        } catch (error) {
            console.error('Error calculating directory size:', error);
            return 0;
        }
    }

    // Get all player manifests 
    async getAllPlayerManifests() {
        try {
            const manifests = {};
            const manifestFiles = fs.readdirSync(this.playerManifestsDir);
            
            for (const file of manifestFiles) {
                if (file.endsWith('.json')) {
                    const playerId = file.replace('.json', '');
                    const manifestPath = path.join(this.playerManifestsDir, file);
                    const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
                    manifests[playerId] = manifest;
                }
            }
            
            return manifests;
        } catch (error) {
            console.error('Error reading player manifests:', error);
            return {};
        }
    }

    // Cleanup unused content
    async cleanup() {
        let cleanedMods = 0;
        let cleanedConfigs = 0;

        // Clean up orphaned mods
        const modFiles = fs.readdirSync(this.modContentDir);
        for (const file of modFiles) {
            const modHash = file.replace('.mod', '');
            if (!this.modHashMap[modHash] || this.modHashMap[modHash] <= 0) {
                fs.unlinkSync(path.join(this.modContentDir, file));
                delete this.modHashMap[modHash];
                cleanedMods++;
            }
        }

        // Clean up orphaned configs
        const configFiles = fs.readdirSync(this.configContentDir);
        for (const file of configFiles) {
            const configHash = file.replace('.json', '');
            if (!this.configHashMap[configHash] || this.configHashMap[configHash] <= 0) {
                fs.unlinkSync(path.join(this.configContentDir, file));
                delete this.configHashMap[configHash];
                cleanedConfigs++;
            }
        }

        if (cleanedMods > 0 || cleanedConfigs > 0) {
            this.saveHashMaps();
            console.log(`🧹 Cleanup complete: ${cleanedMods} mods, ${cleanedConfigs} configs removed`);
        }

        return { cleanedMods, cleanedConfigs };
    }
}

module.exports = OptimalModDeduplicationService;
