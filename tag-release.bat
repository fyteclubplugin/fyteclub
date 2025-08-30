@echo off
echo Creating FyteClub v1.0.1 Release Tag...

git add .
git commit -m "Release v1.0.1: Complete feature set with server management, security fixes, and deployment scripts"
git tag -a v1.0.1 -m "FyteClub v1.0.1 - Production Ready Release"
git push origin main
git push origin v1.0.1

echo.
echo ✅ Release v1.0.1 tagged and pushed!
echo.
echo Next steps:
echo 1. Go to GitHub repository
echo 2. Create release from v1.0.1 tag
echo 3. Upload FyteClub-Server.zip as release asset
echo 4. Copy RELEASE_NOTES.md content to release description
echo.
pause