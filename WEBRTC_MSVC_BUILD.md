# Building WebRTC with MSVC Compatibility

The current WebRTC build uses libc++ which causes ABI compatibility issues. We need to rebuild WebRTC with MSVC compatibility.

## Attempted Solution: Rebuild WebRTC with MSVC Runtime

**Status: FAILED** - Same compiler-rt atomic issues

```bash
cd webrtc-checkout/src

# Configure for MSVC compatibility
gn gen out/msvc --args='
  is_debug=false
  is_clang=false
  use_custom_libcxx=false
  use_rtti=true
  rtc_build_examples=false
  rtc_build_tools=false
  rtc_include_tests=false
  rtc_enable_protobuf=false
  target_cpu="x64"
  is_component_build=false
'

# Build WebRTC with MSVC - FAILS
ninja -C out/msvc webrtc
```

**Error**: `atomic.c(313): fatal error C1003: error count exceeds 100`

The compiler-rt atomic operations are incompatible with MSVC regardless of build configuration.

## Alternative: Use Pre-built WebRTC

Download pre-built WebRTC libraries that are MSVC-compatible:
- https://github.com/webrtc-uwp/webrtc-uwp-sdk/releases
- Or use vcpkg: `vcpkg install webrtc`

## Current Status

The WebRTC library at `webrtc-checkout/src/out/Default/obj/webrtc.lib` was built with:
- Clang compiler
- libc++ standard library
- Custom runtime

This causes linking errors when trying to use it with our Clang wrapper that uses Windows C runtime.

## Required Solution

**WebRTC fundamentally requires Clang and libc++**. We need:

1. **Find pre-built libc++ runtime libraries** for Windows
2. **Link against libc++ runtime** in our Clang wrapper
3. **Alternative**: Use a different P2P library (not WebRTC)

## Potential Sources for libc++
- LLVM pre-built binaries with runtime libraries
- vcpkg libc++ package
- Build libc++ separately from LLVM source
- Use WebRTC pre-built binaries from vcpkg or other sources