const path = require('path');
const fs = require('fs');
const OptimalModDeduplicationService = require('./optimal-mod-deduplication-service');
const CacheService = require('./cache-service');
const StorageMonitorService = require('./storage-monitor-service');

class ModSyncService {
    constructor(dataDir) {
        this.dataDir = dataDir;
        this.modsDir = path.join(dataDir, 'mods');
        
        // Use the new optimal deduplication system
        this.deduplication = new OptimalModDeduplicationService(dataDir);
        
        this.cache = new CacheService({
            ttl: 300, // 5 minutes
            enableFallback: true
        });
        
        // Initialize storage monitoring
        this.storageMonitor = new StorageMonitorService(dataDir, {
            maxStorageGB: 50,
            warningThresholdPercent: 95,
            cleanupThresholdPercent: 98,
            oldModThresholdDays: 30
        });
        
        this.ensureModsDirectory();
    }

    ensureModsDirectory() {
        if (!fs.existsSync(this.modsDir)) {
            fs.mkdirSync(this.modsDir, { recursive: true });
        }
    }

    async handleNearbyPlayers(playerId, nearbyPlayers, zone) {
        try {
            // Update player's current session
            const database = require('./database-service');
            
            if (!nearbyPlayers || !Array.isArray(nearbyPlayers)) {
                throw new Error('nearbyPlayers must be an array');
            }
            
            // For each nearby player, check if we have their mods
            const playerModData = [];
            
            for (const player of nearbyPlayers) {
                // Validate player object structure
                if (!player || !player.contentId) {
                    console.warn(`Invalid player object: ${JSON.stringify(player)}`);
                    continue;
                }
                
                const playerIdStr = typeof player.contentId === 'string' ? player.contentId : player.contentId.toString();
                const mods = await this.getPlayerMods(playerIdStr);
                if (mods) {
                    playerModData.push({
                        playerId: playerIdStr,
                        playerName: player.name || 'Unknown',
                        encryptedMods: mods,
                        distance: player.distance || 0
                    });
                }
            }

            return {
                success: true,
                nearbyPlayerMods: playerModData,
                timestamp: Date.now()
            };
        } catch (error) {
            console.error('Error handling nearby players:', error);
            return {
                success: false,
                error: error.message
            };
        }
    }

    async updatePlayerMods(playerId, playerModData) {
        try {
            if (!playerId) {
                throw new Error('Player ID is required');
            }
            
            if (!playerModData) {
                throw new Error('Player mod data is required');
            }

            // Parse the player mod data if it's encrypted/stringified
            let parsedModData;
            if (typeof playerModData === 'string') {
                try {
                    parsedModData = JSON.parse(playerModData);
                } catch (parseError) {
                    // Assume it's already in the right format or encrypted
                    parsedModData = { rawData: playerModData };
                }
            } else {
                parsedModData = playerModData;
            }

            // Process with optimal mod+config deduplication
            const results = await this.deduplication.processPlayerMods(playerId, parsedModData);
            
            // Update cache with the processed results
            const cacheKey = `player_mods:${playerId}`;
            await this.cache.set(cacheKey, parsedModData, 300); // 5 minutes
            
            // Calculate deduplication stats for logging
            const modDupes = results.modResults.filter(r => r.isDuplicate).length;
            const configDupes = results.configResults.filter(r => r.isDuplicate).length;
            const totalItems = results.modResults.length + results.configResults.length;
            const totalDupes = modDupes + configDupes;
            
            console.log(`💾 Updated mods for player ${playerId}:`);
            console.log(`   📦 Mods: ${results.modResults.length} (${modDupes} deduplicated)`);
            console.log(`   ⚙️ Configs: ${results.configResults.length} (${configDupes} deduplicated)`);
            console.log(`   🎯 Overall efficiency: ${totalItems > 0 ? ((totalDupes / totalItems) * 100).toFixed(1) : 0}% deduplicated`);
            
            return { 
                success: true,
                modResults: results.modResults,
                configResults: results.configResults,
                associations: results.associations,
                deduplicationStats: {
                    totalItems,
                    totalDeduplicated: totalDupes,
                    efficiencyPercent: totalItems > 0 ? ((totalDupes / totalItems) * 100).toFixed(1) : 0
                }
            };
        } catch (error) {
            console.error('Error updating player mods:', error);
            throw error;
        }
    }

    async getPlayerMods(playerId) {
        try {
            // Check cache first
            const cacheKey = `player_mods:${playerId}`;
            const cachedMods = await this.cache.get(cacheKey);
            if (cachedMods) {
                return cachedMods;
            }

            // Get packaged mods with configs from optimal deduplication service
            const packagedMods = await this.deduplication.packagePlayerMods(playerId, playerId);
            
            if (!packagedMods) {
                return null;
            }

            // Cache the result for future requests
            await this.cache.set(cacheKey, packagedMods, 300); // 5 minutes
            
            console.log(`📦 Retrieved ${packagedMods.mods.length} packaged mods for ${playerId}`);
            return packagedMods;
            
        } catch (error) {
            console.error('Error getting player mods:', error);
            return null;
        }
    }

    // New method: Get mods for another player (cross-player sharing)
    async getPlayerModsForSharing(requestingPlayerId, targetPlayerId) {
        try {
            // Check cache first
            const cacheKey = `shared_mods:${requestingPlayerId}:${targetPlayerId}`;
            const cachedMods = await this.cache.get(cacheKey);
            if (cachedMods) {
                return cachedMods;
            }

            // Package target player's mods for the requesting player
            const packagedMods = await this.deduplication.packagePlayerMods(requestingPlayerId, targetPlayerId);
            
            if (!packagedMods) {
                return null;
            }

            // Cache the result for a shorter time (cross-player data changes more frequently)
            await this.cache.set(cacheKey, packagedMods, 60); // 1 minute
            
            console.log(`🔄 ${requestingPlayerId} retrieved ${packagedMods.mods.length} mods from ${targetPlayerId}`);
            return packagedMods;
            
        } catch (error) {
            console.error('Error getting player mods for sharing:', error);
            return null;
        }
    }

    async cleanupOldMods() {
        try {
            const twentyFourHours = 24 * 60 * 60 * 1000;
            let cleanedPlayersCount = 0;
            let totalSpaceSaved = 0;

            // Get all player manifests from optimal deduplication service
            const playerManifests = await this.deduplication.getAllPlayerManifests();
            
            for (const [playerId, manifest] of Object.entries(playerManifests)) {
                const isStale = Date.now() - manifest.updatedAt > twentyFourHours;
                
                if (isStale) {
                    // Clean up player's mods and configs
                    const spaceSaved = await this.deduplication.cleanupPlayerData(playerId);
                    totalSpaceSaved += spaceSaved;
                    cleanedPlayersCount++;
                    
                    // Invalidate cache
                    const cacheKey = `player_mods:${playerId}`;
                    await this.cache.delete(cacheKey);
                    
                    console.log(`🧹 Cleaned up stale mods for ${playerId}, saved ${this.formatFileSize(spaceSaved)}`);
                }
            }

            // Run deduplication maintenance (cleanup unreferenced content)
            const additionalSpaceSaved = await this.deduplication.runMaintenance();
            totalSpaceSaved += additionalSpaceSaved;

            if (cleanedPlayersCount > 0) {
                console.log(`✨ Cleanup complete: ${cleanedPlayersCount} players, ${this.formatFileSize(totalSpaceSaved)} reclaimed`);
            } else {
                console.log('🔍 No stale mod data found during cleanup');
            }
            
            return {
                cleanedPlayers: cleanedPlayersCount,
                spaceSaved: totalSpaceSaved
            };
        } catch (error) {
            console.error('Error during cleanup:', error);
            return {
                cleanedPlayers: 0,
                spaceSaved: 0
            };
        }
    }

    async getServerStats() {
        try {
            // Get stats from optimal deduplication service
            const deduplicationStats = await this.deduplication.getStats();
            
            // Get cache stats
            const cacheStats = await this.cache.getStats();
            
            // Get mod directory size (for legacy comparison)
            const modsDirSize = this.getDirectorySize(this.modsDir);
            
            return {
                players: {
                    total: deduplicationStats.totalPlayers,
                    activeToday: deduplicationStats.activePlayers,
                    withMods: deduplicationStats.playersWithMods
                },
                mods: {
                    uniqueContent: deduplicationStats.uniqueMods,
                    totalConfigurations: deduplicationStats.totalConfigs,
                    averageModsPerPlayer: deduplicationStats.avgModsPerPlayer,
                    largestModSize: this.formatFileSize(deduplicationStats.largestModSize),
                    totalContentSize: this.formatFileSize(deduplicationStats.totalContentSize)
                },
                deduplication: {
                    spaceSavings: this.formatFileSize(deduplicationStats.spaceSaved),
                    savingsPercentage: deduplicationStats.savingsPercentage,
                    duplicatesFound: deduplicationStats.duplicatesFound,
                    mostPopularMod: deduplicationStats.mostPopularMod || 'None'
                },
                storage: {
                    contentDirectory: this.formatFileSize(deduplicationStats.contentDirSize),
                    configDirectory: this.formatFileSize(deduplicationStats.configDirSize), 
                    manifestDirectory: this.formatFileSize(deduplicationStats.manifestDirSize),
                    legacyModsDirectory: this.formatFileSize(modsDirSize),
                    totalOptimalStorage: this.formatFileSize(deduplicationStats.totalOptimalSize)
                },
                cache: {
                    hitRate: cacheStats.hitRate || 0,
                    size: cacheStats.size || 0,
                    memoryUsage: this.formatFileSize(cacheStats.memoryUsage || 0)
                },
                performance: {
                    avgUploadTime: deduplicationStats.avgUploadTime || 0,
                    avgDownloadTime: deduplicationStats.avgDownloadTime || 0,
                    lastCleanup: deduplicationStats.lastCleanup || 'Never'
                }
            };
        } catch (error) {
            console.error('Error getting server stats:', error);
            return {
                players: { total: 0, activeToday: 0, withMods: 0 },
                mods: { uniqueContent: 0, totalConfigurations: 0, averageModsPerPlayer: 0, largestModSize: '0 B', totalContentSize: '0 B' },
                deduplication: { spaceSavings: '0 B', savingsPercentage: 0, duplicatesFound: 0, mostPopularMod: 'None' },
                storage: { contentDirectory: '0 B', configDirectory: '0 B', manifestDirectory: '0 B', legacyModsDirectory: '0 B', totalOptimalStorage: '0 B' },
                cache: { hitRate: 0, size: 0, memoryUsage: '0 B' },
                performance: { avgUploadTime: 0, avgDownloadTime: 0, lastCleanup: 'Never' }
            };
        }
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

    formatFileSize(bytes) {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }

    async getAllRegisteredPlayers() {
        try {
            // Get all player manifests from optimal deduplication service
            const playerManifests = await this.deduplication.getAllPlayerManifests();
            
            const players = [];
            for (const [playerId, manifest] of Object.entries(playerManifests)) {
                players.push({
                    playerId: playerId,
                    characterName: manifest.characterName || playerId,
                    lastSeen: new Date(manifest.updatedAt).toISOString(),
                    modCount: manifest.mods.length,
                    totalSize: this.formatFileSize(manifest.totalSize || 0),
                    lastUpdate: manifest.updatedAt
                });
            }
            
            // Sort by last update time (most recent first)
            players.sort((a, b) => b.lastUpdate - a.lastUpdate);
            
            return players;
        } catch (error) {
            console.error('Error getting registered players:', error.message);
            return [];
        }
    }

    async getPlayerManifest(playerId) {
        try {
            const playerManifests = await this.deduplication.getAllPlayerManifests();
            return playerManifests[playerId] || null;
        } catch (error) {
            console.error('Error getting player manifest:', error.message);
            return null;
        }
    }
}

module.exports = ModSyncService;