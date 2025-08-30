const crypto = require('crypto');

describe('Encryption System', () => {
    let keyPair;
    
    beforeAll(() => {
        // Generate test key pair
        keyPair = crypto.generateKeyPairSync('rsa', {
            modulusLength: 2048,
            publicKeyEncoding: { type: 'spki', format: 'pem' },
            privateKeyEncoding: { type: 'pkcs8', format: 'pem' }
        });
    });

    describe('RSA Key Generation', () => {
        it('should generate valid RSA key pairs', () => {
            const testKeyPair = crypto.generateKeyPairSync('rsa', {
                modulusLength: 2048,
                publicKeyEncoding: { type: 'spki', format: 'pem' },
                privateKeyEncoding: { type: 'pkcs8', format: 'pem' }
            });
            
            expect(testKeyPair.publicKey).toContain('BEGIN PUBLIC KEY');
            expect(testKeyPair.privateKey).toContain('BEGIN PRIVATE KEY');
        });
    });

    describe('AES Encryption', () => {
        it('should encrypt and decrypt data correctly', () => {
            const testData = 'test mod data';
            const key = crypto.randomBytes(32);
            
            // Encrypt
            const iv = crypto.randomBytes(16);
            const cipher = crypto.createCipheriv('aes-256-cbc', key, iv);
            let encrypted = cipher.update(testData, 'utf8', 'hex');
            encrypted += cipher.final('hex');
            
            // Decrypt
            const decipher = crypto.createDecipheriv('aes-256-cbc', key, iv);
            let decrypted = decipher.update(encrypted, 'hex', 'utf8');
            decrypted += decipher.final('utf8');
            
            expect(decrypted).toBe(testData);
        });
    });

    describe('Hybrid Encryption', () => {
        it('should encrypt large data with AES and key with RSA', () => {
            const largeData = 'x'.repeat(10000); // 10KB test data
            const aesKey = crypto.randomBytes(32);
            
            // Encrypt data with AES
            const iv = crypto.randomBytes(16);
            const cipher = crypto.createCipheriv('aes-256-cbc', aesKey, iv);
            let encryptedData = cipher.update(largeData, 'utf8', 'hex');
            encryptedData += cipher.final('hex');
            
            // Encrypt AES key with RSA
            const encryptedKey = crypto.publicEncrypt(keyPair.publicKey, aesKey);
            
            // Decrypt AES key with RSA
            const decryptedKey = crypto.privateDecrypt(keyPair.privateKey, encryptedKey);
            
            // Decrypt data with AES
            const decipher = crypto.createDecipheriv('aes-256-cbc', decryptedKey, iv);
            let decryptedData = decipher.update(encryptedData, 'hex', 'utf8');
            decryptedData += decipher.final('utf8');
            
            expect(decryptedData).toBe(largeData);
        });
    });
});