const { ipcRenderer } = require('electron');

class StallionSyncUI {
    constructor() {
        this.config = {};
        this.init();
    }

    async init() {
        this.setupTabs();
        this.setupEventListeners();
        await this.loadConfig();
        await this.updateStatus();
        this.startStatusUpdates();
    }

    setupTabs() {
        const tabs = document.querySelectorAll('.tab');
        const tabContents = document.querySelectorAll('.tab-content');

        tabs.forEach(tab => {
            tab.addEventListener('click', () => {
                const targetTab = tab.dataset.tab;
                
                // Remove active class from all tabs and contents
                tabs.forEach(t => t.classList.remove('active'));
                tabContents.forEach(tc => tc.classList.remove('active'));
                
                // Add active class to clicked tab and corresponding content
                tab.classList.add('active');
                document.getElementById(targetTab).classList.add('active');
            });
        });
    }

    setupEventListeners() {
        // Dashboard actions
        document.getElementById('sync-now').addEventListener('click', () => this.syncNow());
        document.getElementById('clean-cache').addEventListener('click', () => this.cleanCache());
        
        // Groups
        document.getElementById('join-group').addEventListener('click', () => this.joinGroup());
        
        // Settings
        document.getElementById('save-config').addEventListener('click', () => this.saveConfig());
        document.getElementById('aws-setup').addEventListener('click', () => this.launchAWSSetup());
        document.getElementById('pi-setup').addEventListener('click', () => this.launchPiSetup());
        document.getElementById('local-setup').addEventListener('click', () => this.launchLocalSetup());
        
        // Listen for IPC events
        ipcRenderer.on('sync-now', () => this.syncNow());
    }

    async loadConfig() {
        this.config = await ipcRenderer.invoke('get-config');
        
        // Populate settings form
        document.getElementById('api-endpoint').value = this.config.apiEndpoint || '';
        document.getElementById('player-id').value = this.config.playerId || '';
        document.getElementById('character-name').value = this.config.characterName || '';
        document.getElementById('world-server').value = this.config.worldServer || '';
        
        // Update groups display
        this.updateGroupsList();
    }

    async updateStatus() {
        const status = await ipcRenderer.invoke('get-status');
        
        // Update status indicator
        const statusDot = document.querySelector('.dot');
        const statusText = document.getElementById('status-text');
        
        if (status.connected) {
            statusDot.className = 'dot online';
            statusText.textContent = 'Connected';
        } else {
            statusDot.className = 'dot offline';
            statusText.textContent = 'Disconnected';
        }
        
        // Update dashboard stats
        document.getElementById('connection-status').textContent = status.connected ? 'Connected' : 'Not configured';
        document.getElementById('cache-size').textContent = this.formatSize(status.cacheSize);
        document.getElementById('cached-mods').textContent = status.cachedMods;
        document.getElementById('group-count').textContent = this.config.groups?.length || 0;
    }

    updateGroupsList() {
        const groupsList = document.getElementById('groups-list');
        
        if (!this.config.groups || this.config.groups.length === 0) {
            groupsList.innerHTML = '<p class="empty-state">No groups joined yet</p>';
            return;
        }
        
        groupsList.innerHTML = this.config.groups.map(group => `
            <div class="group-item">
                <span>${group}</span>
                <button class="btn secondary" onclick="ui.leaveGroup('${group}')">Leave</button>
            </div>
        `).join('');
    }

    async syncNow() {
        const button = document.getElementById('sync-now');
        const originalText = button.textContent;
        
        button.textContent = 'Syncing...';
        button.disabled = true;
        
        try {
            await ipcRenderer.invoke('sync-mods');
            await this.updateStatus();
        } catch (error) {
            console.error('Sync failed:', error);
        } finally {
            button.textContent = originalText;
            button.disabled = false;
        }
    }

    async cleanCache() {
        const button = document.getElementById('clean-cache');
        const originalText = button.textContent;
        
        button.textContent = 'Cleaning...';
        button.disabled = true;
        
        try {
            await ipcRenderer.invoke('clean-cache');
            await this.updateStatus();
        } catch (error) {
            console.error('Cache clean failed:', error);
        } finally {
            button.textContent = originalText;
            button.disabled = false;
        }
    }

    async joinGroup() {
        const groupIdInput = document.getElementById('group-id');
        const groupId = groupIdInput.value.trim();
        
        if (!groupId) {
            alert('Please enter a group ID');
            return;
        }
        
        try {
            const success = await ipcRenderer.invoke('join-group', groupId);
            if (success) {
                groupIdInput.value = '';
                await this.loadConfig();
                alert('Successfully joined group!');
            } else {
                alert('Failed to join group');
            }
        } catch (error) {
            console.error('Join group failed:', error);
            alert('Failed to join group');
        }
    }

    async saveConfig() {
        const newConfig = {
            ...this.config,
            apiEndpoint: document.getElementById('api-endpoint').value,
            playerId: document.getElementById('player-id').value,
            characterName: document.getElementById('character-name').value,
            worldServer: document.getElementById('world-server').value
        };
        
        try {
            await ipcRenderer.invoke('save-config', newConfig);
            this.config = newConfig;
            await this.updateStatus();
            alert('Configuration saved!');
        } catch (error) {
            console.error('Save config failed:', error);
            alert('Failed to save configuration');
        }
    }

    formatSize(bytes) {
        if (bytes === 0) return '0 MB';
        const k = 1024;
        const sizes = ['Bytes', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }

    async launchAWSSetup() {
        try {
            await ipcRenderer.invoke('launch-aws-setup');
        } catch (error) {
            console.error('Failed to launch AWS setup:', error);
            alert('Failed to launch AWS setup wizard');
        }
    }

    async launchPiSetup() {
        try {
            await ipcRenderer.invoke('launch-pi-setup');
        } catch (error) {
            console.error('Failed to launch Pi setup:', error);
            alert('Failed to launch Raspberry Pi setup wizard');
        }
    }

    async launchLocalSetup() {
        try {
            await ipcRenderer.invoke('launch-local-setup');
        } catch (error) {
            console.error('Failed to launch local setup:', error);
            alert('Failed to launch local PC setup wizard');
        }
    }

    startStatusUpdates() {
        // Update status every 30 seconds
        setInterval(() => this.updateStatus(), 30000);
    }
}

// Initialize UI when DOM is loaded
const ui = new StallionSyncUI();