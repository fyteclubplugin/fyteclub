const fs = require('fs-extra');
const path = require('path');
const axios = require('axios');
const config = require('./config');
const api = require('./api');

class ModManager {
  constructor() {
    this.cfg = config.load();
    this.cacheDir = this.cfg.modCache;
    fs.ensureDirSync(this.cacheDir);
  }

  hasModCached(modId) {
    const modPath = path.join(this.cacheDir, `${modId}.mod`);
    return fs.existsSync(modPath);
  }

  async downloadMod(mod) {
    try {
      const downloadUrl = await api.getModDownloadUrl(mod.id);
      if (!downloadUrl) return false;

      const response = await axios.get(downloadUrl, {
        responseType: 'stream',
        timeout: 30000
      });

      const modPath = path.join(this.cacheDir, `${mod.id}.mod`);
      const writer = fs.createWriteStream(modPath);
      
      response.data.pipe(writer);

      return new Promise((resolve, reject) => {
        writer.on('finish', () => {
          console.log(`✅ Downloaded: ${mod.name}`);
          this.saveModMetadata(mod);
          resolve(true);
        });
        writer.on('error', reject);
      });

    } catch (error) {
      console.error(`❌ Download failed for ${mod.name}:`, error.message);
      return false;
    }
  }

  saveModMetadata(mod) {
    const metaPath = path.join(this.cacheDir, `${mod.id}.json`);
    fs.writeJsonSync(metaPath, {
      ...mod,
      downloadedAt: new Date().toISOString()
    });
  }

  getModMetadata(modId) {
    const metaPath = path.join(this.cacheDir, `${modId}.json`);
    try {
      return fs.readJsonSync(metaPath);
    } catch {
      return null;
    }
  }

  listCachedMods() {
    const files = fs.readdirSync(this.cacheDir);
    return files
      .filter(f => f.endsWith('.json'))
      .map(f => f.replace('.json', ''))
      .map(id => this.getModMetadata(id))
      .filter(Boolean);
  }

  cleanup() {
    console.log('🧹 Cleaning up old mods...');
    const mods = this.listCachedMods();
    const cutoff = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000); // 30 days

    let cleaned = 0;
    for (const mod of mods) {
      const downloadDate = new Date(mod.downloadedAt);
      if (downloadDate < cutoff) {
        this.deleteMod(mod.id);
        cleaned++;
      }
    }

    console.log(`🗑️ Cleaned ${cleaned} old mods`);
  }

  deleteMod(modId) {
    const modPath = path.join(this.cacheDir, `${modId}.mod`);
    const metaPath = path.join(this.cacheDir, `${modId}.json`);
    
    fs.removeSync(modPath);
    fs.removeSync(metaPath);
  }

  getCacheSize() {
    const files = fs.readdirSync(this.cacheDir);
    let totalSize = 0;
    
    for (const file of files) {
      const filePath = path.join(this.cacheDir, file);
      const stats = fs.statSync(filePath);
      totalSize += stats.size;
    }
    
    return totalSize;
  }

  formatSize(bytes) {
    const sizes = ['B', 'KB', 'MB', 'GB'];
    if (bytes === 0) return '0 B';
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return Math.round(bytes / Math.pow(1024, i) * 100) / 100 + ' ' + sizes[i];
  }
}

module.exports = new ModManager();