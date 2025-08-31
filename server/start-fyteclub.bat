@echo off 
echo [*] Starting FyteClub server... 
echo [HELP] To stop server: Press Ctrl+C 
echo [HELP] To close window: Press Ctrl+C then any key 
echo. 
taskkill /f /im node.exe /fi "WINDOWTITLE eq FyteClub*" 2>nul 
cd /d "c:\Users\Me\git\fyteclub\server" 
node bin/fyteclub-server.js --name "CHRIS Server" 
echo. 
echo [INFO] Server stopped. Press any key to close... 
pause 
