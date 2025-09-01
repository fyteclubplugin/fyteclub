const axios = require('axios');
const { spawn } = require('child_process');
const crypto = require('crypto');

class PiSetup {
  constructor() {
    this.testResults = {};
    this.authToken = null;
  }
  
  // Generate secure auth token for API access
  generateAuthToken() {
    this.authToken = crypto.randomBytes(32).toString('hex');
    return this.authToken;
  }
  
  // Validate auth token
  validateAuthToken(token) {
    return this.authToken && crypto.timingSafeEqual(
      Buffer.from(token, 'hex'),
      Buffer.from(this.authToken, 'hex')
    );
  }

  async discoverPi(localNetwork = '192.168.1') {
    const discoveries = [];
    const promises = [];

    // Scan common Pi IP addresses
    for (let i = 1; i <= 254; i++) {
      const ip = `${localNetwork}.${i}`;
      promises.push(this.testPiConnection(ip));
    }

    const results = await Promise.allSettled(promises);
    
    results.forEach((result, index) => {
      if (result.status === 'fulfilled' && result.value) {
        discoveries.push({
          ip: `${localNetwork}.${index + 1}`,
          ...result.value
        });
      }
    });

    return discoveries;
  }

  async testPiConnection(ip, port = 3000) {
    try {
      const response = await axios.get(`http://${ip}:${port}/health`, {
        timeout: 2000,
        headers: { 'User-Agent': 'FyteClub-Client' }
      });

      if (response.data && response.data.service === 'fyteclub') {
        return {
          status: 'online',
          version: response.data.version,
          uptime: response.data.uptime,
          port: port
        };
      }
    } catch (error) {
      // Connection failed - not a FyteClub Pi
    }
    return null;
  }

  async runConnectivityTests(ip, port = 3000, apiKey = null) {
    const tests = {
      ping: false,
      httpConnection: false,
      apiHealth: false,
      authentication: false,
      portForwarding: false
    };

    // Test 1: Basic ping
    try {
      await this.pingHost(ip);
      tests.ping = true;
    } catch (error) {
      return { tests, error: 'Cannot ping Raspberry Pi' };
    }

    // Test 2: HTTP connection
    try {
      const response = await axios.get(`http://${ip}:${port}`, { timeout: 5000 });
      tests.httpConnection = true;
    } catch (error) {
      return { tests, error: 'Cannot connect to HTTP server' };
    }

    // Test 3: API health check
    try {
      const response = await axios.get(`http://${ip}:${port}/health`, { timeout: 5000 });
      if (response.data.service === 'fyteclub') {
        tests.apiHealth = true;
      }
    } catch (error) {
      return { tests, error: 'FyteClub API not responding' };
    }

    // Test 4: Authentication (if API key provided)
    if (apiKey) {
      try {
        const authToken = this.generateAuthToken();
        const response = await axios.get(`http://${ip}:${port}/api/status`, {
          headers: { 
            'Authorization': `Bearer ${apiKey}`,
            'X-Auth-Token': authToken
          },
          timeout: 5000
        });
        tests.authentication = true;
      } catch (error) {
        return { tests, error: 'API key authentication failed' };
      }
    }

    // Test 5: Port forwarding (check external access)
    try {
      const publicIP = await this.getPublicIP();
      if (publicIP) {
        const response = await axios.get(`http://${publicIP}:${port}/health`, { timeout: 10000 });
        if (response.data.service === 'stallionsync') {
          tests.portForwarding = true;
        }
      }
    } catch (error) {
      // Port forwarding not configured - this is optional
    }

    return { tests, success: true };
  }

  async pingHost(ip) {
    return new Promise((resolve, reject) => {
      const ping = spawn('ping', ['-c', '1', '-W', '2000', ip]);
      ping.on('close', (code) => {
        if (code === 0) {
          resolve(true);
        } else {
          reject(new Error('Ping failed'));
        }
      });
    });
  }

  async getPublicIP() {
    try {
      const response = await axios.get('https://api.ipify.org?format=json', { timeout: 5000 });
      return response.data.ip;
    } catch (error) {
      return null;
    }
  }

  async getRouterInstructions(routerBrand = 'generic') {
    const instructions = {
      generic: {
        title: 'Generic Router Port Forwarding',
        steps: [
          'Open your router admin panel (usually 192.168.1.1 or 192.168.0.1)',
          'Log in with admin credentials',
          'Find "Port Forwarding" or "Virtual Server" settings',
          'Add new rule: External Port 3000 → Internal IP {PI_IP} Port 3000',
          'Protocol: TCP',
          'Save settings and restart router'
        ]
      },
      netgear: {
        title: 'Netgear Router Port Forwarding',
        steps: [
          'Go to http://192.168.1.1 and log in',
          'Navigate to Advanced → Dynamic DNS/Port Forwarding',
          'Click "Add Custom Service"',
          'Service Name: StallionSync',
          'External Port: 3000, Internal Port: 3000',
          'Internal IP: {PI_IP}',
          'Click Apply'
        ]
      },
      linksys: {
        title: 'Linksys Router Port Forwarding',
        steps: [
          'Go to http://192.168.1.1 and log in',
          'Navigate to Smart Wi-Fi Tools → Port Forwarding',
          'Click "Add a New Port Forwarding Rule"',
          'Application Name: StallionSync',
          'External Port: 3000, Internal Port: 3000',
          'Device IP: {PI_IP}',
          'Protocol: TCP',
          'Click Save'
        ]
      }
    };

    return instructions[routerBrand] || instructions.generic;
  }

  generatePiConfig(ip, apiKey, port = 3000) {
    return {
      apiEndpoint: `http://${ip}:${port}`,
      apiKey: apiKey,
      provider: 'raspberry-pi',
      selfHosted: true,
      piIP: ip,
      piPort: port
    };
  }
}

module.exports = PiSetup;