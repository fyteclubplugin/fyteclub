#!/usr/bin/env node

const { spawn } = require('child_process');
const path = require('path');

async function analyzeCoverage() {
    console.log('📊 FyteClub Test Coverage Analysis\n');
    
    const components = [
        { name: 'Server', dir: 'server', target: 80 },
        { name: 'Client', dir: 'client', target: 75 }
    ];
    
    for (const { name, dir, target } of components) {
        console.log(`🔍 Analyzing ${name} Coverage...`);
        
        try {
            const coverage = await getCoverage(dir);
            const status = coverage >= target ? '✅' : '⚠️';
            
            console.log(`${status} ${name}: ${coverage}% (target: ${target}%)`);
            
            if (coverage < target) {
                console.log(`   📈 Need ${target - coverage}% more coverage`);
            }
            
        } catch (error) {
            console.log(`❌ ${name}: Coverage analysis failed`);
        }
        
        console.log('');
    }
    
    console.log('🎯 Coverage Priorities:');
    console.log('1. Encryption system (security critical)');
    console.log('2. Database operations (data integrity)');
    console.log('3. Share code system (core functionality)');
    console.log('4. Error handling (reliability)');
    console.log('5. Integration flows (end-to-end)');
}

function getCoverage(dir) {
    return new Promise((resolve, reject) => {
        const testProcess = spawn('npm', ['test', '--', '--coverage', '--silent'], {
            cwd: path.join(__dirname, dir),
            stdio: 'pipe'
        });
        
        let output = '';
        
        testProcess.stdout.on('data', (data) => {
            output += data.toString();
        });
        
        testProcess.on('close', (code) => {
            // Extract coverage percentage from Jest output
            const coverageMatch = output.match(/All files\s+\|\s+([\d.]+)/);
            
            if (coverageMatch) {
                resolve(parseFloat(coverageMatch[1]));
            } else {
                reject(new Error('Could not parse coverage'));
            }
        });
        
        testProcess.on('error', reject);
    });
}

if (require.main === module) {
    analyzeCoverage().catch(console.error);
}

module.exports = { analyzeCoverage };