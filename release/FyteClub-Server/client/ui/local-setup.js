const { ipcRenderer } = require('electron');

let currentStep = 1;
let selectedOption = null;
let serverInfo = null;

function updateProgress() {
    const progress = (currentStep / 3) * 100;
    document.getElementById('progress').style.width = `${progress}%`;
}

function nextStep() {
    if (currentStep < 3) {
        document.getElementById(`step${currentStep}`).classList.remove('active');
        currentStep++;
        document.getElementById(`step${currentStep}`).classList.add('active');
        updateProgress();
    }
}

function prevStep() {
    if (currentStep > 1) {
        document.getElementById(`step${currentStep}`).classList.remove('active');
        currentStep--;
        document.getElementById(`step${currentStep}`).classList.add('active');
        updateProgress();
    }
}

function selectOption(option) {
    selectedOption = option;
    
    // Remove selection from all options
    document.querySelectorAll('.hosting-option').forEach(opt => {
        opt.classList.remove('selected');
    });
    
    // Select clicked option
    document.getElementById(`option-${option}`).classList.add('selected');
    
    // Show continue button
    document.getElementById('continue-setup').style.display = 'inline-block';
}

async function startServer() {
    const port = parseInt(document.getElementById('server-port').value);
    const startBtn = document.getElementById('start-server');
    const continueBtn = document.getElementById('continue-final');
    
    startBtn.textContent = 'Starting...';
    startBtn.disabled = true;
    
    try {
        if (selectedOption === 'embedded') {
            serverInfo = await ipcRenderer.invoke('start-local-server', port);
        } else {
            serverInfo = await ipcRenderer.invoke('install-standalone-server', port);
        }
        
        // Update status
        document.getElementById('status-dot').className = 'status-dot running';
        document.getElementById('status-text').textContent = 'Server running';
        
        // Show continue button
        continueBtn.style.display = 'inline-block';
        startBtn.style.display = 'none';
        
        // Update connection info
        updateConnectionInfo();
        
    } catch (error) {
        alert(`Failed to start server: ${error.message}`);
        startBtn.textContent = 'Start Server';
        startBtn.disabled = false;
    }
}

function updateConnectionInfo() {
    if (serverInfo) {
        document.getElementById('server-url').textContent = serverInfo.url;
        document.getElementById('api-key').textContent = serverInfo.apiKey;
        document.getElementById('final-status').textContent = 'Running';
        document.getElementById('port-number').textContent = serverInfo.port;
    }
}

async function finishSetup() {
    if (serverInfo) {
        // Save local server configuration
        await ipcRenderer.invoke('save-local-config', {
            provider: 'local-pc',
            apiEndpoint: serverInfo.url,
            apiKey: serverInfo.apiKey,
            port: serverInfo.port,
            mode: selectedOption
        });
    }
    
    // Close setup window
    ipcRenderer.send('local-setup-complete');
    window.close();
}

function openPortForwardingGuide() {
    // Open port forwarding guide in default browser
    require('electron').shell.openExternal('https://portforward.com/');
}

// Auto-update server status
async function checkServerStatus() {
    try {
        const status = await ipcRenderer.invoke('get-local-server-status');
        if (status && status.running) {
            document.getElementById('status-dot').className = 'status-dot running';
            document.getElementById('status-text').textContent = `Running on port ${status.port}`;
        } else {
            document.getElementById('status-dot').className = 'status-dot stopped';
            document.getElementById('status-text').textContent = 'Server stopped';
        }
    } catch (error) {
        // Server not running
    }
}

// Initialize
document.addEventListener('DOMContentLoaded', () => {
    updateProgress();
    
    // Check server status every 5 seconds
    setInterval(checkServerStatus, 5000);
});