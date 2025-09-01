@echo off
set /p VERSION=<VERSION
echo Creating FyteClub v%VERSION% Release Tag...
echo.

echo Updating all version references to %VERSION%...

REM Update client package.json
echo Updating client/package.json...
powershell -Command "(Get-Content 'client/package.json') -replace '\\\"version\\\": \\\"[^\\\"]*\\\"', '\\\"version\\\": \\\"%VERSION%\\\"' | Set-Content 'client/package.json'"

REM Update server package.json
echo Updating server/package.json...
powershell -Command "(Get-Content 'server/package.json') -replace '\\\"version\\\": \\\"[^\\\"]*\\\"', '\\\"version\\\": \\\"%VERSION%\\\"' | Set-Content 'server/package.json'"

REM Update package-lock.json files (first occurrence only - main project version)
echo Updating package-lock.json files...
if exist "client\package-lock.json" (
    powershell -Command "$content = Get-Content 'client/package-lock.json' -Raw; $content = $content -replace '(\\\"version\\\":\\s*\\\")[^\\\"]*(\\\",)', \\\"${1}%VERSION%${2}\\\", 1; Set-Content 'client/package-lock.json' $content"
)
if exist "server\package-lock.json" (
    powershell -Command "$content = Get-Content 'server/package-lock.json' -Raw; $content = $content -replace '(\\\"version\\\":\\s*\\\")[^\\\"]*(\\\",)', \\\"${1}%VERSION%${2}\\\", 1; Set-Content 'server/package-lock.json' $content"
)

REM Update plugin manifest
echo Updating plugin/FyteClub.json...
powershell -Command "(Get-Content 'plugin/FyteClub.json') -replace '\\\"AssemblyVersion\\\": \\\"[^\\\"]*\\\"', '\\\"AssemblyVersion\\\": \\\"%VERSION%\\\"' | Set-Content 'plugin/FyteClub.json'"

REM Update plugin csproj
echo Updating plugin/FyteClub.csproj...
powershell -Command "(Get-Content 'plugin/FyteClub.csproj') -replace '<Version>[^<]*</Version>', '<Version>%VERSION%</Version>' | Set-Content 'plugin/FyteClub.csproj'"

REM Update build scripts version displays
echo Updating build script version displays...
for %%f in (build-*.bat) do (
    powershell -Command "(Get-Content '%%f') -replace 'echo Building FyteClub v[0-9.]*', 'echo Building FyteClub v%VERSION%' | Set-Content '%%f'"
    powershell -Command "(Get-Content '%%f') -replace 'echo ✅ FyteClub v[0-9.]* build complete!', 'echo ✅ FyteClub v%VERSION% build complete!' | Set-Content '%%f'"
)

echo.
echo ✅ All version references updated to v%VERSION%
echo.

git add .
git commit -m "Release v%VERSION%: Connection fixes and Redis auto-detection"
git tag -a v%VERSION% -m "FyteClub v%VERSION% - Connection Status & Infrastructure Fixes"
git push origin main
git push origin v%VERSION%

echo.
echo ✅ Release v%VERSION% tagged and pushed!
echo.
echo Next steps:
echo 1. Go to GitHub repository
echo 2. Create release from v%VERSION% tag
echo 3. Upload FyteClub-Server.zip as release asset
echo 4. Copy RELEASE_NOTES.md content to release description
echo.
pause