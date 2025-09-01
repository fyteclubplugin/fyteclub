const { createServer } = require('net');
const fs = require('fs');
const path = require('path');
const config = require('./config');
const api = require('./api');
const modManager = require('./modManager');

class IPCServer {
  constructor() {
    this.server = null;
    this.clients = new Set();
    this.isRunning = false;
  }

  start() {
    if (this.isRunning) return;

    // Create named pipe server for Windows
    if (process.platform === 'win32') {
      this.startNamedPipeServer();
    } else {
      this.startUnixSocketServer();
    }
  }

  startNamedPipeServer() {
    const pipeName = '\\\\.\\pipe\\friendssync_pipe';
    
    this.server = createServer((socket) => {
      console.log('🔌 FFXIV plugin connected');
      this.clients.add(socket);
      
      socket.on('data', (data) => {
        this.handlePluginMessage(socket, data);
      });
      
      socket.on('end', () => {
        console.log('🔌 FFXIV plugin disconnected');
        this.clients.delete(socket);
      });
      
      socket.on('error', (err) => {
        console.error('Plugin connection error:', err.message);
        this.clients.delete(socket);
      });
    });

    this.server.listen(pipeName, () => {
      console.log('🎮 IPC server listening for FFXIV plugin');
      this.isRunning = true;
    });
  }

  startUnixSocketServer() {
    const socketPath = '/tmp/friendssync.sock';
    
    // Remove existing socket
    if (fs.existsSync(socketPath)) {
      fs.unlinkSync(socketPath);
    }

    this.server = createServer((socket) => {
      console.log('🔌 FFXIV plugin connected');
      this.clients.add(socket);
      
      socket.on('data', (data) => {
        this.handlePluginMessage(socket, data);
      });
      
      socket.on('end', () => {
        console.log('🔌 FFXIV plugin disconnected');
        this.clients.delete(socket);
      });
    });

    this.server.listen(socketPath, () => {
      console.log('🎮 IPC server listening for FFXIV plugin');
      this.isRunning = true;
    });
  }

  async handlePluginMessage(socket, data) {
    try {
      const messages = data.toString().split('\n').filter(Boolean);
      
      for (const messageStr of messages) {
        const message = JSON.parse(messageStr);
        
        switch (message.type) {
          case 'nearby_players':
            await this.handleNearbyPlayers(message.players);
            break;
          case 'player_left':
            await this.handlePlayerLeft(message.player);
            break;
          default:
            console.log('Unknown message type:', message.type);
        }
      }
    } catch (error) {
      console.error('Error handling plugin message:', error.message);
    }
  }

  async handleNearbyPlayers(players) {
    const cfg = config.load();
    
    for (const player of players) {
      const playerId = `${player.Name}@${this.getWorldName(player.WorldId)}`;
      
      try {
        // Get player's mods from API
        const playerMods = await api.getPlayerMods(playerId);
        
        // Check which mods we need to download
        for (const mod of playerMods) {
          if (!modManager.hasModCached(mod.id)) {
            console.log(`📥 Downloading mod for ${player.Name}: ${mod.name}`);
            const success = await modManager.downloadMod(mod);
            
            if (success) {
              // Tell plugin to apply the mod
              this.sendToPlugin({
                type: 'apply_mod',
                playerName: player.Name,
                modId: mod.id,
                modPath: modManager.getModPath(mod.id)
              });
            }
          } else {
            // Mod already cached, just apply it
            this.sendToPlugin({
              type: 'apply_mod',
              playerName: player.Name,
              modId: mod.id,
              modPath: modManager.getModPath(mod.id)
            });
          }
        }
      } catch (error) {
        console.error(`Error processing mods for ${player.Name}:`, error.message);
      }
    }
  }

  async handlePlayerLeft(player) {
    // Remove mods for player who left the area
    this.sendToPlugin({
      type: 'remove_player_mods',
      playerName: player.Name
    });
  }

  sendToPlugin(message) {
    const data = JSON.stringify(message) + '\n';
    
    for (const client of this.clients) {
      try {
        client.write(data);
      } catch (error) {
        console.error('Error sending to plugin:', error.message);
        this.clients.delete(client);
      }
    }
  }

  getWorldName(worldId) {
    // Map world IDs to names (simplified)
    const worlds = {
      34: 'Brynhildr',
      35: 'Diabolos',
      40: 'Gilgamesh',
      41: 'Jenova',
      42: 'Midgardsormr',
      43: 'Sargatanas',
      44: 'Siren'
      // Add more as needed
    };
    
    return worlds[worldId] || `World${worldId}`;
  }

  stop() {
    if (this.server) {
      this.server.close();
      this.isRunning = false;
    }
  }
}

module.exports = new IPCServer();