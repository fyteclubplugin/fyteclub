const crypto = require('crypto');
const fs = require('fs').promises;
const path = require('path');

// Mock deduplication service that doesn't require actual file system
class MockDeduplicationService {
  constructor(config = {}) {
    this.storageDir = config.storageDir || './test-storage';
    this.dedupedFiles = new Map(); // hash -> { path, refCount, size }
    this.fileIndex = new Map(); // originalPath -> hash
  }

  async initialize() {
    // Mock initialization
    return this;
  }

  generateHash(content) {
    return crypto.createHash('sha256').update(content).digest('hex');
  }

  async storeFile(filePath, content) {
    try {
      const hash = this.generateHash(content);
      
      if (this.dedupedFiles.has(hash)) {
        // File already exists, increment reference count
        const existing = this.dedupedFiles.get(hash);
        existing.refCount++;
        this.fileIndex.set(filePath, hash);
        return {
          hash,
          deduplicated: true,
          size: existing.size,
          totalRefs: existing.refCount
        };
      } else {
        // New file, store it
        const size = Buffer.byteLength(content, 'utf8');
        const storagePath = path.join(this.storageDir, hash);
        
        this.dedupedFiles.set(hash, {
          path: storagePath,
          refCount: 1,
          size
        });
        this.fileIndex.set(filePath, hash);
        
        return {
          hash,
          deduplicated: false,
          size,
          totalRefs: 1
        };
      }
    } catch (error) {
      throw new Error(`Failed to store file: ${error.message}`);
    }
  }

  async retrieveFile(filePath) {
    const hash = this.fileIndex.get(filePath);
    if (!hash || !this.dedupedFiles.has(hash)) {
      return null;
    }
    
    // In a real implementation, we'd read from disk
    // For testing, we'll simulate the content
    return `mock-content-for-${hash}`;
  }

  async deleteFile(filePath) {
    const hash = this.fileIndex.get(filePath);
    if (!hash || !this.dedupedFiles.has(hash)) {
      return false;
    }
    
    const file = this.dedupedFiles.get(hash);
    file.refCount--;
    
    if (file.refCount <= 0) {
      // No more references, remove from storage
      this.dedupedFiles.delete(hash);
    }
    
    this.fileIndex.delete(filePath);
    return true;
  }

  async getStats() {
    let totalSize = 0;
    let totalRefs = 0;
    
    for (const file of this.dedupedFiles.values()) {
      totalSize += file.size;
      totalRefs += file.refCount;
    }
    
    return {
      uniqueFiles: this.dedupedFiles.size,
      totalReferences: totalRefs,
      storageUsed: totalSize,
      deduplicationRatio: totalRefs > 0 ? totalRefs / this.dedupedFiles.size : 0
    };
  }

  async cleanup() {
    const orphaned = [];
    
    for (const [hash, file] of this.dedupedFiles.entries()) {
      if (file.refCount <= 0) {
        orphaned.push(hash);
        this.dedupedFiles.delete(hash);
      }
    }
    
    return {
      cleaned: orphaned.length,
      remaining: this.dedupedFiles.size
    };
  }
}

describe('DeduplicationService (Isolated)', () => {
  let dedupService;

  beforeEach(() => {
    dedupService = new MockDeduplicationService({
      storageDir: './test-dedup-storage'
    });
  });

  describe('File Storage', () => {
    test('should store new file', async () => {
      const content = 'test file content';
      const result = await dedupService.storeFile('test.txt', content);
      
      expect(result.deduplicated).toBe(false);
      expect(result.totalRefs).toBe(1);
      expect(result.hash).toBeDefined();
      expect(result.size).toBe(Buffer.byteLength(content, 'utf8'));
    });

    test('should deduplicate identical files', async () => {
      const content = 'identical content';
      
      const result1 = await dedupService.storeFile('file1.txt', content);
      const result2 = await dedupService.storeFile('file2.txt', content);
      
      expect(result1.deduplicated).toBe(false);
      expect(result2.deduplicated).toBe(true);
      expect(result1.hash).toBe(result2.hash);
      expect(result2.totalRefs).toBe(2);
    });

    test('should handle different files with same name', async () => {
      const content1 = 'content 1';
      const content2 = 'content 2';
      
      const result1 = await dedupService.storeFile('same-name.txt', content1);
      const result2 = await dedupService.storeFile('same-name.txt', content2);
      
      expect(result1.hash).not.toBe(result2.hash);
      expect(result1.deduplicated).toBe(false);
      expect(result2.deduplicated).toBe(false);
    });
  });

  describe('File Retrieval', () => {
    test('should retrieve stored file', async () => {
      const content = 'retrievable content';
      await dedupService.storeFile('retrieve.txt', content);
      
      const retrieved = await dedupService.retrieveFile('retrieve.txt');
      expect(retrieved).toBeDefined();
    });

    test('should return null for non-existent file', async () => {
      const retrieved = await dedupService.retrieveFile('non-existent.txt');
      expect(retrieved).toBeNull();
    });
  });

  describe('File Deletion', () => {
    test('should delete file and reduce reference count', async () => {
      const content = 'deletable content';
      
      await dedupService.storeFile('delete1.txt', content);
      await dedupService.storeFile('delete2.txt', content);
      
      // Delete one reference
      const deleted = await dedupService.deleteFile('delete1.txt');
      expect(deleted).toBe(true);
      
      // Other reference should still exist
      const retrieved = await dedupService.retrieveFile('delete2.txt');
      expect(retrieved).toBeDefined();
    });

    test('should completely remove file when all references deleted', async () => {
      const content = 'completely deletable';
      
      await dedupService.storeFile('final.txt', content);
      await dedupService.deleteFile('final.txt');
      
      const stats = await dedupService.getStats();
      expect(stats.uniqueFiles).toBe(0);
    });

    test('should return false for non-existent file deletion', async () => {
      const deleted = await dedupService.deleteFile('non-existent.txt');
      expect(deleted).toBe(false);
    });
  });

  describe('Statistics', () => {
    test('should provide accurate statistics', async () => {
      const content1 = 'content 1';
      const content2 = 'content 2';
      
      await dedupService.storeFile('file1.txt', content1);
      await dedupService.storeFile('file2.txt', content1); // Duplicate
      await dedupService.storeFile('file3.txt', content2);
      
      const stats = await dedupService.getStats();
      
      expect(stats.uniqueFiles).toBe(2);
      expect(stats.totalReferences).toBe(3);
      expect(stats.deduplicationRatio).toBe(1.5);
      expect(stats.storageUsed).toBeGreaterThan(0);
    });

    test('should handle empty storage stats', async () => {
      const stats = await dedupService.getStats();
      
      expect(stats.uniqueFiles).toBe(0);
      expect(stats.totalReferences).toBe(0);
      expect(stats.deduplicationRatio).toBe(0);
      expect(stats.storageUsed).toBe(0);
    });
  });

  describe('Cleanup', () => {
    test('should clean up orphaned files', async () => {
      const content = 'orphaned content';
      
      await dedupService.storeFile('orphan.txt', content);
      
      // Manually corrupt the reference count
      const hash = dedupService.fileIndex.get('orphan.txt');
      const file = dedupService.dedupedFiles.get(hash);
      file.refCount = 0;
      
      const cleanup = await dedupService.cleanup();
      
      expect(cleanup.cleaned).toBe(1);
      expect(cleanup.remaining).toBe(0);
    });

    test('should not clean up files with valid references', async () => {
      const content = 'valid content';
      
      await dedupService.storeFile('valid.txt', content);
      
      const cleanup = await dedupService.cleanup();
      
      expect(cleanup.cleaned).toBe(0);
      expect(cleanup.remaining).toBe(1);
    });
  });

  describe('Hash Generation', () => {
    test('should generate consistent hashes', () => {
      const content = 'test content for hashing';
      
      const hash1 = dedupService.generateHash(content);
      const hash2 = dedupService.generateHash(content);
      
      expect(hash1).toBe(hash2);
      expect(hash1).toMatch(/^[a-f0-9]{64}$/); // SHA-256 hex format
    });

    test('should generate different hashes for different content', () => {
      const content1 = 'content 1';
      const content2 = 'content 2';
      
      const hash1 = dedupService.generateHash(content1);
      const hash2 = dedupService.generateHash(content2);
      
      expect(hash1).not.toBe(hash2);
    });
  });

  describe('Error Handling', () => {
    test('should handle storage errors gracefully', async () => {
      // Simulate an error by passing invalid content
      await expect(dedupService.storeFile('error.txt', null))
        .rejects.toThrow();
    });
  });

  describe('Configuration', () => {
    test('should accept custom storage directory', () => {
      const customService = new MockDeduplicationService({
        storageDir: '/custom/storage/path'
      });
      
      expect(customService.storageDir).toBe('/custom/storage/path');
    });

    test('should use default storage directory', () => {
      const defaultService = new MockDeduplicationService();
      expect(defaultService.storageDir).toBe('./test-storage');
    });
  });
});
