@echo off
echo Copying FyteClub plugin files...
copy "c:\Users\Me\git\fyteclub\plugin\bin\Release\FyteClub.dll" "C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\4.0.0\"
copy "c:\Users\Me\git\fyteclub\plugin\bin\Release\FyteClub.deps.json" "C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\4.0.0\"
copy "c:\Users\Me\git\fyteclub\plugin\bin\Release\Penumbra.Api.dll" "C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\4.0.0\"
copy "c:\Users\Me\git\fyteclub\plugin\bin\Release\Glamourer.Api.dll" "C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\4.0.0\"
echo Plugin files copied successfully!
echo Please restart FFXIV completely to load the updated plugin.
pause
