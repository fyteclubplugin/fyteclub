// Quick test to debug what getPlayerMods returns
const ModSyncService = require('./server/src/mod-sync-service.js');

async function debugModSyncService() {
    try {
        console.log('Creating ModSyncService instance...');
        const modSync = new ModSyncService();
        
        console.log('Getting player mods for Butter Beans...');
        const mods = await modSync.getPlayerMods('Butter Beans');
        
        console.log('ModSyncService response:');
        console.log('Type:', typeof mods);
        console.log('Keys:', mods ? Object.keys(mods) : 'null');
        
        if (mods) {
            console.log('mods.mods length:', mods.mods?.length);
            console.log('mods.lastModified:', mods.lastModified);
            console.log('mods.packagedAt:', mods.packagedAt);
            console.log('mods.targetPlayerId:', mods.targetPlayerId);
        }
        
    } catch (error) {
        console.error('Error:', error.message);
    }
}

debugModSyncService();
