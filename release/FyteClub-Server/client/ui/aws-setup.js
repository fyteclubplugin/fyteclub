const { ipcRenderer } = require('electron');

let currentStep = 1;
const totalSteps = 5;

function up            // Save configuration to FyteClub
            await ipcRenderer.invoke('save-aws-config', outputs);
            
            addLog('Configuration saved to FyteClub');Progress() {
    const progress = (currentStep / totalSteps) * 100;
    document.getElementById('progress').style.width = `${progress}%`;
}

function nextStep() {
    if (currentStep < totalSteps) {
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

async function checkPrerequisites() {
    const awsCheck = document.getElementById('aws-cli-check');
    const terraformCheck = document.getElementById('terraform-check');
    const missingTools = document.getElementById('missing-tools');
    const continueBtn = document.getElementById('continue-deploy');
    
    // Check AWS CLI
    awsCheck.querySelector('.status-icon').textContent = '⏳';
    const hasAWS = await ipcRenderer.invoke('check-aws-cli');
    
    if (hasAWS) {
        awsCheck.classList.add('met');
        awsCheck.querySelector('.status-icon').textContent = '✅';
    } else {
        awsCheck.classList.add('failed');
        awsCheck.querySelector('.status-icon').textContent = '❌';
    }
    
    // Check Terraform
    terraformCheck.querySelector('.status-icon').textContent = '⏳';
    const hasTerraform = await ipcRenderer.invoke('check-terraform');
    
    if (hasTerraform) {
        terraformCheck.classList.add('met');
        terraformCheck.querySelector('.status-icon').textContent = '✅';
    } else {
        terraformCheck.classList.add('failed');
        terraformCheck.querySelector('.status-icon').textContent = '❌';
    }
    
    // Show results
    if (hasAWS && hasTerraform) {
        continueBtn.style.display = 'inline-block';
        missingTools.style.display = 'none';
    } else {
        missingTools.style.display = 'block';
        continueBtn.style.display = 'none';
    }
}

async function deployInfrastructure() {
    const log = document.getElementById('deployment-log');
    const startBtn = document.getElementById('start-deploy');
    const backBtn = document.getElementById('back-deploy');
    const continueBtn = document.getElementById('continue-final');
    
    startBtn.style.display = 'none';
    backBtn.style.display = 'none';
    
    const region = document.getElementById('aws-region').value;
    
    function addLog(message) {
        log.textContent += `${new Date().toLocaleTimeString()}: ${message}\n`;
        log.scrollTop = log.scrollHeight;
    }
    
    try {
        addLog('Starting deployment...');
        addLog(`Deploying to region: ${region}`);
        
        const result = await ipcRenderer.invoke('deploy-infrastructure', region);
        
        if (result.success) {
            addLog('✅ Infrastructure deployed successfully!');
            addLog('Getting configuration details...');
            
            const outputs = await ipcRenderer.invoke('get-terraform-outputs');
            
            // Update final step with results
            document.getElementById('final-endpoint').textContent = outputs.apiEndpoint;
            document.getElementById('final-region').textContent = outputs.region;
            document.getElementById('final-bucket').textContent = outputs.s3Bucket;
            
            // Save configuration to StallionSync
            await ipcRenderer.invoke('save-aws-config', outputs);
            
            addLog('✅ Configuration saved to StallionSync');
            addLog('Setup complete! Click Continue to finish.');
            
            continueBtn.style.display = 'inline-block';
        } else {
            addLog(`❌ Deployment failed: ${result.message}`);
            addLog('Please check the error and try again.');
            startBtn.style.display = 'inline-block';
            backBtn.style.display = 'inline-block';
        }
    } catch (error) {
        addLog(`❌ Error: ${error.message}`);
        startBtn.style.display = 'inline-block';
        backBtn.style.display = 'inline-block';
    }
}

function finishSetup() {
    // Close setup window and return to main app
    ipcRenderer.send('setup-complete');
    window.close();
}

// Auto-check prerequisites when step 2 loads
document.addEventListener('DOMContentLoaded', () => {
    updateProgress();
});