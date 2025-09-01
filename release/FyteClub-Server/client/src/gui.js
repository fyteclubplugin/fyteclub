const inquirer = require('inquirer');
const config = require('./config');
const api = require('./api');
const modManager = require('./modManager');
const TermsOfService = require('./terms');

async function start() {
  // Check terms acceptance first
  const terms = new TermsOfService();
  const accepted = await terms.ensureTermsAccepted();
  if (!accepted) {
    return; // App will quit if terms not accepted
  }

  console.clear();
  console.log('FyteClub GUI');
  console.log('============\n');

  while (true) {
    const cfg = config.load();
    
    // Show status
    console.log(`Status: ${cfg.apiEndpoint ? '🟢 Connected' : '🔴 Not configured'}`);
    console.log(`Player: ${cfg.playerId || 'Not set'}`);
    console.log(`Cache: ${modManager.formatSize(modManager.getCacheSize())}`);
    console.log(`Mods: ${modManager.listCachedMods().length} cached\n`);

    const { action } = await inquirer.prompt([
      {
        type: 'list',
        name: 'action',
        message: 'What would you like to do?',
        choices: [
          { name: '⚙️  Setup/Reconfigure', value: 'setup' },
          { name: '👥 Manage Groups', value: 'groups' },
          { name: '📦 View Cached Mods', value: 'mods' },
          { name: '🧹 Clean Cache', value: 'clean' },
          { name: '📊 Show Status', value: 'status' },
          { name: '🚪 Exit', value: 'exit' }
        ]
      }
    ]);

    console.log();

    switch (action) {
      case 'setup':
        await require('./setup').run();
        break;
      case 'groups':
        await manageGroups();
        break;
      case 'mods':
        await viewMods();
        break;
      case 'clean':
        modManager.cleanup();
        break;
      case 'status':
        await showStatus();
        break;
      case 'exit':
        console.log('👋 Goodbye!');
        process.exit(0);
    }

    console.log('\nPress Enter to continue...');
    await inquirer.prompt([{ type: 'input', name: 'continue', message: '' }]);
    console.clear();
  }
}

async function manageGroups() {
  const cfg = config.load();
  
  if (!cfg.apiEndpoint) {
    console.log('❌ Please run setup first');
    return;
  }

  const { action } = await inquirer.prompt([
    {
      type: 'list',
      name: 'action',
      message: 'Group management:',
      choices: [
        { name: 'Join a group', value: 'join' },
        { name: 'View my groups', value: 'view' },
        { name: 'Back', value: 'back' }
      ]
    }
  ]);

  if (action === 'join') {
    const { groupId } = await inquirer.prompt([
      {
        type: 'input',
        name: 'groupId',
        message: 'Group ID to join:',
        validate: input => input.length > 0
      }
    ]);

    const success = await api.joinGroup(groupId, cfg.playerId);
    if (success) {
      cfg.groups = cfg.groups || [];
      if (!cfg.groups.includes(groupId)) {
        cfg.groups.push(groupId);
        config.save(cfg);
      }
      console.log('✅ Joined group successfully!');
    } else {
      console.log('❌ Failed to join group');
    }
  } else if (action === 'view') {
    console.log('Your groups:');
    if (cfg.groups && cfg.groups.length > 0) {
      cfg.groups.forEach((group, i) => {
        console.log(`${i + 1}. ${group}`);
      });
    } else {
      console.log('No groups joined yet');
    }
  }
}

async function viewMods() {
  const mods = modManager.listCachedMods();
  
  if (mods.length === 0) {
    console.log('No mods cached yet');
    return;
  }

  console.log('Cached mods:');
  mods.forEach((mod, i) => {
    console.log(`${i + 1}. ${mod.name || mod.id}`);
    console.log(`   Downloaded: ${new Date(mod.downloadedAt).toLocaleDateString()}`);
  });
}

async function showStatus() {
  const cfg = config.load();
  
  console.log('=== FyteClub Status ===');
  console.log(`Provider: ${cfg.provider || 'Not configured'}`);
  console.log(`API Endpoint: ${cfg.apiEndpoint || 'Not set'}`);
  console.log(`Player ID: ${cfg.playerId || 'Not set'}`);
  console.log(`Character: ${cfg.characterName || 'Not set'}`);
  console.log(`World: ${cfg.worldServer || 'Not set'}`);
  console.log(`Groups: ${cfg.groups?.length || 0}`);
  console.log(`Last Sync: ${cfg.lastSync ? new Date(cfg.lastSync).toLocaleString() : 'Never'}`);
  console.log(`Cache Size: ${modManager.formatSize(modManager.getCacheSize())}`);
  console.log(`Cached Mods: ${modManager.listCachedMods().length}`);
}

module.exports = { start };