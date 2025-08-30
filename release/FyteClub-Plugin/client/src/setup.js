const inquirer = require('inquirer');
const fs = require('fs-extra');
const path = require('path');
const config = require('./config');

const PROVIDERS = {
  aws: {
    name: 'Amazon Web Services (AWS)',
    cost: '$0-5/month',
    difficulty: 'Easy',
    setup: setupAWS
  },
  gcp: {
    name: 'Google Cloud Platform',
    cost: '$0-3/month',
    difficulty: 'Medium',
    setup: setupGCP
  },
  azure: {
    name: 'Microsoft Azure',
    cost: '$0-4/month',
    difficulty: 'Medium',
    setup: setupAzure
  },
  selfhosted: {
    name: 'Self-hosted (Raspberry Pi/VPS)',
    cost: '$0/month',
    difficulty: 'Hard',
    setup: setupSelfHosted
  }
};

async function run() {
  console.log('👥 FriendsSync Setup Wizard');
  console.log('==========================\n');

  const answers = await inquirer.prompt([
    {
      type: 'list',
      name: 'provider',
      message: 'Choose your hosting provider:',
      choices: Object.entries(PROVIDERS).map(([key, provider]) => ({
        name: `${provider.name} (${provider.cost}, ${provider.difficulty})`,
        value: key
      }))
    }
  ]);

  const provider = PROVIDERS[answers.provider];
  console.log(`\nSetting up ${provider.name}...\n`);
  
  await provider.setup();
  
  // Get player info
  const playerInfo = await inquirer.prompt([
    {
      type: 'input',
      name: 'characterName',
      message: 'FFXIV Character Name:',
      validate: input => input.length > 0
    },
    {
      type: 'input',
      name: 'worldServer',
      message: 'World Server (e.g., Gilgamesh):',
      validate: input => input.length > 0
    }
  ]);

  // Save configuration
  const cfg = config.load();
  cfg.provider = answers.provider;
  cfg.playerId = `${playerInfo.characterName}@${playerInfo.worldServer}`;
  cfg.characterName = playerInfo.characterName;
  cfg.worldServer = playerInfo.worldServer;
  config.save(cfg);

  console.log('\n✅ Setup complete!');
  console.log('\nNext steps:');
  console.log('1. friendssync start    # Start the sync daemon');
  console.log('2. friendssync gui      # Launch GUI interface');
}

async function setupAWS() {
  console.log('AWS Setup Instructions:');
  console.log('1. Create AWS account (free tier)');
  console.log('2. Install AWS CLI and run: aws configure');
  console.log('3. Deploy infrastructure using our Terraform scripts');
  
  const answers = await inquirer.prompt([
    {
      type: 'input',
      name: 'apiEndpoint',
      message: 'API Gateway endpoint URL:',
      validate: input => input.startsWith('https://')
    },
    {
      type: 'input',
      name: 'region',
      message: 'AWS Region:',
      default: 'us-east-1'
    }
  ]);

  const cfg = config.load();
  cfg.apiEndpoint = answers.apiEndpoint;
  cfg.awsRegion = answers.region;
  config.save(cfg);
}

async function setupGCP() {
  console.log('Google Cloud Setup:');
  console.log('Coming soon - GCP support in development');
  process.exit(1);
}

async function setupAzure() {
  console.log('Azure Setup:');
  console.log('Coming soon - Azure support in development');
  process.exit(1);
}

async function setupSelfHosted() {
  console.log('Self-hosted Setup:');
  console.log('Perfect for Raspberry Pi!');
  
  const answers = await inquirer.prompt([
    {
      type: 'input',
      name: 'serverUrl',
      message: 'Server URL (e.g., http://192.168.1.100:3000):',
      validate: input => input.startsWith('http')
    },
    {
      type: 'confirm',
      name: 'setupServer',
      message: 'Do you want to setup the server on this machine?',
      default: false
    }
  ]);

  if (answers.setupServer) {
    console.log('\nInstalling server components...');
    // TODO: Install Docker containers for self-hosted setup
    console.log('Server setup coming soon!');
  }

  const cfg = config.load();
  cfg.apiEndpoint = answers.serverUrl;
  config.save(cfg);
}

module.exports = { run };