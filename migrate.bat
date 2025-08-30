@echo off
echo FyteClub Repository Migration Script
echo.
echo Migrating to: https://github.com/fyteclubplugin/fyteclub
echo.
echo BEFORE RUNNING:
echo 1. Create new public repo: https://github.com/fyteclubplugin/fyteclub
echo 2. Don't initialize with README (we'll push existing code)
echo.
set /p confirm="Ready to migrate? (y/n): "

if /i "%confirm%" neq "y" (
    echo Migration cancelled.
    exit /b
)

echo.
echo Adding new remote and pushing...
echo.

REM Add new remote
git remote add new-origin https://github.com/fyteclubplugin/fyteclub.git

REM Push all branches and tags
git push new-origin --all
git push new-origin --tags

echo.
echo Migration complete!
echo.
echo NEXT STEPS:
echo 1. Verify everything at: https://github.com/fyteclubplugin/fyteclub
echo 2. Test GitHub Actions releases
echo 3. Update custom repository URL: https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json
echo 4. Make original repo private or delete it
echo.
pause