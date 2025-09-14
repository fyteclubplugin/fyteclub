#!/usr/bin/env node

const { spawn } = require('child_process');
const path = require('path');

async function runComprehensiveTests() {
    console.log('🧪 FyteClub v3.0.0 - Comprehensive Test Suite\n');
    
    const testSuites = [
        {
            name: 'Server - Core Services',
            dir: 'server',
            command: 'npx jest --config=jest.stable.config.json'
        },
        {
            name: 'Client - All Components', 
            dir: 'client',
            command: 'npm test'
        }
    ];
    
    let totalPassed = 0;
    let totalFailed = 0;
    let totalSuites = 0;
    
    for (const suite of testSuites) {
        console.log(`📋 Testing ${suite.name}...`);
        
        try {
            const result = await runTestCommand(suite.dir, suite.command);
            console.log(`✅ ${suite.name}: ${result.passed} passed, ${result.failed} failed`);
            console.log(`   Test Suites: ${result.suites} passed\n`);
            
            totalPassed += result.passed;
            totalFailed += result.failed;
            totalSuites += result.suites;
        } catch (error) {
            console.log(`❌ ${suite.name}: Test execution failed - ${error.message}\n`);
            totalFailed++;
        }
    }
    
    console.log('='.repeat(60));
    console.log('📊 COMPREHENSIVE TEST SUMMARY');
    console.log('='.repeat(60));
    console.log(`✅ Total Tests Passed: ${totalPassed}`);
    console.log(`❌ Total Tests Failed: ${totalFailed}`);
    console.log(`📦 Total Test Suites: ${totalSuites}`);
    
    const coverage = totalPassed > 0 ? ((totalPassed / (totalPassed + totalFailed)) * 100).toFixed(1) : 0;
    console.log(`📈 Test Success Rate: ${coverage}%`);
    
    if (totalFailed === 0) {
        console.log('\n🎉 ALL TESTS PASSED! Ready for v3.0.0 release!');
        console.log('\n🚀 New Features Tested:');
        console.log('   • Storage Deduplication with SHA-256 hashing');
        console.log('   • Redis Caching with memory fallback');
        console.log('   • Enhanced Database Service');
        console.log('   • Client Server Management');
        console.log('   • Encryption Services');
        process.exit(0);
    } else {
        console.log(`\n⚠️  ${totalFailed} test(s) failed. Review output above.`);
        process.exit(1);
    }
}

function runTestCommand(dir, command) {
    return new Promise((resolve, reject) => {
        const isWindows = process.platform === 'win32';
        const parts = command.split(' ');
        const cmd = parts[0];
        const args = parts.slice(1);
        
        const testProcess = spawn(cmd, args, {
            cwd: path.join(__dirname, dir),
            stdio: 'pipe',
            shell: isWindows
        });
        
        let output = '';
        
        testProcess.stdout.on('data', (data) => {
            output += data.toString();
        });
        
        testProcess.stderr.on('data', (data) => {
            output += data.toString();
        });
        
        testProcess.on('close', (code) => {
            // Parse Jest output
            const testsMatch = output.match(/Tests:\\s+([0-9]+) passed(?:, ([0-9]+) failed)?/);
            const suitesMatch = output.match(/Test Suites: ([0-9]+) passed/);
            
            let passed = 0;
            let failed = 0;
            let suites = 0;
            
            if (testsMatch) {
                passed = parseInt(testsMatch[1]) || 0;
                failed = parseInt(testsMatch[2]) || 0;
            }
            
            if (suitesMatch) {
                suites = parseInt(suitesMatch[1]) || 0;
            }
            
            if (code === 0 || passed > 0) {
                resolve({ passed, failed, suites });
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
    runComprehensiveTests().catch(error => {
        console.error('❌ Test runner error:', error.message);
        process.exit(1);
    });
}

module.exports = { runComprehensiveTests };
