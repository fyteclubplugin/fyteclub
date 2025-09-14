# Install Google libwebrtc

## Download and Setup

1. **Download libwebrtc**: https://github.com/webrtc-sdk/libwebrtc/releases
2. **Extract to**: `c:\Users\Me\git\libwebrtc\`
3. **Verify structure**:
   ```
   c:\Users\Me\git\libwebrtc\
   ├── include\
   │   └── api\
   │       ├── peer_connection_interface.h
   │       └── create_peerconnection_factory.h
   └── lib\
       └── webrtc.lib
   ```

## Build WebRTC Native

```bash
cd fyteclub
call build-webrtc-native.bat
```

The build will automatically find libwebrtc at `../libwebrtc/` relative to the fyteclub directory.