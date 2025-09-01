@echo off
REM FyteClub Gaming PC Setup Script
REM Complete installation and configuration for gaming PCs

title FyteClub PC Setup
echo.
echo ===============================================
echo [*] FyteClub Gaming PC Setup
echo ===============================================
echo Friend-to-friend mod sharing server for FFXIV
echo.

REM Check if Node.js is installed
echo [1/6] Checking Node.js installation...
node --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [X] ERROR: Node.js not found
    echo.
    echo Please install Node.js first:
    echo 1. Go to https://nodejs.org
    echo 2. Download and install the LTS version
    echo 3. Restart this script
    echo.
    pause
    exit /b 1
)
for /f "tokens=*" %%i in ('node --version') do set NODE_VERSION=%%i
echo [OK] Node.js %NODE_VERSION% found

REM Check if we're in the correct directory
echo [2/6] Checking project structure...
if not exist "server\package.json" (
    echo [X] ERROR: Cannot find server\package.json
    echo Please run this script from the FyteClub root directory
    pause
    exit /b 1
)
echo [OK] Project structure verified

REM Install server dependencies
echo [3/6] Installing server dependencies...
cd server
call npm install --silent
if %errorlevel% neq 0 (
    echo [X] ERROR: Failed to install dependencies
    echo Check your internet connection and try again
    pause
    exit /b 1
)
echo [OK] Dependencies installed successfully

REM Verify start script exists (don't overwrite the enhanced one)
echo [4/6] Configuring startup script...
if not exist "start-fyteclub.bat" (
    echo Creating default startup script...
    echo @echo off > start-fyteclub.bat
    echo title FyteClub Server >> start-fyteclub.bat
    echo echo Starting FyteClub Server... >> start-fyteclub.bat
    echo node src/server.js --name "%COMPUTERNAME% FyteClub Server" >> start-fyteclub.bat
    echo pause >> start-fyteclub.bat
)
echo [OK] Startup script ready

REM Create desktop shortcut
echo [5/6] Creating desktop shortcut...
powershell -Command "try { $WshShell = New-Object -comObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\FyteClub Server.lnk'); $Shortcut.TargetPath = '%CD%\start-fyteclub.bat'; $Shortcut.WorkingDirectory = '%CD%'; $Shortcut.Description = 'FyteClub Friend-to-Friend Mod Sharing Server'; $Shortcut.Save(); Write-Host 'Desktop shortcut created' } catch { Write-Host 'Could not create desktop shortcut' }"
cd ..

REM Get network information
echo [6/6] Getting network information...
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4 Address" ^| findstr /v "127.0.0.1"') do set LOCAL_IP=%%a
if defined LOCAL_IP (
    set LOCAL_IP=%LOCAL_IP: =%
) else (
    set LOCAL_IP=localhost
)

REM Get public IP (optional)
echo Checking public IP...
powershell -Command "try { $publicIP = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 5); Write-Host 'Public IP: '$publicIP } catch { Write-Host 'Could not determine public IP' }" 2>nul

echo.
echo ===============================================
echo [*] FyteClub PC Setup Complete!
echo ===============================================
echo.
echo [i] Server Information:
echo   Computer Name: %COMPUTERNAME%
echo   Local IP: %LOCAL_IP%
echo   Port: 3000
echo   Cache: Memory fallback (Redis optional for better performance)
echo.
echo [i] Connection URLs:
echo   Local Network: http://%LOCAL_IP%:3000
echo   Health Check: http://%LOCAL_IP%:3000/health
echo.
echo [i] How to Start:
echo   - Double-click "FyteClub Server" on desktop
echo   - Or run: server\start-fyteclub.bat
echo.
echo [!] Router Setup (for friends outside your network):
echo   1. Log into your router admin panel
echo   2. Set up port forwarding:
echo      External Port: 3000
echo      Internal IP: %LOCAL_IP%
echo      Internal Port: 3000
echo   3. Share your public IP with friends
echo.
echo [?] Troubleshooting:
echo   - Test local: http://localhost:3000/health
echo   - Check firewall: Allow port 3000
echo   - Check antivirus: Whitelist FyteClub folder
echo   - Redis setup: Server works without Redis (uses memory cache)
echo   - Performance: Install Redis via Docker for better caching
echo.
echo [#] Security Setup:
echo [?] Do you want to set a password for your server? (Y/N)
set /p password_choice="Enter choice: "
set SERVER_PASSWORD=
if /i "%password_choice%"=="Y" (
    echo.
    echo Enter a password for your FyteClub server:
    echo Note: This password will be required for friends to connect
    set /p SERVER_PASSWORD="Password: "
    echo.
    echo [OK] Password protection enabled
) else (
    echo.
    echo [!] No password set - server will be open to anyone who can connect
)

echo.
echo [!] Redis Cache Setup (Optional - Recommended for 20+ users):
echo Redis dramatically improves performance but requires additional setup.
echo Without Redis, the server uses memory caching (still works great for small groups).
echo.
echo Redis Installation Options for Windows:
echo   1. Docker Desktop - Easiest, current Redis version
echo   2. WSL2 + Ubuntu Redis - Best performance, current Redis version
echo   3. Skip Redis - Use memory cache fallback
echo.
echo [?] Which Redis option do you prefer? (1-3)
set /p redis_choice="Enter choice (1-3): "
if "%redis_choice%"=="1" (
    echo.
    echo [INFO] Docker Desktop Redis Setup:
    echo.
    
    REM Check if Docker is running
    docker --version >nul 2>&1
    if %ERRORLEVEL% EQU 0 (
        echo [DETECTED] Docker is available
        
        REM Check if Docker Desktop is running
        docker ps >nul 2>&1
        if %ERRORLEVEL% EQU 0 (
            echo [RUNNING] Docker Desktop is running
            
            REM Check if FyteClub Redis container already exists and is running
            docker ps --filter "name=fyteclub-redis" --format "{{.Names}}" | findstr "fyteclub-redis" >nul 2>&1
            if %ERRORLEVEL% EQU 0 (
                echo [FOUND] FyteClub Redis container is already running!
                echo [TEST] Testing existing Redis connection...
                docker exec fyteclub-redis redis-cli ping >nul 2>&1
                if %ERRORLEVEL% EQU 0 (
                    echo [OK] Existing Redis is working perfectly!
                    echo [SKIP] No need to create new container
                ) else (
                    echo [WARNING] Existing Redis not responding, restarting...
                    docker restart fyteclub-redis
                    timeout /t 3 /nobreak >nul
                    docker exec fyteclub-redis redis-cli ping >nul 2>&1
                    if %ERRORLEVEL% EQU 0 (
                        echo [OK] Redis restarted successfully
                    ) else (
                        echo [ERROR] Redis restart failed
                    )
                )
            ) else (
                REM Check if container exists but is stopped
                docker ps -a --filter "name=fyteclub-redis" --format "{{.Names}}" | findstr "fyteclub-redis" >nul 2>&1
                if %ERRORLEVEL% EQU 0 (
                    echo [FOUND] FyteClub Redis container exists but is stopped
                    echo [START] Starting existing container...
                    docker start fyteclub-redis
                    if %ERRORLEVEL% EQU 0 (
                        echo [SUCCESS] Existing Redis container started!
                        timeout /t 3 /nobreak >nul
                        docker exec fyteclub-redis redis-cli ping >nul 2>&1
                        if %ERRORLEVEL% EQU 0 (
                            echo [OK] Redis is responding to ping
                        ) else (
                            echo [WARNING] Redis may still be starting up
                        )
                    ) else (
                        echo [ERROR] Failed to start existing container, creating new one...
                        docker rm fyteclub-redis >nul 2>&1
                        docker run -d --name fyteclub-redis -p 6379:6379 redis:alpine
                    )
                ) else (
                    echo [CREATE] Creating new FyteClub Redis container...
                    docker run -d --name fyteclub-redis -p 6379:6379 redis:alpine
                    if %ERRORLEVEL% EQU 0 (
                        echo [SUCCESS] Redis container created successfully!
                        echo [TEST] Testing Redis connection...
                        timeout /t 3 /nobreak >nul
                        docker exec fyteclub-redis redis-cli ping >nul 2>&1
                        if %ERRORLEVEL% EQU 0 (
                            echo [OK] Redis is responding to ping
                        ) else (
                            echo [WARNING] Redis may still be starting up
                        )
                    ) else (
                        echo [ERROR] Failed to create Redis container
                    )
                )
            )
        ) else (
            echo [NOT RUNNING] Docker Desktop is not running
            echo.
            echo [ACTION] Opening Docker Desktop...
            start "" "C:\Program Files\Docker\Docker\Docker Desktop.exe" 2>nul || (
                echo [INFO] Could not auto-start Docker Desktop
                echo Please manually start Docker Desktop and then run:
                echo    docker run -d --name fyteclub-redis -p 6379:6379 redis:alpine
            )
            echo.
            echo [WAIT] Waiting for Docker Desktop to start...
            echo Press any key after Docker Desktop is running to continue...
            pause >nul
            goto :retry_docker
        )
    ) else (
        echo [NOT INSTALLED] Docker is not installed
        echo.
        echo [DOWNLOAD] Opening Docker Desktop download page...
        start https://docker.com/products/docker-desktop
        echo.
        echo After installing Docker Desktop:
        echo 1. Start Docker Desktop
        echo 2. Open command prompt and run:
        echo    docker run -d --name fyteclub-redis -p 6379:6379 redis:alpine
        echo 3. Restart your FyteClub server to use Redis
    )
    echo.
    echo [OK] Docker Redis setup completed
    
    :retry_docker
    if "%redis_choice%"=="1" (
        docker ps >nul 2>&1
        if %ERRORLEVEL% NEQ 0 (
            echo [RETRY] Docker still not ready. Try starting Redis manually:
            echo    docker run -d --name fyteclub-redis -p 6379:6379 redis:alpine
        ) else (
            echo [RETRY] Docker is now ready. Starting Redis...
            docker stop fyteclub-redis >nul 2>&1
            docker rm fyteclub-redis >nul 2>&1
            docker run -d --name fyteclub-redis -p 6379:6379 redis:alpine
            if %ERRORLEVEL% EQU 0 (
                echo [SUCCESS] Redis container started after retry!
            )
        )
    )
) else if "%redis_choice%"=="2" (
    echo.
    echo [INFO] WSL2 Redis Setup:
    echo.
    echo 1. Enable WSL2: wsl --install
    echo 2. Install Ubuntu from Microsoft Store
    echo 3. In Ubuntu terminal run:
    echo    sudo apt update ^&^& sudo apt install redis-server
    echo    sudo service redis-server start
    echo 4. Restart your FyteClub server to use Redis
    echo.
    echo [OK] WSL2 Redis instructions provided
) else (
    echo.
    echo [OK] Using memory cache fallback - Redis can be added later
    echo     Server will work perfectly for small to medium groups
)

echo.
echo [?] Start server now? (Y/N)
set /p choice="Enter choice: "
if /i "%choice%"=="Y" (
    echo.
    echo Starting FyteClub server...
    if defined SERVER_PASSWORD (
        start "FyteClub Server" /d "%CD%\server" cmd /c "node src/server.js --name \"%COMPUTERNAME% FyteClub Server\" --password \"%SERVER_PASSWORD%\" & pause"
    ) else (
        start "FyteClub Server" /d "%CD%\server" start-fyteclub.bat
    )
    echo [OK] Server started in new window
    echo.
    echo Keep that window open while friends are connected!
    echo Press any key to close this setup window...
) else (
    echo.
    echo [OK] Setup complete! Start the server when ready:
    if defined SERVER_PASSWORD (
        echo   - Manual with password: node src/server.js --name "%COMPUTERNAME% FyteClub Server" --password "%SERVER_PASSWORD%"
    ) else (
        echo   - Desktop shortcut: "FyteClub Server"
        echo   - Manual: server\start-fyteclub.bat
    )
    echo.
    echo Press any key to close...
)
pause > nul