@echo off
echo Testing steps after client build...

echo Creating Plugin package...
if exist "release" rmdir /s /q "release"
mkdir "release"
mkdir "release\FyteClub-Plugin"

echo Copying files...
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\"
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\"
copy "client\dist\fyteclub.exe" "release\FyteClub-Plugin\"

echo Creating README...
echo # FyteClub Plugin > "release\FyteClub-Plugin\README.txt"

echo Done with plugin package!

echo Creating Server package...
mkdir "release\FyteClub-Server"
echo Server directory created

echo All done!