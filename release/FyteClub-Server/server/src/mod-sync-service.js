const path = require('path');
const fs = require('fs');
const DeduplicationService = require('./deduplication-service');
const CacheService = require('./cache-service');

class ModSyncService {
    constructor(dataDir) {
        this.dataDir = dataDir;
        this.modsDir = path.join(dataDir, 'mods');
        this.deduplication = new DeduplicationService(dataDir);
        this.cache = new CacheService({
            ttl: 300, // 5 minutes
            enableFallback: true
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

    async updatePlayerMods(playerId, encryptedMods) {
        try {
            if (!playerId) {
                throw new Error('Player ID is required');
            }
            
            if (!encryptedMods) {
                throw new Error('Encrypted mods data is required');
            }
            
            // Store mod data with deduplication
            const contentBuffer = Buffer.from(JSON.stringify(encryptedMods));
            const storeResult = await this.deduplication.storeContent(contentBuffer);
            
            // Store metadata pointing to deduplicated content
            const modFile = path.join(this.modsDir, `${playerId}.json`);
            const modData = {
                playerId,
                contentHash: storeResult.hash,
                size: storeResult.size,
                isDuplicate: storeResult.isDuplicate,
                updatedAt: Date.now()
            };
            
            fs.writeFileSync(modFile, JSON.stringify(modData, null, 2));
            
            // Update cache
            const cacheKey = `player_mods:${playerId}`;
            await this.cache.set(cacheKey, encryptedMods, 300); // 5 minutes
            
            const dedupeMsg = storeResult.isDuplicate ? ' (deduplicated)' : ' (new content)';
            console.log(`💾 Updated mods for player ${playerId}${dedupeMsg}`);
            
            return { 
                success: true,
                isDuplicate: storeResult.isDuplicate,
                hash: storeResult.hash
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

            const modFile = path.join(this.modsDir, `${playerId}.json`);
            
            if (!fs.existsSync(modFile)) {
                return null;
            }
            
            const data = fs.readFileSync(modFile, 'utf8');
            const modData = JSON.parse(data);
            
            // Check if data is stale (older than 24 hours)
            const maxAge = 24 * 60 * 60 * 1000; // 24 hours
            if (Date.now() - modData.updatedAt > maxAge) {
                // Clean up stale data and remove content reference
                if (modData.contentHash) {
                    await this.deduplication.removeContentReference(modData.contentHash);
                }
                fs.unlinkSync(modFile);
                return null;
            }
            
            let encryptedMods = null;
            
            // Retrieve deduplicated content
            if (modData.contentHash) {
                const contentBuffer = await this.deduplication.getContent(modData.contentHash);
                if (contentBuffer) {
                    encryptedMods = JSON.parse(contentBuffer.toString());
                }
            } else {
                // Fallback for old format
                encryptedMods = modData.encryptedMods || null;
            }
            
            // Cache the result for future requests
            if (encryptedMods) {
                await this.cache.set(cacheKey, encryptedMods, 300); // 5 minutes
            }
            
            return encryptedMods;
        } catch (error) {
            console.error('Error getting player mods:', error);
            return null;
        }
    }

    async cleanupOldMods() {
        try {
            const files = fs.readdirSync(this.modsDir);
            const maxAge = 24 * 60 * 60 * 1000; // 24 hours
            let cleaned = 0;
            
            for (const file of files) {
                if (!file.endsWith('.json')) continue;
                
                const filePath = path.join(this.modsDir, file);
                const data = fs.readFileSync(filePath, 'utf8');
                const modData = JSON.parse(data);
                
                if (Date.now() - modData.updatedAt > maxAge) {
                    // Remove content reference for deduplication
                    if (modData.contentHash) {
                        await this.deduplication.removeContentReference(modData.contentHash);
                    }
                    fs.unlinkSync(filePath);
                    cleaned++;
                }
            }
            
            // Also cleanup orphaned content
            const orphansCleanedCount = await this.deduplication.cleanup();
            
            if (cleaned > 0 || orphansCleanedCount > 0) {
                console.log(`🧹 Cleaned up ${cleaned} old mod files and ${orphansCleanedCount} orphaned content files`);
            }
        } catch (error) {
            console.error('Error cleaning up old mods:', error);
        }
    }

    async getServerStats() {
        try {
            const files = fs.readdirSync(this.modsDir);
            const modFiles = files.filter(f => f.endsWith('.json'));
            
            let totalSize = 0;
            for (const file of modFiles) {
                const filePath = path.join(this.modsDir, file);
                const stats = fs.statSync(filePath);
                totalSize += stats.size;
            }
            
            // Get deduplication stats
            const dedupeStats = this.deduplication.getStats();
            
            // Get cache stats
            const cacheStats = this.cache.getStats();
            
            return {
                totalPlayers: modFiles.length,
                totalDataSize: totalSize,
                dataDirectory: this.dataDir,
                deduplication: dedupeStats,
                cache: cacheStats
            };
        } catch (error) {
            return {
                totalPlayers: 0,
                totalDataSize: 0,
                dataDirectory: this.dataDir,
                deduplication: { error: error.message },
                cache: { error: error.message }
            };
        }
    }

    async getAllRegisteredPlayers() {
        try {
            const files = fs.readdirSync(this.modsDir);
            const modFiles = files.filter(f => f.endsWith('.json'));
            
            const players = [];
            for (const file of modFiles) {
                try {
                    const filePath = path.join(this.modsDir, file);
                    const data = fs.readFileSync(filePath, 'utf8');
                    const playerData = JSON.parse(data);
                    players.push(playerData);
                } catch (error) {
                    console.error(`Error reading player file ${file}:`, error.message);
                }
            }
            
            return players;
        } catch (error) {
            console.error('Error getting registered players:', error.message);
            return [];
        }
    }
}

module.exports = ModSyncService;