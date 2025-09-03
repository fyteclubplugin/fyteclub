const http = require('http');

// Debug what the ModSyncService.getPlayerMods actually returns
console.log('Debugging ModSyncService.getPlayerMods response structure...\n');

const debugReq = http.request({
    hostname: 'localhost',
    port: 3000,
    path: '/api/mods/Butter%20Beans/chunked?limit=2&offset=0',
    method: 'GET'
}, (res) => {
    console.log(`Status: ${res.statusCode}`);
    console.log('Headers:');
    Object.entries(res.headers).forEach(([key, value]) => {
        console.log(`  ${key}: ${value}`);
    });
    
    let data = '';
    res.on('data', chunk => data += chunk);
    res.on('end', () => {
        try {
            const parsed = JSON.parse(data);
            console.log('\nParsed response structure:');
            console.log('Keys:', Object.keys(parsed));
            console.log('Pagination:', parsed.pagination);
            console.log('Has lastModified?', 'lastModified' in parsed);
            console.log('Has playerId?', 'playerId' in parsed);
            console.log('Mods count:', parsed.Mods?.length);
            
            if (parsed.lastModified) {
                console.log('lastModified value:', parsed.lastModified);
            } else {
                console.log('❌ lastModified field is missing from response');
            }
        } catch (e) {
            console.log('Failed to parse response:', e.message);
            console.log('Raw response:', data);
        }
    });
});

debugReq.on('error', (err) => {
    console.error('Debug request error:', err.message);
});

debugReq.end();
