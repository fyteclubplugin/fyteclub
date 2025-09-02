@echo off
REM FyteClub Version Update Script
REM Updates all version references across the entire repository

if "%1"=="" (
    echo Usage: update-version.bat [NEW_VERSION]
    echo Example: update-version.bat 3.1.1
    echo.
    echo This script will update version numbers in:
    echo   - VERSION file
    echo   - All package.json files
    echo   - Plugin .csproj file
    echo   - Plugin manifest files ^(FyteClub.json, repo.json^)
    echo   - Documentation files
    echo.
    pause
    exit /b 1
)

set NEW_VERSION=%1
echo.
echo ===============================================
echo 🔄 FyteClub Version Update to %NEW_VERSION%
echo ===============================================
echo.

REM Read current version
if exist VERSION (
    set /p CURRENT_VERSION=<VERSION
    echo Current version: %CURRENT_VERSION%
) else (
    echo WARNING: VERSION file not found
    set CURRENT_VERSION=unknown
)

echo New version: %NEW_VERSION%
echo.

REM Ask for confirmation
set /p CONFIRM="Update all files to v%NEW_VERSION%? (Y/N): "
if /i not "%CONFIRM%"=="Y" (
    echo Update cancelled
    exit /b 0
)

echo.
echo [1/7] Updating VERSION file...
echo %NEW_VERSION% > VERSION
echo [OK] VERSION file updated

echo [2/7] Updating server package.json...
powershell -Command "(Get-Content 'server\package.json') -replace '\"version\": \".*\"', '\"version\": \"%NEW_VERSION%\"' | Set-Content 'server\package.json'"
echo [OK] server/package.json updated

echo [3/7] Updating client package.json...
powershell -Command "(Get-Content 'client\package.json') -replace '\"version\": \".*\"', '\"version\": \"%NEW_VERSION%\"' | Set-Content 'client\package.json'"
echo ✅ client/package.json updated

echo [4/7] Updating plugin .csproj file...
powershell -Command "(Get-Content 'plugin\FyteClub.csproj') -replace '<Version>.*</Version>', '<Version>%NEW_VERSION%</Version>' | Set-Content 'plugin\FyteClub.csproj'"
echo ✅ plugin/FyteClub.csproj updated

echo [5/7] Updating plugin manifest ^(FyteClub.json^)...
powershell -Command "(Get-Content 'plugin\FyteClub.json') -replace '\"AssemblyVersion\": \".*\"', '\"AssemblyVersion\": \"%NEW_VERSION%\"' | Set-Content 'plugin\FyteClub.json'"
echo ✅ plugin/FyteClub.json updated

echo [6/7] Updating plugin repo manifest ^(repo.json^)...
powershell -Command "(Get-Content 'plugin\repo.json') -replace '\"AssemblyVersion\": \".*\"', '\"AssemblyVersion\": \"%NEW_VERSION%\"' | Set-Content 'plugin\repo.json'"
echo ✅ plugin/repo.json updated

echo [7/7] Updating documentation version references...
REM Update README.md version references
powershell -Command "if (Test-Path 'README.md') { (Get-Content 'README.md') -replace 'v[0-9]+\.[0-9]+\.[0-9]+', 'v%NEW_VERSION%' | Set-Content 'README.md' }"
REM Update release notes
powershell -Command "if (Test-Path 'RELEASE_NOTES.md') { (Get-Content 'RELEASE_NOTES.md') -replace '## FyteClub v[0-9]+\.[0-9]+\.[0-9]+', '## FyteClub v%NEW_VERSION%' | Set-Content 'RELEASE_NOTES.md' }"
echo ✅ Documentation updated

echo.
echo ===============================================
echo [*] Version Update Complete!
echo ===============================================
echo.
echo Updated from: %CURRENT_VERSION%
echo Updated to: %NEW_VERSION%
echo.
echo 📁 Files updated:
echo   • VERSION
echo   • server/package.json
echo   • client/package.json  
echo   • plugin/FyteClub.csproj
echo   • plugin/FyteClub.json
echo   • plugin/repo.json
echo   • Documentation files
echo.
echo [>] Next steps:
echo   1. Review changes: git diff
echo   2. Test build: build-pc.bat or npm test
echo   3. Commit: git add . ^&^& git commit -m "Release v%NEW_VERSION%"
echo   4. Tag: git tag -a v%NEW_VERSION% -m "FyteClub v%NEW_VERSION%"
echo   5. Push: git push ^&^& git push origin v%NEW_VERSION%
echo.
pause
