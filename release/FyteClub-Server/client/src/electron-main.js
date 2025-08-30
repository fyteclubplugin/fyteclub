const { app, BrowserWindow, ipcMain, Menu, Tray } = require('electron');
const path = require('path');
const config = require('./config');
const api = require('./api');
const modManager = require('./modManager');
const TermsOfService = require('./terms');

let mainWindow;
let tray;

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1000,
    height: 700,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: path.join(__dirname, 'preload.js')
    },
    icon: path.join(__dirname, '../assets/icon.png'),
    title: 'FyteClub'
  });

  mainWindow.loadFile(path.join(__dirname, '../ui/index.html'));

  // Hide to system tray instead of closing
  mainWindow.on('close', (event) => {
    if (!app.isQuiting) {
      event.preventDefault();
      mainWindow.hide();
    }
  });
}

function createTray() {
  tray = new Tray(path.join(__dirname, '../assets/tray-icon.png'));
  
  const contextMenu = Menu.buildFromTemplate([
    { label: 'Show FyteClub', click: () => mainWindow.show() },
    { label: 'Sync Now', click: () => mainWindow.webContents.send('sync-now') },
    { type: 'separator' },
    { label: 'Quit', click: () => {
      app.isQuiting = true;
      app.quit();
    }}
  ]);
  
  tray.setContextMenu(contextMenu);
  tray.setToolTip('FyteClub - Mod Sharing');
  
  tray.on('double-click', () => {
    mainWindow.show();
  });
}

app.whenReady().then(async () => {
  // Check terms acceptance first
  const terms = new TermsOfService();
  const accepted = await terms.ensureTermsAccepted();
  if (!accepted) {
    app.quit();
    return;
  }

  createWindow();
  createTray();
});

app.on('window-all-closed', () => {
  // Keep app running in system tray
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) {
    createWindow();
  }
});

// IPC handlers for renderer process
ipcMain.handle('get-config', () => config.load());
ipcMain.handle('save-config', (event, cfg) => config.save(cfg));
ipcMain.handle('get-status', () => ({
  connected: !!config.load().apiEndpoint,
  cacheSize: modManager.getCacheSize(),
  cachedMods: modManager.listCachedMods().length
}));
ipcMain.handle('join-group', (event, groupId) => api.joinGroup(groupId, config.load().playerId));
ipcMain.handle('sync-mods', () => modManager.syncMods());
ipcMain.handle('clean-cache', () => modManager.cleanup());

// AWS Setup handlers
const AWSSetup = require('./awsSetup');
const awsSetup = new AWSSetup();

ipcMain.handle('check-aws-cli', () => awsSetup.checkAWSCLI());
ipcMain.handle('check-terraform', () => awsSetup.checkTerraform());
ipcMain.handle('deploy-infrastructure', (event, region) => awsSetup.deployInfrastructure(region));
ipcMain.handle('get-terraform-outputs', () => awsSetup.getOutputs());
ipcMain.handle('save-aws-config', (event, awsConfig) => {
  const cfg = config.load();
  cfg.apiEndpoint = awsConfig.apiEndpoint;
  cfg.s3Bucket = awsConfig.s3Bucket;
  cfg.awsRegion = awsConfig.region;
  config.save(cfg);
});

ipcMain.handle('launch-aws-setup', () => {
  const setupWindow = new BrowserWindow({
    width: 800,
    height: 600,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: path.join(__dirname, 'preload.js')
    },
    parent: mainWindow,
    modal: true,
    title: 'AWS Setup - FyteClub'
  });
  
  setupWindow.loadFile(path.join(__dirname, '../ui/aws-setup.html'));
  return true;
});

// Local Server handlers
const LocalServer = require('./localServer');
let localServer = null;

ipcMain.handle('start-local-server', async (event, port) => {
  if (!localServer) {
    localServer = new LocalServer();
    await localServer.initialize();
  }
  return await localServer.start(port);
});

ipcMain.handle('stop-local-server', async () => {
  if (localServer) {
    await localServer.stop();
  }
});

ipcMain.handle('get-local-server-status', () => {
  if (localServer) {
    return localServer.getConnectionInfo();
  }
  return { status: 'stopped' };
});

ipcMain.handle('save-local-config', (event, localConfig) => {
  const cfg = config.load();
  cfg.provider = localConfig.provider;
  cfg.apiEndpoint = localConfig.apiEndpoint;
  cfg.apiKey = localConfig.apiKey;
  cfg.localServer = localConfig;
  config.save(cfg);
});

ipcMain.handle('launch-local-setup', () => {
  const setupWindow = new BrowserWindow({
    width: 800,
    height: 600,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: path.join(__dirname, 'preload.js')
    },
    parent: mainWindow,
    modal: true,
    title: 'Local PC Setup - FyteClub'
  });
  
  setupWindow.loadFile(path.join(__dirname, '../ui/local-setup.html'));
  return true;
});