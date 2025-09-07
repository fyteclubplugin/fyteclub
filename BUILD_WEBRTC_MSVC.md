# WebRTC Build Steps for Windows/MSVC

## Prerequisites
- Visual Studio 2022 (Desktop C++ workload)
- Python 3.x
- Git (latest)
- depot_tools (clone to e.g. `C:\depot_tools`)

## 1. Add depot_tools to PATH
```cmd
set PATH=C:\depot_tools;%PATH%
```
(Or add to system/user environment variables.)

## 2. Fetch WebRTC Source
```cmd
mkdir C:\webrtc-msvc
cd C:\webrtc-msvc
fetch webrtc
cd src
gclient sync --force
gclient runhooks
```

## 3. If gn.exe Is Missing
- Check for `gn.exe` in:
  - `C:\webrtc-msvc\src\buildtools\win\gn\gn.exe`
- If missing, use depot_tools' gn:
  ```cmd
  gn gen out/msvc --ide=vs --args="is_debug=true is_component_build=false is_clang=false target_cpu=\"x64\""
  ```
  Or:
  ```cmd
  C:\depot_tools\gn gen out/msvc --ide=vs --args="is_debug=true is_component_build=false is_clang=false target_cpu=\"x64\""
  ```

## 4. Build WebRTC
```cmd
ninja -C out/msvc
```

## 5. Update CMakeLists.txt
Update the WebRTC path in `native/CMakeLists.txt`:
```cmake
set(WEBRTC_ROOT "C:/webrtc-msvc/src")
target_link_directories(webrtc_native PRIVATE "${WEBRTC_ROOT}/out/msvc/obj")
```

## 6. Use Output
- Link your native wrapper to the `.lib` and headers in `out/msvc`

**Key Points:**
- `is_clang=false` forces MSVC compilation for ABI compatibility
- Always use the same compiler and runtime for all components
- The current WebRTC build uses Clang/libc++ which causes linking issues with MSVC

## Known Issues
- WebRTC build with MSVC fails due to compiler-rt atomic compatibility issues
- This is a known problem between WebRTC's compiler-rt and MSVC
- Alternative: Use pre-built WebRTC binaries or continue with mock implementation