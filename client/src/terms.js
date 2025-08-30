/**
 * StallionSync Terms of Service
 * Simple, humorous terms that users must accept on first run
 */

const fs = require('fs').promises;
const path = require('path');
const os = require('os');
const inquirer = require('inquirer');

class TermsOfService {
  constructor() {
    const configDir = path.join(os.homedir(), '.fyteclub');
    this.termsFile = path.join(configDir, 'terms-accepted.json');
  }

  async checkAcceptance() {
    try {
      const data = await fs.readFile(this.termsFile, 'utf8');
      const terms = JSON.parse(data);
      return terms.accepted === true;
    } catch (error) {
      return false;
    }
  }

  async showTermsDialog() {
    console.clear();
    console.log('FyteClub - Terms of Service');
    console.log('===========================\n');
    console.log('You do not talk about FyteClub, respect others, do not commit atrocities, and do not be weird.\n');
    
    const { accepted } = await inquirer.prompt([
      {
        type: 'confirm',
        name: 'accepted',
        message: 'Do you agree to these terms?',
        default: false
      }
    ]);

    if (accepted) {
      await this.saveAcceptance();
      return true;
    } else {
      console.log('\n👋 Terms not accepted. Goodbye!');
      process.exit(0);
    }
  }

  async saveAcceptance() {
    try {
      // Ensure directory exists
      const dir = path.dirname(this.termsFile);
      await fs.mkdir(dir, { recursive: true });
      
      const termsData = {
        accepted: true,
        timestamp: new Date().toISOString()
      };
      await fs.writeFile(this.termsFile, JSON.stringify(termsData, null, 2));
    } catch (error) {
      console.error('Failed to save terms acceptance:', error);
    }
  }

  async ensureTermsAccepted() {
    const accepted = await this.checkAcceptance();
    if (!accepted) {
      return await this.showTermsDialog();
    }
    return true;
  }
}

module.exports = TermsOfService;