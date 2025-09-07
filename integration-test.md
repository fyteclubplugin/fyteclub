# FyteClub P2P Integration Test

## Prerequisites
1. Two FFXIV instances with Dalamud
2. FyteClub plugin installed on both
3. webrtc_native.dll present in plugin directories

## Test Procedure

### Step 1: Verify WebRTC Implementation
1. Start both FFXIV instances
2. Check Dalamud logs for WebRTC initialization:
   - **Expected (Release)**: `WebRTC: Using LibWebRTCConnection (native)`
   - **Expected (Debug)**: `WebRTC: Using MockWebRTCConnection (test mode)`
   - **Error**: `CRITICAL: webrtc_native.dll not found or failed to load`

### Step 2: Create Syncshell (Instance A)
1. Open FyteClub config (`/fyteclub`)
2. Go to "Syncshells" tab
3. Enter syncshell name: `TestSync`
4. Click "Create Syncshell"
5. **Expected**: Green "Active Syncshells: 1/1"
6. **Expected Log**: `Syncshell 'TestSync' created successfully as host`

### Step 3: Join Syncshell (Instance B)
1. Open FyteClub config (`/fyteclub`)
2. Go to "Syncshells" tab
3. Enter syncshell name: `TestSync`
4. Enter encryption key from Instance A's config
5. Click "Join Syncshell"
6. **Expected**: Green "Active Syncshells: 1/1"
7. **Expected Log**: `Successfully joined syncshell: 'TestSync'`

### Step 4: Test P2P Connection
1. Both instances: Click "Discover Peers"
2. **Expected Logs**:
   - `Performing peer discovery for 1 active syncshells...`
   - `WebRTC host ready in [syncshell_id]` (Instance A)
   - `WebRTC joined syncshell [syncshell_id]` (Instance B)

### Step 5: Test Mod Sharing
1. Instance A: Change appearance (Penumbra/Glamourer)
2. Instance A: Click "Resync My Appearance"
3. **Expected Logs**:
   - `Shared mods to syncshell peers`
   - `Sent mod data to [syncshell_id]: [X] bytes`
4. Instance B: Should receive mod data
5. **Expected Log**: `Received mod data from syncshell [syncshell_id]: [X] bytes`

## Troubleshooting

### WebRTC DLL Issues
- **Error**: `webrtc_native.dll not found`
- **Fix**: Run `build-native.bat` to compile DLL
- **Verify**: Check `plugin/bin/Release/webrtc_native.dll` exists

### Connection Issues
- **Error**: No peer discovery logs
- **Fix**: Check firewall, ensure both instances on same network
- **Debug**: Use DEBUG build to test with mock connections

### Mod Sharing Issues
- **Error**: No mod data sent/received
- **Fix**: Ensure Penumbra/Glamourer installed and working
- **Debug**: Check cache statistics in "Cache" tab

## Success Criteria
✓ WebRTC native implementation loads successfully  
✓ Syncshell creation and joining works  
✓ P2P peer discovery establishes connections  
✓ Mod data transfers between instances  
✓ No critical errors in logs  