const { contextBridge, ipcRenderer } = require('electron');

// Expose protected methods that allow the renderer process to use
// the ipcRenderer without exposing the entire object
contextBridge.exposeInMainWorld('electronAPI', {
  // Config operations
  getConfig: () => ipcRenderer.invoke('get-config'),
  saveConfig: (config) => ipcRenderer.invoke('save-config', config),
  
  // Status operations
  getStatus: () => ipcRenderer.invoke('get-status'),
  
  // Group operations
  joinGroup: (groupId) => ipcRenderer.invoke('join-group', groupId),
  
  // Mod operations
  syncMods: () => ipcRenderer.invoke('sync-mods'),
  cleanCache: () => ipcRenderer.invoke('clean-cache'),
  
  // AWS setup operations
  checkAWSCLI: () => ipcRenderer.invoke('check-aws-cli'),
  checkTerraform: () => ipcRenderer.invoke('check-terraform'),
  deployInfrastructure: (region) => ipcRenderer.invoke('deploy-infrastructure', region),
  getTerraformOutputs: () => ipcRenderer.invoke('get-terraform-outputs'),
  saveAWSConfig: (awsConfig) => ipcRenderer.invoke('save-aws-config', awsConfig),
  launchAWSSetup: () => ipcRenderer.invoke('launch-aws-setup'),
  
  // Local server operations
  startLocalServer: (port) => ipcRenderer.invoke('start-local-server', port),
  stopLocalServer: () => ipcRenderer.invoke('stop-local-server'),
  getLocalServerStatus: () => ipcRenderer.invoke('get-local-server-status'),
  saveLocalConfig: (localConfig) => ipcRenderer.invoke('save-local-config', localConfig),
  launchLocalSetup: () => ipcRenderer.invoke('launch-local-setup'),
  
  // Event listeners
  onSyncNow: (callback) => ipcRenderer.on('sync-now', callback),
  removeAllListeners: (channel) => ipcRenderer.removeAllListeners(channel)
});