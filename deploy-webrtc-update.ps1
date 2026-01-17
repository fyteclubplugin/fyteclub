# Quick deployment of WebRTC test updates to Pi
param(
    [string]$PiAddress = "192.168.1.51",
    [string]$PiUser = "pi",
    [string]$PiPassword = "fyteclub123"
)

Write-Host "Deploying WebRTC test updates to Pi..." -ForegroundColor Green
Write-Host "Target: $PiUser@$PiAddress" -ForegroundColor Cyan

# Create temp directory for deployment files
$tempDir = "$env:TEMP\fyteclub-webrtc-update"
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $tempDir | Out-Null

# Copy the built files
$buildPath = "C:\Users\Me\git\fyteclub\pi-test-node\bin\Release\net8.0\linux-arm64"
Copy-Item "$buildPath\FyteClub.Pi.TestNode.dll" $tempDir
Copy-Item "$buildPath\FyteClub.Pi.TestNode.runtimeconfig.json" $tempDir -ErrorAction SilentlyContinue
Copy-Item "$buildPath\FyteClub.Pi.TestNode.deps.json" $tempDir -ErrorAction SilentlyContinue

Write-Host "Files prepared for deployment:" -ForegroundColor Yellow
Get-ChildItem $tempDir | ForEach-Object { Write-Host "  - $($_.Name)" }

Write-Host ""
Write-Host "Manual deployment steps:" -ForegroundColor Yellow
Write-Host "1. Stop the current test node on Pi"
Write-Host "2. Copy files from: $tempDir"
Write-Host "3. To: pi@${PiAddress}:/home/pi/fyteclub-test/"
Write-Host "4. Restart the Pi test node"
Write-Host ""

Write-Host "Files ready at: $tempDir" -ForegroundColor Green