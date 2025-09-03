const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

class StorageMonitorService {
    constructor(dataDir, options = {}) {
        this.dataDir = dataDir;
        this.modsDir = path.join(dataDir, 'mods');
        this.statsFile = path.join(dataDir, 'storage-stats.json');
        
        // Configuration
        this.config = {
            maxStorageGB: options.maxStorageGB || 50,
            warningThresholdPercent: options.warningThresholdPercent || 95,
            cleanupThresholdPercent: options.cleanupThresholdPercent || 98,
            oldModThresholdDays: options.oldModThresholdDays || 30,
            duplicateCheckInterval: options.duplicateCheckInterval || 7200000, // 2 hours (reduced from 1 hour)
            statsReportInterval: options.statsReportInterval || 600000, // 10 minutes (reduced from 5 minutes)
            ...options
        };
        
        // Storage statistics
        this.stats = {
            totalSizeBytes: 0,
            totalSizeGB: 0,
            availableSpaceGB: 0,
            usedSpacePercent: 0,
            totalFiles: 0,
            totalPlayers: 0,
            oldestModDate: null,
            newestModDate: null,
            duplicatesFound: 0,
            duplicatesSavedGB: 0,
            lastCleanupDate: null,
            lastStatsUpdate: null
        };
        
        // Deduplication tracking
        this.fileHashes = new Map(); // hash -> {files: [], size: number, players: Set}
        this.duplicateFiles = new Map(); // filePath -> {hash, originalPath, isDuplicate}
        
        this.loadStats();
        this.startMonitoring();
    }

    // Platform detection and optimization
    isPlatformRaspberryPi() {
        if (process.platform !== 'linux') return false;
        
        try {
            const cpuInfo = fs.readFileSync('/proc/cpuinfo', 'utf8');
            return cpuInfo.includes('Raspberry Pi') || cpuInfo.includes('BCM');
        } catch {
            return false;
        }
    }

    getPlatformInfo() {
        const platform = process.platform;
        const arch = process.arch;
        const isRaspberryPi = this.isPlatformRaspberryPi();
        
        let platformName = platform;
        if (isRaspberryPi) {
            platformName = 'Raspberry Pi';
        } else if (platform === 'win32') {
            platformName = 'Windows';
        } else if (platform === 'darwin') {
            platformName = 'macOS';
        } else if (platform === 'linux') {
            platformName = 'Linux';
        }
        
        return {
            platform: platformName,
            architecture: arch,
            isRaspberryPi,
            nodeVersion: process.version
        };
    }

    startMonitoring() {
        const platformInfo = this.getPlatformInfo();
        console.log(`🖥️  Platform detected: ${platformInfo.platform} (${platformInfo.architecture})`);
        
        // Adjust intervals for Raspberry Pi to reduce CPU load
        let statsInterval = this.config.statsReportInterval;
        let duplicateInterval = this.config.duplicateCheckInterval;
        
        if (platformInfo.isRaspberryPi) {
            statsInterval = Math.max(statsInterval, 600000); // Minimum 10 minutes on Pi
            duplicateInterval = Math.max(duplicateInterval, 7200000); // Minimum 2 hours on Pi
            console.log(`🥧 Raspberry Pi detected: Using reduced monitoring intervals for efficiency`);
        }
        
        // Regular stats updates
        setInterval(() => {
            this.updateStats().catch(err => 
                console.error('Storage stats update failed:', err)
            );
        }, statsInterval);
        
        // Regular duplicate checks
        setInterval(() => {
            this.findAndDeduplicateFiles().catch(err => 
                console.error('Deduplication failed:', err)
            );
        }, duplicateInterval);
        
        // Initial stats update
        this.updateStats();
    }

    async updateStats() {
        try {
            console.log('📊 Updating storage statistics...');
            
            // Get disk space information
            const diskSpace = await this.getDiskSpace();
            
            // Scan mod directory
            const modStats = await this.scanModDirectory();
            
            // Update stats object
            this.stats = {
                ...this.stats,
                ...diskSpace,
                ...modStats,
                lastStatsUpdate: new Date().toISOString()
            };
            
            // Save stats to file
            this.saveStats();
            
            // Report current status
            this.reportStorageStatus();
            
            // Check if cleanup is needed
            if (this.stats.usedSpacePercent >= this.config.cleanupThresholdPercent) {
                await this.performCleanup();
            } else if (this.stats.usedSpacePercent >= this.config.warningThresholdPercent) {
                this.reportStorageWarning();
            }
            
        } catch (error) {
            console.error('Failed to update storage stats:', error);
        }
    }

    async getDiskSpace() {
        return new Promise((resolve) => {
            // For Windows, use different approach
            if (process.platform === 'win32') {
                const { execSync } = require('child_process');
                try {
                    const drive = path.parse(this.dataDir).root;
                    console.log(`🔍 Checking disk space for drive: ${drive}`);
                    
                    // Use wmic for better Windows compatibility
                    const output = execSync(`wmic logicaldisk where caption="${drive.replace('\\', '')}" get size,freespace /value`, { encoding: 'utf8' });
                    console.log(`📊 WMIC Output: ${output}`);
                    
                    let freeBytes = 0;
                    let totalBytes = 0;
                    
                    const lines = output.split('\n');
                    for (const line of lines) {
                        if (line.includes('FreeSpace=')) {
                            freeBytes = parseInt(line.split('=')[1]) || 0;
                        } else if (line.includes('Size=')) {
                            totalBytes = parseInt(line.split('=')[1]) || 0;
                        }
                    }
                    
                    console.log(`💾 Free: ${freeBytes} bytes, Total: ${totalBytes} bytes`);
                    
                    if (totalBytes === 0) {
                        // Fallback: Use dir command
                        const dirOutput = execSync(`dir /-c "${drive}"`, { encoding: 'utf8' });
                        console.log(`📁 DIR Output: ${dirOutput}`);
                        
                        // Parse dir output for free space
                        const lines = dirOutput.split('\n');
                        for (const line of lines) {
                            if (line.includes('bytes free')) {
                                const match = line.match(/([0-9,]+)\s+bytes free/);
                                if (match) {
                                    freeBytes = parseInt(match[1].replace(/,/g, ''));
                                    totalBytes = 100 * 1024 * 1024 * 1024; // Assume 100GB total as fallback
                                    break;
                                }
                            }
                        }
                    }
                    
                    const usedBytes = Math.max(0, totalBytes - freeBytes);
                    const usedPercent = totalBytes > 0 ? (usedBytes / totalBytes) * 100 : 0;
                    
                    const result = {
                        availableSpaceGB: Math.round((freeBytes / (1024 ** 3)) * 100) / 100,
                        usedSpacePercent: Math.round(usedPercent * 100) / 100
                    };
                    
                    console.log(`✅ Disk space result:`, result);
                    resolve(result);
                    
                } catch (error) {
                    console.warn('Windows disk space check failed, using fallback:', error.message);
                    resolve({ availableSpaceGB: 50, usedSpacePercent: 25 }); // Reasonable fallback
                }
            } else {
                // Linux/Mac/Raspberry Pi approach using df
                const { execSync } = require('child_process');
                try {
                    console.log(`🔍 Checking Unix disk space for: ${this.dataDir}`);
                    
                    // Use df with human-readable output, then parse
                    const output = execSync(`df -h "${this.dataDir}"`, { encoding: 'utf8' });
                    console.log(`📊 Unix df output:\n${output}`);
                    
                    const lines = output.trim().split('\n');
                    if (lines.length < 2) {
                        throw new Error('Unexpected df output format');
                    }
                    
                    // Parse the data line (skip header)
                    const dataLine = lines[1].split(/\s+/);
                    console.log(`📋 Parsed df line: ${JSON.stringify(dataLine)}`);
                    
                    // df -h output format: Filesystem Size Used Avail Use% Mounted
                    const sizeStr = dataLine[1]; // e.g., "29G"
                    const availStr = dataLine[3]; // e.g., "25G"
                    const usePercentStr = dataLine[4]; // e.g., "15%"
                    
                    // Parse size (remove G/M/K suffix and convert to GB)
                    const parseSize = (str) => {
                        const num = parseFloat(str);
                        if (str.includes('T')) return num * 1024;
                        if (str.includes('G')) return num;
                        if (str.includes('M')) return num / 1024;
                        if (str.includes('K')) return num / (1024 * 1024);
                        return num / (1024 ** 3); // Assume bytes
                    };
                    
                    const availableSpaceGB = parseSize(availStr);
                    const usedPercent = parseInt(usePercentStr.replace('%', ''));
                    
                    console.log(`💾 Unix disk stats: ${availableSpaceGB}GB available, ${usedPercent}% used`);
                    
                    const result = {
                        availableSpaceGB: Math.round(availableSpaceGB * 100) / 100,
                        usedSpacePercent: usedPercent
                    };
                    
                    console.log(`✅ Unix disk space result:`, result);
                    resolve(result);
                    
                } catch (error) {
                    console.warn(`❌ Unix disk space check failed: ${error.message}`);
                    
                    // Try Python approach for Raspberry Pi (more reliable)
                    try {
                        console.log(`🔍 Trying Python shutil approach for Raspberry Pi...`);
                        const pythonScript = `
import shutil
import json
try:
    disk_usage = shutil.disk_usage('${this.dataDir}')
    total = disk_usage.total
    free = disk_usage.free
    used = total - free
    used_percent = (used / total) * 100
    result = {
        'availableSpaceGB': round(free / (1024**3), 2),
        'usedSpacePercent': round(used_percent, 1)
    }
    print(json.dumps(result))
except Exception as e:
    print('{"error": "' + str(e) + '"}')
`;
                        
                        const output = execSync(`python3 -c "${pythonScript}"`, { encoding: 'utf8' });
                        const result = JSON.parse(output.trim());
                        
                        if (result.error) {
                            throw new Error(result.error);
                        }
                        
                        console.log(`💾 Python disk stats: ${result.availableSpaceGB}GB available, ${result.usedSpacePercent}% used`);
                        console.log(`✅ Python disk space result:`, result);
                        resolve(result);
                        
                    } catch (pythonError) {
                        console.warn(`❌ Python shutil also failed: ${pythonError.message}`);
                        
                        // Final fallback: Use simple df without -h flag
                        try {
                            console.log(`🔍 Final fallback: simple df command...`);
                            const output = execSync(`df "${this.dataDir}"`, { encoding: 'utf8' });
                            console.log(`📊 Simple df output:\n${output}`);
                            
                            const lines = output.trim().split('\n');
                            const dataLine = lines[1].split(/\s+/);
                            
                            // df output in 1K blocks: Filesystem 1K-blocks Used Available Use% Mounted
                            const totalKB = parseInt(dataLine[1]);
                            const availKB = parseInt(dataLine[3]);
                            const usePercent = parseInt(dataLine[4].replace('%', ''));
                            
                            const availableSpaceGB = Math.round((availKB / (1024 * 1024)) * 100) / 100;
                            
                            const result = {
                                availableSpaceGB: availableSpaceGB,
                                usedSpacePercent: usePercent
                            };
                            
                            console.log(`💾 Fallback disk stats: ${availableSpaceGB}GB available, ${usePercent}% used`);
                            console.log(`✅ Fallback disk space result:`, result);
                            resolve(result);
                            
                        } catch (fallbackError) {
                            console.warn(`❌ All disk space checks failed: ${fallbackError.message}`);
                            resolve({ availableSpaceGB: 25, usedSpacePercent: 30 }); // Raspberry Pi typical fallback
                        }
                    }
                }
            }
        });
    }

    async scanModDirectory() {
        if (!fs.existsSync(this.modsDir)) {
            return {
                totalSizeBytes: 0,
                totalSizeGB: 0,
                totalFiles: 0,
                totalPlayers: 0,
                oldestModDate: null,
                newestModDate: null
            };
        }
        
        let totalSize = 0;
        let totalFiles = 0;
        let oldestDate = null;
        let newestDate = null;
        const players = new Set();
        
        const scanDirectory = (dir) => {
            const items = fs.readdirSync(dir);
            
            for (const item of items) {
                const itemPath = path.join(dir, item);
                const stats = fs.statSync(itemPath);
                
                if (stats.isDirectory()) {
                    // Player directory
                    players.add(item);
                    scanDirectory(itemPath);
                } else if (stats.isFile() && item.endsWith('.json')) {
                    totalSize += stats.size;
                    totalFiles++;
                    
                    const modTime = stats.mtime;
                    if (!oldestDate || modTime < oldestDate) {
                        oldestDate = modTime;
                    }
                    if (!newestDate || modTime > newestDate) {
                        newestDate = modTime;
                    }
                }
            }
        };
        
        scanDirectory(this.modsDir);
        
        return {
            totalSizeBytes: totalSize,
            totalSizeGB: Math.round((totalSize / (1024 ** 3)) * 1000) / 1000,
            totalFiles,
            totalPlayers: players.size,
            oldestModDate: oldestDate ? oldestDate.toISOString() : null,
            newestModDate: newestDate ? newestDate.toISOString() : null
        };
    }

    async findAndDeduplicateFiles() {
        console.log('🔍 Scanning for duplicate mods...');
        
        this.fileHashes.clear();
        this.duplicateFiles.clear();
        
        if (!fs.existsSync(this.modsDir)) {
            return;
        }
        
        const hashFile = async (filePath) => {
            return new Promise((resolve, reject) => {
                const hash = crypto.createHash('sha256');
                const stream = fs.createReadStream(filePath);
                
                stream.on('data', chunk => hash.update(chunk));
                stream.on('end', () => resolve(hash.digest('hex')));
                stream.on('error', reject);
            });
        };
        
        const scanForDuplicates = async (dir, playerName = '') => {
            const items = fs.readdirSync(dir);
            
            for (const item of items) {
                const itemPath = path.join(dir, item);
                const stats = fs.statSync(itemPath);
                
                if (stats.isDirectory()) {
                    await scanForDuplicates(itemPath, item);
                } else if (stats.isFile() && item.endsWith('.json')) {
                    try {
                        const fileHash = await hashFile(itemPath);
                        
                        if (!this.fileHashes.has(fileHash)) {
                            this.fileHashes.set(fileHash, {
                                files: [],
                                size: stats.size,
                                players: new Set()
                            });
                        }
                        
                        const hashInfo = this.fileHashes.get(fileHash);
                        hashInfo.files.push(itemPath);
                        hashInfo.players.add(playerName);
                        
                        // Mark as duplicate if not the first occurrence
                        if (hashInfo.files.length > 1) {
                            this.duplicateFiles.set(itemPath, {
                                hash: fileHash,
                                originalPath: hashInfo.files[0],
                                isDuplicate: true,
                                size: stats.size
                            });
                        }
                    } catch (error) {
                        console.warn(`Failed to hash file ${itemPath}:`, error.message);
                    }
                }
            }
        };
        
        await scanForDuplicates(this.modsDir);
        
        // Calculate duplicate statistics
        let duplicatesCount = 0;
        let duplicatesSavedBytes = 0;
        
        for (const [filePath, info] of this.duplicateFiles) {
            if (info.isDuplicate) {
                duplicatesCount++;
                duplicatesSavedBytes += info.size;
            }
        }
        
        this.stats.duplicatesFound = duplicatesCount;
        this.stats.duplicatesSavedGB = Math.round((duplicatesSavedBytes / (1024 ** 3)) * 1000) / 1000;
        
        console.log(`📁 Found ${duplicatesCount} duplicate files, potentially saving ${this.stats.duplicatesSavedGB}GB`);
        
        // Report duplicates
        this.reportDuplicates();
        
        return {
            duplicatesCount,
            duplicatesSavedGB: this.stats.duplicatesSavedGB
        };
    }

    reportStorageStatus() {
        const { totalSizeGB, availableSpaceGB, usedSpacePercent, totalFiles, totalPlayers, duplicatesFound, duplicatesSavedGB } = this.stats;
        const platformInfo = this.getPlatformInfo();
        
        console.log('\\n📊 === FYTECLUB STORAGE REPORT ===');
        console.log(`�️  Platform: ${platformInfo.platform} (${platformInfo.architecture})`);
        console.log(`�💾 Used Storage: ${totalSizeGB}GB`);
        console.log(`💽 Available Space: ${availableSpaceGB}GB`);
        console.log(`📈 Disk Usage: ${usedSpacePercent}%`);
        console.log(`📄 Total Mod Files: ${totalFiles.toLocaleString()}`);
        console.log(`👥 Active Players: ${totalPlayers.toLocaleString()}`);
        console.log(`🔄 Duplicates Found: ${duplicatesFound.toLocaleString()}`);
        console.log(`💰 Space Saved by Dedup: ${duplicatesSavedGB}GB`);
        
        if (this.stats.oldestModDate) {
            const oldestAge = Math.floor((Date.now() - new Date(this.stats.oldestModDate)) / (1000 * 60 * 60 * 24));
            console.log(`⏰ Oldest Mod: ${oldestAge} days old`);
        }
        
        if (platformInfo.isRaspberryPi) {
            console.log(`🥧 Raspberry Pi optimizations: Active`);
        }
        
        console.log('=====================================\\n');
    }

    reportStorageWarning() {
        console.log('\\n⚠️  === STORAGE WARNING ===');
        console.log(`🔴 DISK USAGE: ${this.stats.usedSpacePercent}% (Warning threshold: ${this.config.warningThresholdPercent}%)`);
        console.log(`💾 Available Space: ${this.stats.availableSpaceGB}GB`);
        console.log('🧹 Consider running cleanup or increasing storage limits');
        console.log('===========================\\n');
    }

    reportDuplicates() {
        if (this.duplicateFiles.size === 0) {
            console.log('✅ No duplicate mod files found');
            return;
        }
        
        console.log('\\n🔄 === DUPLICATE MODS REPORT ===');
        
        // Group duplicates by hash
        const duplicateGroups = new Map();
        for (const [filePath, info] of this.duplicateFiles) {
            if (!duplicateGroups.has(info.hash)) {
                duplicateGroups.set(info.hash, []);
            }
            duplicateGroups.get(info.hash).push(filePath);
        }
        
        let totalShown = 0;
        for (const [hash, files] of duplicateGroups) {
            if (totalShown >= 5) { // Show only first 5 groups
                console.log(`... and ${duplicateGroups.size - totalShown} more duplicate groups`);
                break;
            }
            
            const hashInfo = this.fileHashes.get(hash);
            const sizeKB = Math.round(hashInfo.size / 1024);
            const players = Array.from(hashInfo.players);
            
            console.log(`📄 Hash: ${hash.substring(0, 12)}... (${sizeKB}KB)`);
            console.log(`   👥 Players: ${players.join(', ')}`);
            console.log(`   📁 Files: ${files.length}`);
            files.forEach((file, i) => {
                const isOriginal = i === 0 ? '(ORIGINAL)' : '(DUPLICATE)';
                const playerName = path.basename(path.dirname(file));
                console.log(`      ${i + 1}. ${playerName} ${isOriginal}`);
            });
            console.log('');
            totalShown++;
        }
        
        console.log(`💰 Total Space Wasted: ${this.stats.duplicatesSavedGB}GB`);
        console.log('=================================\\n');
    }

    async performCleanup() {
        console.log('\\n🧹 === STORAGE CLEANUP INITIATED ===');
        console.log(`🔴 Disk usage: ${this.stats.usedSpacePercent}% (Cleanup threshold: ${this.config.cleanupThresholdPercent}%)`);
        
        const oldModThreshold = new Date(Date.now() - (this.config.oldModThresholdDays * 24 * 60 * 60 * 1000));
        let cleanedFiles = 0;
        let cleanedSizeGB = 0;
        
        // Step 1: Remove duplicate files (keep original)
        console.log('🔄 Removing duplicate files...');
        for (const [filePath, info] of this.duplicateFiles) {
            if (info.isDuplicate) {
                try {
                    const stats = fs.statSync(filePath);
                    fs.unlinkSync(filePath);
                    cleanedFiles++;
                    cleanedSizeGB += stats.size / (1024 ** 3);
                    console.log(`  ❌ Removed duplicate: ${path.basename(filePath)}`);
                } catch (error) {
                    console.warn(`Failed to remove duplicate ${filePath}:`, error.message);
                }
            }
        }
        
        // Step 2: Remove old mod files
        console.log(`⏰ Removing mods older than ${this.config.oldModThresholdDays} days...`);
        const removeOldMods = (dir) => {
            if (!fs.existsSync(dir)) return;
            
            const items = fs.readdirSync(dir);
            for (const item of items) {
                const itemPath = path.join(dir, item);
                const stats = fs.statSync(itemPath);
                
                if (stats.isDirectory()) {
                    removeOldMods(itemPath);
                } else if (stats.isFile() && item.endsWith('.json')) {
                    if (stats.mtime < oldModThreshold) {
                        try {
                            fs.unlinkSync(itemPath);
                            cleanedFiles++;
                            cleanedSizeGB += stats.size / (1024 ** 3);
                            console.log(`  ⏰ Removed old mod: ${item} (${Math.floor((Date.now() - stats.mtime) / (1000 * 60 * 60 * 24))} days old)`);
                        } catch (error) {
                            console.warn(`Failed to remove old mod ${itemPath}:`, error.message);
                        }
                    }
                }
            }
        };
        
        removeOldMods(this.modsDir);
        
        // Update cleanup stats
        this.stats.lastCleanupDate = new Date().toISOString();
        this.saveStats();
        
        console.log(`✅ Cleanup complete:`);
        console.log(`   📄 Files removed: ${cleanedFiles}`);
        console.log(`   💾 Space freed: ${Math.round(cleanedSizeGB * 1000) / 1000}GB`);
        console.log('=====================================\\n');
        
        // Update stats after cleanup
        await this.updateStats();
    }

    // Smart deduplication that maintains player associations
    async createSymlinksForDuplicates() {
        if (process.platform === 'win32') {
            console.log('ℹ️  Symlink deduplication not supported on Windows');
            return;
        }
        
        console.log('🔗 Creating symlinks for duplicate files...');
        
        for (const [filePath, info] of this.duplicateFiles) {
            if (info.isDuplicate) {
                try {
                    // Remove duplicate file
                    fs.unlinkSync(filePath);
                    
                    // Create symlink to original
                    fs.symlinkSync(info.originalPath, filePath);
                    
                    console.log(`  🔗 Symlinked: ${path.basename(filePath)} -> ${path.basename(info.originalPath)}`);
                } catch (error) {
                    console.warn(`Failed to create symlink for ${filePath}:`, error.message);
                }
            }
        }
    }

    loadStats() {
        try {
            if (fs.existsSync(this.statsFile)) {
                const data = fs.readFileSync(this.statsFile, 'utf8');
                this.stats = { ...this.stats, ...JSON.parse(data) };
            }
        } catch (error) {
            console.warn('Failed to load storage stats:', error.message);
        }
    }

    saveStats() {
        try {
            fs.writeFileSync(this.statsFile, JSON.stringify(this.stats, null, 2));
        } catch (error) {
            console.warn('Failed to save storage stats:', error.message);
        }
    }

    getStats() {
        return { ...this.stats };
    }

    // API endpoints for monitoring
    getStorageReport() {
        const platformInfo = this.getPlatformInfo();
        
        return {
            platform: platformInfo,
            storage: {
                used: `${this.stats.totalSizeGB}GB`,
                available: `${this.stats.availableSpaceGB}GB`,
                usagePercent: this.stats.usedSpacePercent,
                warningThreshold: this.config.warningThresholdPercent,
                cleanupThreshold: this.config.cleanupThresholdPercent
            },
            files: {
                total: this.stats.totalFiles,
                players: this.stats.totalPlayers,
                oldestMod: this.stats.oldestModDate,
                newestMod: this.stats.newestModDate
            },
            deduplication: {
                duplicatesFound: this.stats.duplicatesFound,
                spaceSaved: `${this.stats.duplicatesSavedGB}GB`,
                lastCheck: this.stats.lastStatsUpdate
            },
            cleanup: {
                lastCleanup: this.stats.lastCleanupDate,
                oldModThreshold: `${this.config.oldModThresholdDays} days`
            }
        };
    }
}

module.exports = StorageMonitorService;
