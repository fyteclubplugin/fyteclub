@echo off
echo Creating FyteClub v1.1.1 Release Tag...

git add .
git commit -m "Release v1.1.1: Enhanced stability and daemon management"
git tag -a v1.1.1 -m "FyteClub v1.1.1 - Enhanced Stability Release"
git push origin main
git push origin v1.1.1

echo.
echo ✅ Release v1.1.1 tagged and pushed!
echo.
echo Next steps:
echo 1. Go to GitHub repository
echo 2. Create release from v1.1.1 tag
echo 3. Upload FyteClub-Server.zip as release asset
echo 4. Copy RELEASE_NOTES.md content to release description
echo.
pause