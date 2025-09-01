#!/usr/bin/env node

const { program } = require('commander');
const setup = require('./setup');
const daemon = require('./daemon');
const config = require('./config');
const TermsOfService = require('./terms');

// Ensure terms are accepted before any command
async function ensureTerms() {
  const terms = new TermsOfService();
  return await terms.ensureTermsAccepted();
}

program
  .name('fyteclub')
  .description('FyteClub mod sharing client')
  .version('1.0.0');

program
  .command('setup')
  .description('Setup cloud provider and configuration')
  .action(async () => {
    if (await ensureTerms()) {
      setup.run();
    }
  });

program
  .command('start')
  .description('Start the sync daemon')
  .option('-d, --daemon', 'run as background daemon')
  .action(async (options) => {
    if (await ensureTerms()) {
      if (options.daemon) {
        daemon.start();
      } else {
        daemon.run();
      }
    }
  });

program
  .command('status')
  .description('Show current status')
  .action(async () => {
    if (await ensureTerms()) {
      const cfg = config.load();
      console.log('FyteClub Status:');
      console.log(`Provider: ${cfg.provider || 'Not configured'}`);
      console.log(`Endpoint: ${cfg.apiEndpoint || 'Not set'}`);
      console.log(`Player ID: ${cfg.playerId || 'Not set'}`);
      console.log(`Groups: ${cfg.groups?.length || 0}`);
    }
  });

program
  .command('gui')
  .description('Launch GUI interface')
  .action(async () => {
    // Launch Electron GUI instead of CLI GUI
    const { spawn } = require('child_process');
    const path = require('path');
    const electronPath = path.join(__dirname, '../node_modules/.bin/electron');
    const mainPath = path.join(__dirname, 'electron-main.js');
    
    spawn(electronPath, [mainPath], {
      stdio: 'inherit',
      shell: true
    });
  });

if (process.argv.length === 2) {
  // No arguments, show help
  program.help();
}

program.parse();