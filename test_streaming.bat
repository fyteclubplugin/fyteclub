@echo off
echo Creating test file...
echo This is a test file for streaming > test_file.txt
for /L %%i in (1,1,1000) do echo Line %%i: Some test data for streaming protocol >> test_file.txt

echo Test file created: test_file.txt
echo File size:
dir test_file.txt | find "test_file.txt"

echo.
echo To test:
echo 1. Launch FFXIV with the plugin
echo 2. Type /fyteclub in chat
echo 3. Click "Test File Transfer" button
echo 4. Check Dalamud logs for streaming results
pause