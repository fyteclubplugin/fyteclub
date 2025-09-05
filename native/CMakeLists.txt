cmake_minimum_required(VERSION 3.16)
project(webrtc_native)

set(CMAKE_CXX_STANDARD 17)

# Find libwebrtc
find_package(PkgConfig REQUIRED)
pkg_check_modules(WEBRTC REQUIRED libwebrtc)

# Create shared library
add_library(webrtc_native SHARED webrtc_wrapper.cpp)

# Link libwebrtc
target_link_libraries(webrtc_native ${WEBRTC_LIBRARIES})
target_include_directories(webrtc_native PRIVATE ${WEBRTC_INCLUDE_DIRS})
target_compile_options(webrtc_native PRIVATE ${WEBRTC_CFLAGS_OTHER})

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