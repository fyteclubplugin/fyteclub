// Jest setup file
process.env.NODE_ENV = 'test';

// Mock console.log to reduce test output noise
const originalConsoleLog = console.log;
global.console = {
    ...console,
    log: (...args) => {
        // Only log errors and important test messages
        if (args.some(arg => typeof arg === 'string' && 
            (arg.includes('error') || arg.includes('Error') || arg.includes('FAIL')))) {
            originalConsoleLog.apply(console, args);
        }
    }
};
