# SPDX-License-Identifier: MIT
# kiln-set.dll: runs Shrouded Engine Tweaks (SET 0.1.1, MIT) Lua mods on Kiln. Deployed to <game>/kiln/plugins/.
# src/lua_api.cpp, lua_api.h, lua_json.cpp, level.h, hooks.h and parts of game.cpp are adapted from SET (Copyright (c)
# 2026 Cacaida, MIT; licenses/ShroudedEngineTweaks-LICENSE.txt). Lua 5.4.8 (MIT) is compiled as C++, as SET does, so
# Lua errors unwind C++ frames properly.
file(GLOB KILN_LUA_SOURCES CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/third_party/lua/*.c")
set_source_files_properties(${KILN_LUA_SOURCES} PROPERTIES LANGUAGE CXX)
kiln_plugin(set
    src/plugin.cpp
    src/game.cpp
    src/hooks.cpp
    src/lua_api.cpp
    src/lua_json.cpp
    src/lua_imgui.cpp
    ${KILN_LUA_SOURCES})
target_include_directories(kiln-set PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src" "${CMAKE_SOURCE_DIR}/third_party/lua")
target_compile_options(kiln-set PRIVATE /EHsc -Wno-unused-parameter -Wno-unused-function -Wno-sign-compare)
