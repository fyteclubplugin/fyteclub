const { ipcRenderer } = require('electron');

let currentStep = 1;
let selectedPi = null;

function updateProgress() {
    const progress = (currentStep / 5) * 100;
    document.getElementById('progress').style.width = `${progress}%`;
}

function nextStep() {
    if (currentStep < 5) {
        document.getElementById(`step${currentStep}`).classList.remove('active');
        currentStep++;
        document.getElementById(`step${currentStep}`).classList.add('active');
        updateProgress();
        
        // Auto-trigger actions for certain steps
        if (currentStep === 2) {
            scanNetwork();
        } else if (currentStep === 4) {
            updateRouterInstructions();
        }
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

function copyInstallCommand() {
    const command = document.getElementById('install-cmd').textContent;
    navigator.clipboard.writeText(command).then(() => {
        const btn = document.querySelector('.copy-btn');
        const originalText = btn.textContent;
        btn.textContent = 'Copied!';
        setTimeout(() => {
            btn.textContent = originalText;
        }, 2000);
    });
}

async function scanNetwork() {
    const discoveryDiv = document.getElementById('pi-discovery');
    discoveryDiv.innerHTML = '<p>🔍 Scanning network for StallionSync servers...</p>';
    
    try {
        const discoveries = await ipcRenderer.invoke('scan-pi-network');
        
        if (discoveries.length === 0) {
            discoveryDiv.innerHTML = `
                <p>❌ No StallionSync servers found on your network</p>
                <p>Make sure your Raspberry Pi is:</p>
                <ul>
                    <li>Connected to the same network</li>
                    <li>Running StallionSync service</li>
                    <li>Firewall allows port 3000</li>
                </ul>
            `;
        } else {
            let html = '<h3>Found StallionSync Servers:</h3>';
            discoveries.forEach((pi, index) => {
                html += `
                    <div class="pi-item" onclick="selectPi('${pi.ip}', ${pi.port})">
                        <div class="pi-info">
                            <strong>${pi.ip}:${pi.port}</strong><br>
                            <small>Version: ${pi.version} | Uptime: ${pi.uptime}s</small>
                        </div>
                        <div class="pi-status online">Online</div>
                    </div>
                `;
            });
            discoveryDiv.innerHTML = html;
            
            // Auto-select first Pi if only one found
            if (discoveries.length === 1) {
                selectPi(discoveries[0].ip, discoveries[0].port);
            }
        }
    } catch (error) {
        discoveryDiv.innerHTML = `<p>❌ Network scan failed: ${error.message}</p>`;
    }
}

function selectPi(ip, port = 3000) {
    selectedPi = { ip, port };
    
    // Highlight selected Pi
    document.querySelectorAll('.pi-item').forEach(item => {
        item.style.border = '1px solid #404040';
    });
    event.target.closest('.pi-item').style.border = '2px solid #ff6b35';
    
    // Show continue button
    document.getElementById('continue-tests').style.display = 'inline-block';
}

async function testManualIP() {
    const ip = document.getElementById('manual-ip').value.trim();
    if (!ip) {
        showNotification('Please enter an IP address', 'error');
        return;
    }
    
    try {
        const result = await ipcRenderer.invoke('test-pi-connection', ip, 3000);
        if (result) {
            selectPi(ip, 3000);
            showNotification('✅ FyteClub server found!', 'success');
        } else {
            showNotification('❌ No FyteClub server found at this IP', 'error');
        }
    } catch (error) {
        showNotification(`❌ Connection test failed: ${error.message}`, 'error');
    }
}

async function runTests() {
    if (!selectedPi) {
        showNotification('Please select a Raspberry Pi first', 'error');
        return;
    }
    
    const apiKey = document.getElementById('api-key').value.trim();
    const testItems = document.querySelectorAll('.test-item');
    
    // Reset all tests to pending
    testItems.forEach(item => {
        item.className = 'test-item pending';
    });
    
    try {
        const results = await ipcRenderer.invoke('run-pi-tests', selectedPi.ip, selectedPi.port, apiKey);
        
        // Update test results
        const tests = ['ping', 'httpConnection', 'apiHealth', 'authentication', 'portForwarding'];
        tests.forEach((test, index) => {
            const item = testItems[index];
            if (results.tests[test]) {
                item.className = 'test-item passed';
            } else {
                item.className = 'test-item failed';
            }
        });
        
        if (results.success) {
            document.getElementById('continue-port').style.display = 'inline-block';
        } else {
            showNotification(`❌ Tests failed: ${results.error}`, 'error');
        }
        
    } catch (error) {
        showNotification(`❌ Test execution failed: ${error.message}`, 'error');
    }
}

async function updateRouterInstructions() {
    const brand = document.getElementById('router-brand').value;
    
    try {
        const instructions = await ipcRenderer.invoke('get-router-instructions', brand);
        
        document.querySelector('#port-guide h3').textContent = instructions.title;
        
        const instructionsList = document.getElementById('port-instructions');
        instructionsList.innerHTML = '';
        
        instructions.steps.forEach(step => {
            const li = document.createElement('li');
            li.textContent = step.replace('{PI_IP}', selectedPi?.ip || 'YOUR_PI_IP');
            instructionsList.appendChild(li);
        });
        
    } catch (error) {
        console.error('Failed to load router instructions:', error);
    }
}

async function testPortForwarding() {
    if (!selectedPi) {
        showNotification('No Pi selected', 'error');
        return;
    }
    
    try {
        const result = await ipcRenderer.invoke('test-port-forwarding', selectedPi.ip, selectedPi.port);
        if (result) {
            showNotification('✅ Port forwarding is working! External access enabled.', 'success');
        } else {
            showNotification('❌ Port forwarding not detected. FyteClub will work locally only.', 'warning');
        }
    } catch (error) {
        showNotification(`❌ Port forwarding test failed: ${error.message}`, 'error');
    }
}

function finishSetup() {
    if (selectedPi) {
        // Update final step info
        document.getElementById('final-pi-ip').textContent = selectedPi.ip;
        document.getElementById('final-pi-endpoint').textContent = `http://${selectedPi.ip}:${selectedPi.port}`;
        
        // Save Pi configuration
        const apiKey = document.getElementById('api-key').value.trim();
        ipcRenderer.invoke('save-pi-config', {
            ip: selectedPi.ip,
            port: selectedPi.port,
            apiKey: apiKey
        });
    }
    
    // Close setup window
    ipcRenderer.send('pi-setup-complete');
    window.close();
}

// Notification system
function showNotification(message, type = 'info') {
    const notification = document.createElement('div');
    notification.className = `notification ${type}`;
    notification.textContent = message;
    notification.style.cssText = `
        position: fixed;
        top: 20px;
        right: 20px;
        padding: 12px 20px;
        border-radius: 4px;
        color: white;
        font-weight: bold;
        z-index: 1000;
        max-width: 300px;
        word-wrap: break-word;
    `;
    
    switch (type) {
        case 'success':
            notification.style.backgroundColor = '#4CAF50';
            break;
        case 'error':
            notification.style.backgroundColor = '#f44336';
            break;
        case 'warning':
            notification.style.backgroundColor = '#ff9800';
            break;
        default:
            notification.style.backgroundColor = '#2196F3';
    }
    
    document.body.appendChild(notification);
    
    setTimeout(() => {
        notification.remove();
    }, 5000);
}

// Initialize
document.addEventListener('DOMContentLoaded', () => {
    updateProgress();
});