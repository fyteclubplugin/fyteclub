cmake_minimum_required(VERSION 3.16)
project(webrtc_native)

set(CMAKE_CXX_STANDARD 17)

# Try to find libdatachannel via vcpkg
find_package(PkgConfig QUIET)
find_package(LibDataChannel QUIET)

# Create shared library
add_library(webrtc_native SHARED webrtc_wrapper.cpp)

# Configure based on available libraries
if(LibDataChannel_FOUND)
    message(STATUS "Using libdatachannel for P2P functionality")
    target_compile_definitions(webrtc_native PRIVATE USE_LIBDATACHANNEL)
    target_link_libraries(webrtc_native LibDataChannel::LibDataChannel)
else()
    message(STATUS "libdatachannel not found, using mock implementation")
    message(STATUS "Install with: vcpkg install libdatachannel")
endif()

# Windows-specific settings
if(WIN32)
    target_compile_definitions(webrtc_native PRIVATE WEBRTC_WIN)
    target_link_libraries(webrtc_native ws2_32 winmm)
endif()

# Output to plugin directory
set_target_properties(webrtc_native PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../plugin/bin/Debug/win-x64"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../plugin/bin/Debug/win-x64"
)

message(STATUS "P2P wrapper configured for MSVC compatibility")