const axios = require('axios');
const config = require('./config');

class APIClient {
  constructor() {
    this.cfg = config.load();
    this.client = axios.create({
      baseURL: this.cfg.apiEndpoint,
      timeout: 10000,
      headers: {
        'Content-Type': 'application/json'
      }
    });
  }

  async getPlayerMods(playerId) {
    try {
      const response = await this.client.get(`/api/v1/players/${playerId}/mods`);
      return response.data.mods || [];
    } catch (error) {
      console.error('API Error:', error.message);
      return [];
    }
  }

  async updatePlayerMods(playerId, mods) {
    try {
      await this.client.post(`/api/v1/players/${playerId}/mods`, { mods });
      return true;
    } catch (error) {
      console.error('API Error:', error.message);
      return false;
    }
  }

  async getModDownloadUrl(modId) {
    try {
      const response = await this.client.get(`/api/v1/mods/${modId}/download`);
      return response.data.download_url;
    } catch (error) {
      console.error('API Error:', error.message);
      return null;
    }
  }

  async uploadMod(modData) {
    try {
      const response = await this.client.post('/api/v1/mods', modData);
      return response.data;
    } catch (error) {
      console.error('API Error:', error.message);
      return null;
    }
  }

  async joinGroup(groupId, playerId) {
    try {
      await this.client.post(`/api/v1/groups/${groupId}/join`, { player_id: playerId });
      return true;
    } catch (error) {
      console.error('API Error:', error.message);
      return false;
    }
  }

  async getGroupMembers(groupId) {
    try {
      const response = await this.client.get(`/api/v1/groups/${groupId}/members`);
      return response.data || [];
    } catch (error) {
      console.error('API Error:', error.message);
      return [];
    }
  }
}

module.exports = new APIClient();