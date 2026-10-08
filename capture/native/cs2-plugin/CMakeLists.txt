cmake_minimum_required(VERSION 3.24)
project(odx_netlog LANGUAGES CXX)

set(HL2SDK_CS2 "D:/odx-dev/hl2sdk-cs2" CACHE PATH "hl2sdk, cs2 branch")
set(MMSOURCE "D:/odx-dev/metamod-source" CACHE PATH "Metamod:Source 2.0 source tree (headers only)")

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

# eiface.h includes network_connection.pb.h, generated with the SDK's own protoc (its protobuf version must match
# the libprotobuf.lib the SDK ships).
set(PROTO_OUT ${CMAKE_CURRENT_BINARY_DIR}/proto)
file(MAKE_DIRECTORY ${PROTO_OUT})
add_custom_command(
    OUTPUT ${PROTO_OUT}/network_connection.pb.h ${PROTO_OUT}/network_connection.pb.cc
    COMMAND ${HL2SDK_CS2}/devtools/bin/protoc.exe --cpp_out=${PROTO_OUT}
        --proto_path=${HL2SDK_CS2}/common --proto_path=${HL2SDK_CS2}/thirdparty/protobuf-3.21.8/src
        ${HL2SDK_CS2}/common/network_connection.proto
    DEPENDS ${HL2SDK_CS2}/common/network_connection.proto)

# Same sources, include paths, defines and libraries as Metamod's AMBuild for an "2.cs2" plugin
# (hl2sdk-manifests/manifests/cs2.json, SdkHelpers.configureCxx).
add_library(odx_netlog SHARED
    src/plugin.cpp
    src/vtable.cpp
    ${PROTO_OUT}/network_connection.pb.cc
    ${HL2SDK_CS2}/public/tier0/memoverride.cpp)

target_include_directories(odx_netlog PRIVATE
    ${PROTO_OUT}
    ${MMSOURCE}/core
    ${MMSOURCE}/third_party/khook/include
    ${HL2SDK_CS2}/thirdparty/protobuf-3.21.8/src
    ${HL2SDK_CS2}/public
    ${HL2SDK_CS2}/public/engine
    ${HL2SDK_CS2}/public/mathlib
    ${HL2SDK_CS2}/public/tier0
    ${HL2SDK_CS2}/public/tier1
    ${HL2SDK_CS2}/public/entity2
    ${HL2SDK_CS2}/public/game/server
    ${HL2SDK_CS2}/game/shared
    ${HL2SDK_CS2}/game/server
    ${HL2SDK_CS2}/common)

target_compile_definitions(odx_netlog PRIVATE
    SE_CS2=25 SOURCE_ENGINE=25 META_IS_SOURCE2 GAME_DLL RAD_TELEMETRY_DISABLED
    COMPILER_MSVC COMPILER_MSVC64 WIN32 WIN64 _WINDOWS X64BITS PLATFORM_64BITS NDEBUG
    _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_DEPRECATE)

target_compile_options(odx_netlog PRIVATE /EHsc /W3 /wd4005 /wd4100 /wd4244 /wd4267)

target_link_libraries(odx_netlog PRIVATE
    ${HL2SDK_CS2}/lib/public/win64/2015/libprotobuf.lib
    ${HL2SDK_CS2}/lib/public/win64/mathlib.lib
    ${HL2SDK_CS2}/lib/public/win64/tier0.lib
    ${HL2SDK_CS2}/lib/public/win64/interfaces.lib
    legacy_stdio_definitions.lib)
