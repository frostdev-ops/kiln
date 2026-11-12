# SPDX-License-Identifier: MIT
# kiln-set.dll: runs Shrouded Engine Tweaks (SET 0.2.0, MIT) Lua mods on Kiln. Deployed to <game>/kiln/plugins/.
# src/lua_api.cpp, lua_api.h, lua_json.cpp, level.h, hooks.h, rules.*, contracts.*, modsettings.*, writelog.* and parts of
# game.cpp and hooks.cpp are adapted from SET (Copyright (c)
# 2026 Cacaida, MIT; licenses/ShroudedEngineTweaks-LICENSE.txt). Lua 5.4.8 (MIT) is compiled as C++, as SET does, so
# Lua errors unwind C++ frames properly.
file(GLOB KILN_LUA_SOURCES CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/third_party/lua/*.c")
# Lua itself keeps /EHsc (the project uses /EHa): under /EHa, Lua's catch(...) in luaD_rawrunprotected would turn an
# access violation into a Lua error, and lua_api.cpp's guarded() would no longer see it and switch the mods off.
set_source_files_properties(${KILN_LUA_SOURCES} PROPERTIES LANGUAGE CXX COMPILE_OPTIONS /EHsc)
kiln_plugin(set
    src/plugin.cpp
    src/game.cpp
    src/hooks.cpp
    src/rules.cpp
    src/contracts.cpp
    src/modsettings.cpp
    src/overlay.cpp
    src/inspector.cpp
    src/writelog.cpp
    src/lua_api.cpp
    src/lua_json.cpp
    src/lua_imgui.cpp
    ${KILN_LUA_SOURCES})
target_include_directories(kiln-set PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src" "${CMAKE_SOURCE_DIR}/third_party/lua")
target_link_libraries(kiln-set PRIVATE user32) # explicit, user-triggered Unicode log copy
target_compile_options(kiln-set PRIVATE -Wno-unused-parameter -Wno-unused-function -Wno-sign-compare)
# English item names for Admin.give and Res.text: Kiln.Items' Data/items.tsv (from the Enshrouded Save Editor's
# data/items.json, MIT, see its ITEMS-LICENSE.txt) as a C table, generated at configure time into the build dir.
set(KILN_SET_ITEMS_TSV "${CMAKE_SOURCE_DIR}/../managed/services/Kiln.Items/Data/items.tsv")
set(KILN_SET_ITEMS_INC "${CMAKE_CURRENT_BINARY_DIR}/generated/kiln_set_items.inc")
set(_set_items "// Generated from managed/services/Kiln.Items/Data/items.tsv; do not edit.\n")
if(EXISTS "${KILN_SET_ITEMS_TSV}")
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${KILN_SET_ITEMS_TSV}")
    file(STRINGS "${KILN_SET_ITEMS_TSV}" _set_item_lines ENCODING UTF-8)
else()
    # fail soft: kiln-set builds without English item names (Admin.give / Res.text then use debug names only)
    message(WARNING "kiln-set: ${KILN_SET_ITEMS_TSV} is missing: English item names are left out")
    set(_set_item_lines "")
endif()
foreach(_line IN LISTS _set_item_lines)
    if(_line MATCHES "^([0-9]+)\t([^\t]*)\t([^\t]*)")
        set(_id "${CMAKE_MATCH_1}")
        set(_debug "${CMAKE_MATCH_2}")
        set(_display "${CMAKE_MATCH_3}")
        foreach(_v _debug _display)
            string(REPLACE "\\" "\\\\" ${_v} "${${_v}}")
            string(REPLACE "\"" "\\\"" ${_v} "${${_v}}")
        endforeach()
        string(APPEND _set_items "{${_id}u, \"${_debug}\", \"${_display}\"},\n")
    endif()
endforeach()
string(APPEND _set_items "{0u, \"\", \"\"},  // end (keeps the array non-empty without a TSV)\n")
file(WRITE "${KILN_SET_ITEMS_INC}.tmp" "${_set_items}")
file(COPY_FILE "${KILN_SET_ITEMS_INC}.tmp" "${KILN_SET_ITEMS_INC}" ONLY_IF_DIFFERENT)
target_include_directories(kiln-set PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/generated" "${CMAKE_SOURCE_DIR}/services/players")
