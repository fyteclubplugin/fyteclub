const fs = require('fs');
const path = require('path');
const os = require('os');

class Config {
    constructor() {
        this.configDir = path.join(os.homedir(), '.fyteclub');
        this.configFile = path.join(this.configDir, 'config.json');
        this.data = {};
        this.load();
    }

    load() {
        try {
            if (!fs.existsSync(this.configDir)) {
                fs.mkdirSync(this.configDir, { recursive: true });
            }
            
            if (fs.existsSync(this.configFile)) {
                const content = fs.readFileSync(this.configFile, 'utf8');
                this.data = JSON.parse(content);
            }
        } catch (error) {
            console.error('Failed to load config:', error.message);
            this.data = {};
        }
    }

    save() {
        try {
            fs.writeFileSync(this.configFile, JSON.stringify(this.data, null, 2));
        } catch (error) {
            console.error('Failed to save config:', error.message);
        }
    }

    get(key, defaultValue = null) {
        return this.data[key] !== undefined ? this.data[key] : defaultValue;
    }

    set(key, value) {
        this.data[key] = value;
        this.save();
    }
}

module.exports = new Config();