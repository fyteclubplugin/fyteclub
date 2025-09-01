const DeduplicationService = require('./deduplication-service');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

describe('DeduplicationService', () => {
    let service;
    let testDataDir;

    beforeEach(() => {
        testDataDir = path.join(__dirname, 'test-data', 'dedup-test');
        if (fs.existsSync(testDataDir)) {
            fs.rmSync(testDataDir, { recursive: true });
        }
        fs.mkdirSync(testDataDir, { recursive: true });
        service = new DeduplicationService(testDataDir);
    });

    afterEach(() => {
        if (fs.existsSync(testDataDir)) {
            fs.rmSync(testDataDir, { recursive: true });
        }
    });

    describe('constructor', () => {
        it('should create deduplication service with data directory', () => {
            expect(service.dataDir).toBe(testDataDir);
            expect(service.contentDir).toBe(path.join(testDataDir, 'content'));
            expect(service.metadataFile).toBe(path.join(testDataDir, 'dedup-metadata.json'));
        });

        it('should create content directory if it does not exist', () => {
            expect(fs.existsSync(service.contentDir)).toBe(true);
        });
    });

    describe('initialize', () => {
        it('should initialize metadata if file does not exist', async () => {
            await service.initialize();
            expect(fs.existsSync(service.metadataFile)).toBe(true);
            expect(service.metadata).toEqual({});
        });

        it('should load existing metadata if file exists', async () => {
            const testMetadata = { 'test-hash': { refs: 1, size: 100 } };
            fs.writeFileSync(service.metadataFile, JSON.stringify(testMetadata, null, 2));
            
            await service.initialize();
            expect(service.metadata).toEqual(testMetadata);
        });
    });

    describe('storeContent', () => {
        beforeEach(async () => {
            await service.initialize();
        });

        it('should store new content and return hash', async () => {
            const content = 'test content data';
            const expectedHash = crypto.createHash('sha256').update(content).digest('hex');
            
            const hash = await service.storeContent(content);
            
            expect(hash).toBe(expectedHash);
            expect(fs.existsSync(path.join(service.contentDir, hash))).toBe(true);
            expect(service.metadata[hash]).toEqual({
                refs: 1,
                size: Buffer.byteLength(content),
                created: expect.any(Number)
            });
        });

        it('should increment reference count for duplicate content', async () => {
            const content = 'duplicate content';
            
            const hash1 = await service.storeContent(content);
            const hash2 = await service.storeContent(content);
            
            expect(hash1).toBe(hash2);
            expect(service.metadata[hash1].refs).toBe(2);
        });

        it('should handle binary content', async () => {
            const binaryContent = Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            
            const hash = await service.storeContent(binaryContent);
            
            expect(hash).toBeDefined();
            expect(fs.existsSync(path.join(service.contentDir, hash))).toBe(true);
        });
    });

    describe('getContent', () => {
        beforeEach(async () => {
            await service.initialize();
        });

        it('should retrieve stored content by hash', async () => {
            const content = 'retrievable content';
            const hash = await service.storeContent(content);
            
            const retrieved = await service.getContent(hash);
            
            expect(retrieved.toString()).toBe(content);
        });

        it('should return null for non-existent hash', async () => {
            const retrieved = await service.getContent('nonexistent-hash');
            
            expect(retrieved).toBeNull();
        });
    });

    describe('removeReference', () => {
        beforeEach(async () => {
            await service.initialize();
        });

        it('should decrement reference count', async () => {
            const content = 'content to remove ref';
            const hash = await service.storeContent(content);
            await service.storeContent(content); // Add second reference
            
            await service.removeReference(hash);
            
            expect(service.metadata[hash].refs).toBe(1);
            expect(fs.existsSync(path.join(service.contentDir, hash))).toBe(true);
        });

        it('should delete content when reference count reaches zero', async () => {
            const content = 'content to delete';
            const hash = await service.storeContent(content);
            
            await service.removeReference(hash);
            
            expect(service.metadata[hash]).toBeUndefined();
            expect(fs.existsSync(path.join(service.contentDir, hash))).toBe(false);
        });

        it('should handle removing reference for non-existent hash', async () => {
            await expect(service.removeReference('nonexistent')).resolves.not.toThrow();
        });
    });

    describe('getStats', () => {
        beforeEach(async () => {
            await service.initialize();
        });

        it('should return correct statistics', async () => {
            const content1 = 'content one';
            const content2 = 'content two';
            
            await service.storeContent(content1);
            await service.storeContent(content2);
            await service.storeContent(content1); // Duplicate
            
            const stats = await service.getStats();
            
            expect(stats.uniqueContent).toBe(2);
            expect(stats.totalReferences).toBe(3);
            expect(stats.duplicateReferences).toBe(1);
            expect(stats.totalSizeMB).toBeGreaterThan(0);
        });

        it('should return empty stats when no content stored', async () => {
            const stats = await service.getStats();
            
            expect(stats.uniqueContent).toBe(0);
            expect(stats.totalReferences).toBe(0);
            expect(stats.duplicateReferences).toBe(0);
            expect(stats.savedSpace).toBe('No duplicates found');
        });
    });

    describe('cleanup', () => {
        beforeEach(async () => {
            await service.initialize();
        });

        it('should remove content older than specified days', async () => {
            const content = 'old content';
            const hash = await service.storeContent(content);
            
            // Manually set old timestamp
            service.metadata[hash].created = Date.now() - (8 * 24 * 60 * 60 * 1000); // 8 days ago
            
            const removed = await service.cleanup(7); // Remove content older than 7 days
            
            expect(removed).toBe(1);
            expect(service.metadata[hash]).toBeUndefined();
            expect(fs.existsSync(path.join(service.contentDir, hash))).toBe(false);
        });

        it('should preserve recent content', async () => {
            const content = 'recent content';
            const hash = await service.storeContent(content);
            
            const removed = await service.cleanup(7);
            
            expect(removed).toBe(0);
            expect(service.metadata[hash]).toBeDefined();
            expect(fs.existsSync(path.join(service.contentDir, hash))).toBe(true);
        });
    });

    describe('error handling', () => {
        it('should handle file system errors gracefully', async () => {
            const readOnlyDir = path.join(__dirname, 'readonly-test');
            if (fs.existsSync(readOnlyDir)) {
                fs.rmSync(readOnlyDir, { recursive: true });
            }
            fs.mkdirSync(readOnlyDir, { recursive: true, mode: 0o444 });
            
            const readOnlyService = new DeduplicationService(readOnlyDir);
            
            await expect(readOnlyService.initialize()).rejects.toThrow();
            
            // Cleanup
            fs.chmodSync(readOnlyDir, 0o755);
            fs.rmSync(readOnlyDir, { recursive: true });
        });
    });
});
