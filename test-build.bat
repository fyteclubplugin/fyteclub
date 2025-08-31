@echo off
echo Testing build steps...

echo Step 1: Clean release directory
if exist "release" rmdir /s /q "release"
mkdir "release"
echo Release directory created

echo Step 2: Build client
cd client
call npm run build
echo Client build completed with exit code: %errorlevel%
cd ..

echo Step 3: Create plugin package
mkdir "release\FyteClub-Plugin"
echo Plugin directory created

echo Step 4: Copy files
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "client\dist\fyteclub.exe" "release\FyteClub-Plugin\"
echo Files copied

echo All steps completed successfully!