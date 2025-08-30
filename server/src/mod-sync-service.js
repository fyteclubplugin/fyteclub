const path = require('path');
const fs = require('fs');

class ModSyncService {
    constructor(dataDir) {
        this.dataDir = dataDir;
        this.modsDir = path.join(dataDir, 'mods');
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
            
            // For each nearby player, check if we have their mods
            const playerModData = [];
            
            for (const player of nearbyPlayers) {
                const mods = await this.getPlayerMods(player.contentId.toString());
                if (mods) {
                    playerModData.push({
                        playerId: player.contentId.toString(),
                        playerName: player.name,
                        encryptedMods: mods,
                        distance: player.distance
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
            // Store encrypted mod data
            const modFile = path.join(this.modsDir, `${playerId}.json`);
            const modData = {
                playerId,
                encryptedMods,
                updatedAt: Date.now()
            };
            
            fs.writeFileSync(modFile, JSON.stringify(modData, null, 2));
            console.log(`💾 Updated mods for player ${playerId}`);
            
            return { success: true };
        } catch (error) {
            console.error('Error updating player mods:', error);
            throw error;
        }
    }

    async getPlayerMods(playerId) {
        try {
            const modFile = path.join(this.modsDir, `${playerId}.json`);
            
            if (!fs.existsSync(modFile)) {
                return null;
            }
            
            const data = fs.readFileSync(modFile, 'utf8');
            const modData = JSON.parse(data);
            
            // Check if data is stale (older than 24 hours)
            const maxAge = 24 * 60 * 60 * 1000; // 24 hours
            if (Date.now() - modData.updatedAt > maxAge) {
                // Clean up stale data
                fs.unlinkSync(modFile);
                return null;
            }
            
            return modData.encryptedMods;
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
                const stats = fs.statSync(filePath);
                
                if (Date.now() - stats.mtime.getTime() > maxAge) {
                    fs.unlinkSync(filePath);
                    cleaned++;
                }
            }
            
            if (cleaned > 0) {
                console.log(`🧹 Cleaned up ${cleaned} old mod files`);
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
            
            return {
                totalPlayers: modFiles.length,
                totalDataSize: totalSize,
                dataDirectory: this.dataDir
            };
        } catch (error) {
            return {
                totalPlayers: 0,
                totalDataSize: 0,
                dataDirectory: this.dataDir
            };
        }
    }
}

module.exports = ModSyncService;