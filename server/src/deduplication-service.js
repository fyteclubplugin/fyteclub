const crypto = require('crypto');
const path = require('path');
const fs = require('fs');

class DeduplicationService {
    constructor(dataDir) {
        this.dataDir = dataDir;
        this.hashMapFile = path.join(dataDir, 'content-hashes.json');
        this.contentDir = path.join(dataDir, 'content-store');
        this.ensureDirectories();
        this.loadHashMap();
    }

    ensureDirectories() {
        if (!fs.existsSync(this.contentDir)) {
            fs.mkdirSync(this.contentDir, { recursive: true });
        }
    }

    loadHashMap() {
        try {
            if (fs.existsSync(this.hashMapFile)) {
                const data = fs.readFileSync(this.hashMapFile, 'utf8');
                this.hashMap = JSON.parse(data);
            } else {
                this.hashMap = {};
            }
        } catch (error) {
            console.error('Error loading hash map:', error);
            this.hashMap = {};
        }
    }

    saveHashMap() {
        try {
            fs.writeFileSync(this.hashMapFile, JSON.stringify(this.hashMap, null, 2));
        } catch (error) {
            console.error('Error saving hash map:', error);
        }
    }

    // Calculate SHA-256 hash of content
    calculateHash(content) {
        return crypto.createHash('sha256').update(content).digest('hex');
    }

    // Store content with deduplication
    async storeContent(content) {
        const hash = this.calculateHash(content);
        const contentPath = path.join(this.contentDir, `${hash}.dat`);

        // Check if content already exists
        if (fs.existsSync(contentPath)) {
            // Content already exists, increment reference count
            this.hashMap[hash] = (this.hashMap[hash] || 0) + 1;
            this.saveHashMap();
            return {
                hash,
                isDuplicate: true,
                path: contentPath,
                size: content.length
            };
        }

        // Store new content
        try {
            fs.writeFileSync(contentPath, content);
            this.hashMap[hash] = 1;
            this.saveHashMap();
            
            console.log(`💾 Stored new content with hash: ${hash.substring(0, 8)}...`);
            return {
                hash,
                isDuplicate: false,
                path: contentPath,
                size: content.length
            };
        } catch (error) {
            console.error('Error storing content:', error);
            throw error;
        }
    }

    // Retrieve content by hash
    async getContent(hash) {
        const contentPath = path.join(this.contentDir, `${hash}.dat`);
        
        if (!fs.existsSync(contentPath)) {
            return null;
        }

        try {
            return fs.readFileSync(contentPath);
        } catch (error) {
            console.error('Error reading content:', error);
            return null;
        }
    }

    // Remove content reference (garbage collection)
    async removeContentReference(hash) {
        if (!this.hashMap[hash]) {
            return false;
        }

        this.hashMap[hash]--;
        
        // If no more references, delete the content file
        if (this.hashMap[hash] <= 0) {
            const contentPath = path.join(this.contentDir, `${hash}.dat`);
            try {
                if (fs.existsSync(contentPath)) {
                    fs.unlinkSync(contentPath);
                    console.log(`🗑️ Removed unreferenced content: ${hash.substring(0, 8)}...`);
                }
                delete this.hashMap[hash];
            } catch (error) {
                console.error('Error removing content:', error);
            }
        }

        this.saveHashMap();
        return true;
    }

    // Get storage statistics
    getStats() {
        const totalHashes = Object.keys(this.hashMap).length;
        const totalReferences = Object.values(this.hashMap).reduce((sum, count) => sum + count, 0);
        
        let totalSize = 0;
        const contentFiles = fs.readdirSync(this.contentDir).filter(f => f.endsWith('.dat'));
        
        for (const file of contentFiles) {
            try {
                const filePath = path.join(this.contentDir, file);
                const stats = fs.statSync(filePath);
                totalSize += stats.size;
            } catch (error) {
                // File might have been deleted, skip
            }
        }

        const duplicateReferences = totalReferences - totalHashes;
        const savedSpace = duplicateReferences > 0 ? 
            `Estimated ${(duplicateReferences * (totalSize / totalHashes) / (1024 * 1024)).toFixed(2)} MB saved` : 
            'No duplicates found';

        return {
            uniqueContent: totalHashes,
            totalReferences: totalReferences,
            duplicateReferences,
            totalSizeMB: (totalSize / (1024 * 1024)).toFixed(2),
            savedSpace
        };
    }

    // Cleanup orphaned content (run periodically)
    async cleanup() {
        const contentFiles = fs.readdirSync(this.contentDir).filter(f => f.endsWith('.dat'));
        let cleanedCount = 0;

        for (const file of contentFiles) {
            const hash = file.replace('.dat', '');
            
            // If hash not in map or has 0 references, remove
            if (!this.hashMap[hash] || this.hashMap[hash] <= 0) {
                try {
                    const filePath = path.join(this.contentDir, file);
                    fs.unlinkSync(filePath);
                    delete this.hashMap[hash];
                    cleanedCount++;
                } catch (error) {
                    console.error(`Error cleaning up ${file}:`, error);
                }
            }
        }

        if (cleanedCount > 0) {
            this.saveHashMap();
            console.log(`🧹 Cleaned up ${cleanedCount} orphaned content files`);
        }

        return cleanedCount;
    }
}

module.exports = DeduplicationService;
