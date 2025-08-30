#!/usr/bin/env node

const { spawn } = require('child_process');
const path = require('path');

async function testIntegration() {
    console.log('🧪 FyteClub Integration Test\n');
    
    console.log('1. Starting server...');
    const server = spawn('node', ['src/server.js'], {
        cwd: path.join(__dirname, 'server'),
        stdio: 'pipe'
    });
    
    // Wait for server to start
    await new Promise(resolve => setTimeout(resolve, 2000));
    
    console.log('2. Starting client...');
    const client = spawn('node', ['bin/fyteclub.js'], {
        cwd: path.join(__dirname, 'client'),
        stdio: 'pipe'
    });
    
    // Wait for client to start
    await new Promise(resolve => setTimeout(resolve, 2000));
    
    console.log('3. Testing server API...');
    try {
        const fetch = require('node-fetch');
        const response = await fetch('http://localhost:3000/api/status');
        const status = await response.json();
        console.log(`✅ Server responding: ${status.name}`);
    } catch (error) {
        console.log(`❌ Server test failed: ${error.message}`);
    }
    
    console.log('4. Testing client status...');
    const statusClient = spawn('node', ['bin/fyteclub.js', 'status'], {
        cwd: path.join(__dirname, 'client'),
        stdio: 'pipe'
    });
    
    statusClient.stdout.on('data', (data) => {
        console.log(`✅ Client status: ${data.toString().trim()}`);
    });
    
    // Clean up
    setTimeout(() => {
        console.log('\n🧹 Cleaning up...');
        server.kill();
        client.kill();
        statusClient.kill();
        console.log('✅ Integration test complete');
        process.exit(0);
    }, 5000);
}

testIntegration().catch(error => {
    console.error('❌ Integration test failed:', error.message);
    process.exit(1);
});