cmake_minimum_required(VERSION 3.24)
project(client_extractor LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
add_compile_options($<$<C_COMPILER_ID:MSVC>:/utf-8> $<$<CXX_COMPILER_ID:MSVC>:/utf-8>)

include(FetchContent)

# Lua 5.4 (64-bit integers are needed for addresses; LuaJIT lacks them).
FetchContent_Declare(lua
  URL https://www.lua.org/ftp/lua-5.4.7.tar.gz
  URL_HASH SHA256=9fbf5e28ef86c69858f6d3d34eccc32e911c1a28b4120ff3e84aaa70cfbf1e30
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(lua)
file(GLOB LUA_SOURCES ${lua_SOURCE_DIR}/src/*.c)
list(REMOVE_ITEM LUA_SOURCES ${lua_SOURCE_DIR}/src/lua.c ${lua_SOURCE_DIR}/src/luac.c)
add_library(lua_static STATIC ${LUA_SOURCES})
target_include_directories(lua_static PUBLIC ${lua_SOURCE_DIR}/src)
if(UNIX)
  target_compile_definitions(lua_static PRIVATE LUA_USE_POSIX)
endif()

# Portable core: geometry, input aggregation, output writers, memory/signatures, Lua profiles, recorder.
add_library(odx_core STATIC
  src/math.cpp src/input.cpp src/output.cpp src/memory.cpp
  src/profile.cpp src/config.cpp src/recorder.cpp src/fake_game.cpp)
target_include_directories(odx_core PUBLIC include)
target_link_libraries(odx_core PUBLIC lua_static)

add_executable(odx_synth tools/synth_session.cpp)
target_link_libraries(odx_synth PRIVATE odx_core)

# Windows: the injectable extractor DLL (Present hook + Raw Input + in-process memory).
if(WIN32)
  FetchContent_Declare(minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG v1.3.3
    SOURCE_SUBDIR none)  # upstream CMakeLists predates CMake 4; build sources directly
  FetchContent_MakeAvailable(minhook)
  file(GLOB MINHOOK_SOURCES ${minhook_SOURCE_DIR}/src/*.c ${minhook_SOURCE_DIR}/src/hde/*.c)
  add_library(minhook STATIC ${MINHOOK_SOURCES})
  target_include_directories(minhook PUBLIC ${minhook_SOURCE_DIR}/include)
  add_library(client_extractor SHARED
    src/win/dllmain.cpp src/win/present_hook.cpp src/win/process_memory.cpp)
  target_link_libraries(client_extractor PRIVATE odx_core minhook d3d11 dxgi psapi)
  target_compile_definitions(client_extractor PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)

  # Input capture lives in capture-cs (odx-input, C#); it forwards to this DLL with --forward.
  add_executable(odx_inject tools/win/inject_main.cpp)
  target_compile_definitions(odx_inject PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)

  # Ship the default config and profiles next to the DLL.
  add_custom_command(TARGET client_extractor POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy ${CMAKE_CURRENT_SOURCE_DIR}/odx_config.lua $<TARGET_FILE_DIR:client_extractor>
    COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_CURRENT_SOURCE_DIR}/profiles $<TARGET_FILE_DIR:client_extractor>/profiles)
endif()

# Tests
option(ODX_BUILD_TESTS "Build tests" ON)
if(ODX_BUILD_TESTS)
  enable_testing()
  FetchContent_Declare(doctest
    GIT_REPOSITORY https://github.com/doctest/doctest.git
    GIT_TAG v2.4.11
    SOURCE_SUBDIR none)  # header-only; upstream CMakeLists predates CMake 4
  FetchContent_MakeAvailable(doctest)
  add_executable(odx_tests tests/test_core.cpp)
  target_include_directories(odx_tests PRIVATE ${doctest_SOURCE_DIR})
  target_link_libraries(odx_tests PRIVATE odx_core)
  target_compile_definitions(odx_tests PRIVATE ODX_PROFILES_DIR="${CMAKE_CURRENT_SOURCE_DIR}/profiles")
  add_test(NAME core COMMAND odx_tests)

  # End to end: fake game -> Lua profile -> recorder -> odx-validate (capture-cs, if built).
  set(ODX_SYNTH_DIR ${CMAKE_CURRENT_BINARY_DIR}/synth_session)
  add_test(NAME synth_session
    COMMAND odx_synth ${CMAKE_CURRENT_SOURCE_DIR}/profiles/synthetic.lua ${ODX_SYNTH_DIR} 300)
  find_program(CLIENT_VALIDATE odx-validate
    HINTS ${CMAKE_CURRENT_SOURCE_DIR}/../capture-cs/src/Odx.Validate/bin/Release/net8.0
          ${CMAKE_CURRENT_SOURCE_DIR}/../capture-cs/src/Odx.Validate/bin/Debug/net8.0)
  if(CLIENT_VALIDATE)
    add_test(NAME synth_session_validates COMMAND ${CLIENT_VALIDATE} --no-video ${ODX_SYNTH_DIR})
    set_tests_properties(synth_session_validates PROPERTIES DEPENDS synth_session)
  endif()
endif()
