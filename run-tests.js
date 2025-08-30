#!/usr/bin/env node

const { spawn } = require('child_process');
const path = require('path');

async function runTests() {
    console.log('🧪 Running FyteClub Test Suite\n');
    
    const testDirs = [
        { name: 'Server', dir: 'server' },
        { name: 'Client', dir: 'client' }
    ];
    
    let totalPassed = 0;
    let totalFailed = 0;
    
    for (const { name, dir } of testDirs) {
        console.log(`📋 Testing ${name}...`);
        
        try {
            const result = await runTestInDir(dir);
            console.log(`✅ ${name}: ${result.passed} passed, ${result.failed} failed\n`);
            totalPassed += result.passed;
            totalFailed += result.failed;
        } catch (error) {
            console.log(`❌ ${name}: Test run failed - ${error.message}\n`);
            totalFailed++;
        }
    }
    
    console.log('📊 Test Summary:');
    console.log(`✅ Total Passed: ${totalPassed}`);
    console.log(`❌ Total Failed: ${totalFailed}`);
    
    if (totalFailed > 0) {
        console.log('\n⚠️  Some tests failed. Check output above for details.');
        process.exit(1);
    } else {
        console.log('\n🎉 All tests passed!');
    }
}

function runTestInDir(dir) {
    return new Promise((resolve, reject) => {
        const isWindows = process.platform === 'win32';
        const npmCmd = isWindows ? 'npm.cmd' : 'npm';
        
        const testProcess = spawn(npmCmd, ['test'], {
            cwd: path.join(__dirname, dir),
            stdio: 'pipe',
            shell: isWindows
        });
        
        let output = '';
        let passed = 0;
        let failed = 0;
        
        testProcess.stdout.on('data', (data) => {
            output += data.toString();
        });
        
        testProcess.stderr.on('data', (data) => {
            output += data.toString();
        });
        
        testProcess.on('close', (code) => {
            // Parse Jest output for pass/fail counts
            const testSuitesMatch = output.match(/Test Suites: (\d+) passed(?:, (\d+) failed)?/);
            const testsMatch = output.match(/Tests:\s+(\d+) passed(?:, (\d+) failed)?/);
            
            if (testsMatch) {
                passed = parseInt(testsMatch[1]) || 0;
                failed = parseInt(testsMatch[2]) || 0;
            }
            
            if (code === 0) {
                resolve({ passed, failed });
            } else {
                reject(new Error(`Exit code ${code}`));
            }
        });
        
        testProcess.on('error', (error) => {
            reject(error);
        });
    });
}

if (require.main === module) {
    runTests().catch(error => {
        console.error('Test runner error:', error.message);
        process.exit(1);
    });
}