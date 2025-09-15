@echo off
if "%1"=="" (
    echo Usage: release.bat [version]
    exit /b 1
)

echo Building and releasing FyteClub v%1...

cd plugin
dotnet build -c Release
if %errorlevel% neq 0 (
    echo Build failed!
    exit /b 1
)

cd ..
update-version.bat %1
build-p2p-release.bat

git add .
git commit -m "Release v%1 - P2P architecture with phonebook deltas and mod cache"
git tag v%1
git push origin main
git push origin v%1

echo Release v%1 complete!