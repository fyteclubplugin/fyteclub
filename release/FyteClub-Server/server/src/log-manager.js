const fs = require('fs');
const path = require('path');

class LogManager {
    constructor(logDir = './logs', maxLogFiles = 3) {
        this.logDir = path.resolve(logDir);
        this.maxLogFiles = maxLogFiles;
        this.logFile = null;
        this.startTime = new Date();
        this.sessionLogs = [];
        
        // Store original console methods FIRST
        this.originalConsoleLog = console.log;
        this.originalConsoleError = console.error;
        this.originalConsoleWarn = console.warn;
        
        this.ensureLogDirectory();
        this.rotateLogFiles();
        this.createNewLogFile();
        
        // Override console methods AFTER setting up logging
        console.log = (...args) => this.log('INFO', ...args);
        console.error = (...args) => this.log('ERROR', ...args);
        console.warn = (...args) => this.log('WARN', ...args);
    }
    
    ensureLogDirectory() {
        if (!fs.existsSync(this.logDir)) {
            fs.mkdirSync(this.logDir, { recursive: true });
        }
    }
    
    rotateLogFiles() {
        try {
            // Get all log files
            const files = fs.readdirSync(this.logDir)
                .filter(file => file.startsWith('fyteclub-') && file.endsWith('.log'))
                .map(file => ({
                    name: file,
                    path: path.join(this.logDir, file),
                    mtime: fs.statSync(path.join(this.logDir, file)).mtime
                }))
                .sort((a, b) => b.mtime - a.mtime); // Newest first
            
            // Remove old log files if we have too many
            if (files.length >= this.maxLogFiles) {
                const filesToDelete = files.slice(this.maxLogFiles - 1); // Keep maxLogFiles - 1 (making room for new one)
                
                for (const file of filesToDelete) {
                    try {
                        fs.unlinkSync(file.path);
                        this.originalConsoleLog(`📁 Deleted old log file: ${file.name}`);
                    } catch (err) {
                        this.originalConsoleError(`❌ Failed to delete log file ${file.name}:`, err.message);
                    }
                }
            }
        } catch (err) {
            this.originalConsoleError('❌ Failed to rotate log files:', err.message);
        }
    }
    
    createNewLogFile() {
        const timestamp = this.startTime.toISOString()
            .replace(/:/g, '-')
            .replace(/\./g, '-')
            .substring(0, 19); // YYYY-MM-DDTHH-MM-SS
        
        const logFileName = `fyteclub-${timestamp}.log`;
        this.logFile = path.join(this.logDir, logFileName);
        
        // Write session header
        const header = `
=================================================================
🥊 FyteClub Server Log - Session Started
=================================================================
Start Time: ${this.startTime.toISOString()}
Platform: ${process.platform} (${process.arch})
Node.js: ${process.version}
PID: ${process.pid}
Working Directory: ${process.cwd()}
Log File: ${logFileName}
=================================================================

`;
        
        try {
            fs.writeFileSync(this.logFile, header);
            this.originalConsoleLog(`📝 Logging to: ${this.logFile}`);
        } catch (err) {
            this.originalConsoleError(`❌ Failed to create log file: ${err.message}`);
            this.logFile = null;
        }
    }
    
    log(level, ...args) {
        const timestamp = new Date().toISOString();
        const message = args.map(arg => 
            typeof arg === 'object' ? JSON.stringify(arg, null, 2) : String(arg)
        ).join(' ');
        
        const logEntry = `[${timestamp}] [${level.padEnd(5)}] ${message}\n`;
        
        // Store in session logs
        if (this.sessionLogs) {
            this.sessionLogs.push({
                timestamp,
                level,
                message,
                entry: logEntry
            });
        }
        
        // Write to console (original behavior)
        switch (level) {
            case 'ERROR':
                this.originalConsoleError(...args);
                break;
            case 'WARN':
                this.originalConsoleWarn(...args);
                break;
            default:
                this.originalConsoleLog(...args);
                break;
        }
        
        // Write to log file
        if (this.logFile) {
            try {
                fs.appendFileSync(this.logFile, logEntry);
            } catch (err) {
                this.originalConsoleError(`❌ Failed to write to log file: ${err.message}`);
            }
        }
    }
    
    // Method to get log statistics
    getLogStats() {
        try {
            const files = fs.readdirSync(this.logDir)
                .filter(file => file.startsWith('fyteclub-') && file.endsWith('.log'))
                .map(file => {
                    const filePath = path.join(this.logDir, file);
                    const stats = fs.statSync(filePath);
                    return {
                        name: file,
                        size: stats.size,
                        sizeKB: Math.round(stats.size / 1024),
                        created: stats.birthtime,
                        modified: stats.mtime
                    };
                })
                .sort((a, b) => b.modified - a.modified);
            
            const totalSize = files.reduce((sum, file) => sum + file.size, 0);
            
            return {
                logDirectory: this.logDir,
                currentLogFile: path.basename(this.logFile || 'none'),
                totalLogFiles: files.length,
                totalSizeKB: Math.round(totalSize / 1024),
                files: files
            };
        } catch (err) {
            return {
                error: err.message,
                logDirectory: this.logDir,
                currentLogFile: path.basename(this.logFile || 'none')
            };
        }
    }
    
    // Method to read recent log entries
    getRecentLogs(lines = 50) {
        if (!this.logFile || !fs.existsSync(this.logFile)) {
            return [];
        }
        
        try {
            const content = fs.readFileSync(this.logFile, 'utf8');
            const logLines = content.split('\n').filter(line => line.trim());
            return logLines.slice(-lines);
        } catch (err) {
            return [`Error reading log file: ${err.message}`];
        }
    }
    
    // Method to clean up and restore console
    cleanup() {
        // Write session footer
        if (this.logFile) {
            const footer = `
=================================================================
🛑 FyteClub Server Log - Session Ended
=================================================================
End Time: ${new Date().toISOString()}
Session Duration: ${Math.round((Date.now() - this.startTime.getTime()) / 1000)}s
=================================================================

`;
            try {
                fs.appendFileSync(this.logFile, footer);
            } catch (err) {
                this.originalConsoleError(`❌ Failed to write log footer: ${err.message}`);
            }
        }
        
        // Restore original console methods
        console.log = this.originalConsoleLog;
        console.error = this.originalConsoleError;
        console.warn = this.originalConsoleWarn;
    }

    startSession() {
        // Start a new logging session
        this.sessionLogs = [];
        this.log('INFO', '🚀 FyteClub server session started');
    }

    endSession() {
        // End the current logging session
        this.log('INFO', '🛑 FyteClub server session ended');
    }

    getCurrentLogFile() {
        return this.logFile;
    }

    getLogStats() {
        return {
            sessionStart: this.startTime,
            logFile: this.logFile,
            entriesCount: this.sessionLogs ? this.sessionLogs.length : 0
        };
    }

    getCurrentLogs() {
        return {
            currentFile: this.currentLogFile,
            logs: this.sessionLogs,
            stats: this.getLogStats()
        };
    }

    getLogFiles() {
        try {
            const files = fs.readdirSync(this.logDir)
                .filter(file => file.endsWith('.log'))
                .map(file => {
                    const filePath = path.join(this.logDir, file);
                    const stats = fs.statSync(filePath);
                    return {
                        name: file,
                        size: stats.size,
                        created: stats.birthtime,
                        modified: stats.mtime
                    };
                })
                .sort((a, b) => b.modified - a.modified);
            
            return files;
        } catch (error) {
            console.error('Error reading log files:', error);
            return [];
        }
    }

    readLogFile(filename) {
        try {
            const filePath = path.join(this.logDir, filename);
            
            // Security check - ensure filename doesn't contain path traversal
            if (filename.includes('..') || filename.includes('/') || filename.includes('\\')) {
                throw new Error('Invalid filename');
            }
            
            if (!fs.existsSync(filePath)) {
                throw new Error('Log file not found');
            }
            
            return fs.readFileSync(filePath, 'utf8');
        } catch (error) {
            throw new Error(`Failed to read log file: ${error.message}`);
        }
    }
}

module.exports = LogManager;
