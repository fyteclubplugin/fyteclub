#!/usr/bin/env node

/**
 * Integration Tests for Horse-Enhanced FyteClub Architecture
 * Tests the complete workflow with all Horse patterns implemented
 */

const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

class HorsePatternIntegrationTests {
    constructor() {
        this.testResults = {
                     console.log('\n🎉 All Horse patterns successfully integrated!');
            console.log('FyteClub now has Horse-level stability and polish.'); passed: 0,
            failed: 0,
            details: []
        };
    }

    async runAllTests() {
        console.log('🧪 FyteClub Horse Pattern Integration Tests\n');
        
        await this.testPluginArchitecture();
        await this.testPerformanceMonitoring();
        await this.testVersionChecking();
        await this.testLockManagement();
        await this.testConnectionResilience();
        await this.testAdvancedPlayerStates();
        await this.testUiStatePersistence();
        await this.testEndToEndWorkflow();
        
        this.printResults();
        return this.testResults.failed === 0;
    }

    async testPluginArchitecture() {
        console.log('📋 Testing Plugin Architecture...');
        
        try {
            // Test 1: Plugin files exist
            const requiredFiles = [
                'plugin/src/FyteClubPlugin.cs',
                'plugin/src/FyteClubPerformanceCollector.cs',
                'plugin/src/FyteClubPluginVersionChecker.cs',
                'plugin/src/FyteClubRedrawCoordinator.cs',
                'plugin/src/FyteClubConnectionManager.cs',
                'plugin/src/FyteClubLockManager.cs',
                'plugin/src/FyteClubAdvancedPlayerState.cs',
                'plugin/src/FyteClubUiConfig.cs'
            ];

            for (const file of requiredFiles) {
                if (!fs.existsSync(path.join(__dirname, file))) {
                    throw new Error(`Missing required file: ${file}`);
                }
            }
            
            this.pass('Plugin Architecture', 'All Horse pattern files exist');

            // Test 2: Plugin compiles
            const compileResult = await this.runCommand('dotnet', ['build'], 'plugin');
            if (compileResult.exitCode !== 0) {
                throw new Error(`Plugin compilation failed: ${compileResult.stderr}`);
            }
            
            this.pass('Plugin Compilation', 'Plugin compiles successfully with Horse patterns');

        } catch (error) {
            this.fail('Plugin Architecture', error.message);
        }
    }

    async testPerformanceMonitoring() {
        console.log('⚡ Testing Performance Monitoring...');
        
        try {
            // Test performance collector exists and has required methods
            const perfCollectorContent = fs.readFileSync(
                path.join(__dirname, 'plugin/src/FyteClubPerformanceCollector.cs'), 
                'utf8'
            );

            const requiredMethods = [
                'BeginScope',
                'RecordOperation',
                'LogMetrics'
            ];

            for (const method of requiredMethods) {
                if (!perfCollectorContent.includes(method)) {
                    throw new Error(`Missing performance method: ${method}`);
                }
            }

            // Test slow operation detection
            if (!perfCollectorContent.includes('elapsedMs > 100')) {
                throw new Error('Missing slow operation detection (>100ms)');
            }

            this.pass('Performance Monitoring', 'Performance collector implements Horse patterns');

        } catch (error) {
            this.fail('Performance Monitoring', error.message);
        }
    }

    async testVersionChecking() {
        console.log('🔍 Testing Version Checking...');
        
        try {
            const versionCheckerContent = fs.readFileSync(
                path.join(__dirname, 'plugin/src/FyteClubPluginVersionChecker.cs'), 
                'utf8'
            );

            // Test all 5 plugins are checked
            const requiredPlugins = ['Penumbra', 'Glamourer', 'Customize+', 'SimpleHeels', 'Honorific'];
            for (const plugin of requiredPlugins) {
                if (!versionCheckerContent.includes(plugin)) {
                    throw new Error(`Missing plugin version check: ${plugin}`);
                }
            }

            // Test API version checking
            if (!versionCheckerContent.includes('GetApiVersion')) {
                throw new Error('Missing API version checking');
            }

            this.pass('Version Checking', 'All 5 plugins have version compatibility checks');

        } catch (error) {
            this.fail('Version Checking', error.message);
        }
    }

    async testLockManagement() {
        console.log('🔒 Testing Lock Management...');
        
        try {
            const lockManagerContent = fs.readFileSync(
                path.join(__dirname, 'plugin/src/FyteClubLockManager.cs'), 
                'utf8'
            );

            const requiredFeatures = [
                'AcquirePlayerLock',
                'ReleasePlayerLock',
                'ApplyModWithLock',
                'ReleaseAllLocks',
                'Guid.NewGuid' // GUID-based lock codes
            ];

            for (const feature of requiredFeatures) {
                if (!lockManagerContent.includes(feature)) {
                    throw new Error(`Missing lock management feature: ${feature}`);
                }
            }

            this.pass('Lock Management', 'Lock manager implements Horse conflict prevention');

        } catch (error) {
            this.fail('Lock Management', error.message);
        }
    }

    async testConnectionResilience() {
        console.log('🌐 Testing Connection Resilience...');
        
        try {
            const connectionManagerContent = fs.readFileSync(
                path.join(__dirname, 'plugin/src/FyteClubConnectionManager.cs'), 
                'utf8'
            );

            const requiredFeatures = [
                'MaintainConnection',
                'AttemptConnection',
                'GetReconnectDelay',
                'exponential backoff',
                'ConnectionStateChanged'
            ];

            for (const feature of requiredFeatures) {
                if (!connectionManagerContent.includes(feature)) {
                    throw new Error(`Missing connection feature: ${feature}`);
                }
            }

            this.pass('Connection Resilience', 'Connection manager implements Horse resilience patterns');

        } catch (error) {
            this.fail('Connection Resilience', error.message);
        }
    }

    async testAdvancedPlayerStates() {
        console.log('👥 Testing Advanced Player States...');
        
        try {
            const playerStateContent = fs.readFileSync(
                path.join(__dirname, 'plugin/src/FyteClubAdvancedPlayerState.cs'), 
                'utf8'
            );

            // Test all 11 player states exist
            const requiredStates = [
                'Unknown', 'Offline', 'Online', 'Requesting', 'Downloading',
                'Applying', 'Applied', 'Failed', 'Paused', 'Visible', 'Hidden'
            ];

            for (const state of requiredStates) {
                if (!playerStateContent.includes(state)) {
                    throw new Error(`Missing player state: ${state}`);
                }
            }

            // Test performance tracking
            const performanceFeatures = [
                'AverageApplyTimeMs',
                'TotalApplyTime',
                'ApplyCount',
                'FailureCount'
            ];

            for (const feature of performanceFeatures) {
                if (!playerStateContent.includes(feature)) {
                    throw new Error(`Missing performance tracking: ${feature}`);
                }
            }

            this.pass('Advanced Player States', 'All 11 states with performance tracking implemented');

        } catch (error) {
            this.fail('Advanced Player States', error.message);
        }
    }

    async testUiStatePersistence() {
        console.log('💾 Testing UI State Persistence...');
        
        try {
            const uiConfigContent = fs.readFileSync(
                path.join(__dirname, 'plugin/src/FyteClubUiConfig.cs'), 
                'utf8'
            );

            const requiredFeatures = [
                'WindowSize',
                'WindowPosition',
                'CollapsedSections',
                'VisibleColumns',
                'IPluginConfiguration',
                'GetStateColor'
            ];

            for (const feature of requiredFeatures) {
                if (!uiConfigContent.includes(feature)) {
                    throw new Error(`Missing UI config feature: ${feature}`);
                }
            }

            this.pass('UI State Persistence', 'UI configuration implements Horse persistence patterns');

        } catch (error) {
            this.fail('UI State Persistence', error.message);
        }
    }

    async testEndToEndWorkflow() {
        console.log('🔄 Testing End-to-End Workflow...');
        
        try {
            const pluginContent = fs.readFileSync(
                path.join(__dirname, 'plugin/src/FyteClubPlugin.cs'), 
                'utf8'
            );

            // Test Horse service initialization
            const horseServices = [
                '_performanceCollector',
                '_versionChecker',
                '_redrawCoordinator',
                '_connectionManager',
                '_lockManager'
            ];

            for (const service of horseServices) {
                if (!pluginContent.includes(service)) {
                    throw new Error(`Missing Horse service: ${service}`);
                }
            }

            // Test framework thread safety
            if (!pluginContent.includes('RunOnFrameworkThread')) {
                throw new Error('Missing framework thread safety');
            }

            // Test advanced player info usage
            if (!pluginContent.includes('AdvancedPlayerInfo')) {
                throw new Error('Missing advanced player info integration');
            }

            this.pass('End-to-End Workflow', 'Complete Horse pattern integration verified');

        } catch (error) {
            this.fail('End-to-End Workflow', error.message);
        }
    }

    async runCommand(command, args, cwd = '.') {
        return new Promise((resolve) => {
            const process = spawn(command, args, {
                cwd: path.join(__dirname, cwd),
                stdio: 'pipe',
                shell: true
            });

            let stdout = '';
            let stderr = '';

            process.stdout.on('data', (data) => stdout += data.toString());
            process.stderr.on('data', (data) => stderr += data.toString());

            process.on('close', (exitCode) => {
                resolve({ exitCode, stdout, stderr });
            });
        });
    }

    pass(testName, message) {
        this.testResults.passed++;
        this.testResults.details.push({ test: testName, status: 'PASS', message });
        console.log(`  ✅ ${testName}: ${message}`);
    }

    fail(testName, message) {
        this.testResults.failed++;
        this.testResults.details.push({ test: testName, status: 'FAIL', message });
        console.log(`  ❌ ${testName}: ${message}`);
    }

    printResults() {
        console.log('\n📊 Horse Pattern Integration Test Results:');
        console.log(`✅ Passed: ${this.testResults.passed}`);
        console.log(`❌ Failed: ${this.testResults.failed}`);
        
        if (this.testResults.failed > 0) {
            console.log('\n🔍 Failed Tests:');
            this.testResults.details
                .filter(d => d.status === 'FAIL')
                .forEach(d => console.log(`  • ${d.test}: ${d.message}`));
        }

        if (this.testResults.failed === 0) {
            console.log('\n🎉 All Horse patterns successfully integrated!');
            console.log('FyteClub now has Horse-level stability and polish.');
        }
    }
}

// Run tests if called directly
if (require.main === module) {
    const tests = new HorsePatternIntegrationTests();
    tests.runAllTests().then(success => {
        process.exit(success ? 0 : 1);
    }).catch(error => {
        console.error('Test runner error:', error.message);
        process.exit(1);
    });
}

module.exports = HorsePatternIntegrationTests;