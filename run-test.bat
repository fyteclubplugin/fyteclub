@echo off
echo Running FyteClub Server Test...
node test-server-standalone.js > test-results.txt 2>&1
echo Test completed. Results saved to test-results.txt
type test-results.txt
