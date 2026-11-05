# SPDX-License-Identifier: MIT
# Kiln native SDK for mods built outside the Kiln repository (docs/kiln/modding/mdk.md, "Native mods").
#   include(<KILN_SDK_DIR>/cmake/KilnPlugin.cmake)       KILN_SDK_DIR = the Kiln.Sdk package's native/ folder
#                                                        (~/.nuget/packages/kiln.sdk/<version>/native) or
#                                                        <kiln repo>/managed/Kiln.Sdk/native
#   kiln_plugin(<name> <sources...> [NATIVE_JSON <native.json>] [OUTPUT_NAME <dll name>])
# builds <name>.dll (Windows x64) against Kiln's C ABI (kiln_api.h, kiln_native.h, services/kiln_<name>.h).
#   NATIVE_JSON  a native mod (kiln_native_init; docs/kiln/native-mods.md): the mod folder is staged in
#                ${CMAKE_BINARY_DIR}/mod/<id>/ (dll + native.json + pdb), and target `deploy` copies it to
#                ${KILN_SERVER}/kiln/native/<id>/ (configure with -DKILN_SERVER=<game or server folder with Kiln>).
#   without      a Kiln plugin (kiln_plugin_main) for <game>/kiln/plugins/; `deploy` copies the dll there.
# On Linux configure with the toolchain next to this file (clang-cl + lld-link + the xwin MSVC CRT/SDK):
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<KILN_SDK_DIR>/cmake/clang-cl-xwin.cmake -DKILN_SDK_DIR=<dir>
# `kiln build` / `kiln deploy` run these steps for you.
# Inside the Kiln repository's own CMake tree (native/plugins/<name>/) kiln_plugin is Kiln's own function; this file
# then does nothing.
if(COMMAND kiln_plugin)
    return()
endif()
include_guard(GLOBAL)

get_filename_component(_kiln_sdk_dir "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
if(EXISTS "${_kiln_sdk_dir}/include/kiln_api.h")                       # the package layout
    set(KILN_INCLUDE_DIR "${_kiln_sdk_dir}/include" CACHE PATH "Kiln ABI headers")
elseif(EXISTS "${_kiln_sdk_dir}/../../../native/core/abi/kiln_api.h")  # managed/Kiln.Sdk/native in a Kiln checkout
    get_filename_component(_kiln_abi "${_kiln_sdk_dir}/../../../native/core/abi" ABSOLUTE)
    set(KILN_INCLUDE_DIR "${_kiln_abi}" CACHE PATH "Kiln ABI headers")
else()
    message(FATAL_ERROR "KilnPlugin.cmake: no Kiln ABI headers next to ${_kiln_sdk_dir} (expected include/kiln_api.h)")
endif()
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Kiln native mods are Windows x64 DLLs; on Linux use -DCMAKE_TOOLCHAIN_FILE=${_kiln_sdk_dir}/cmake/clang-cl-xwin.cmake")
endif()
set(KILN_SERVER "" CACHE PATH "game or dedicated-server folder with Kiln, for the deploy target")

function(kiln_plugin name)
    cmake_parse_arguments(KP "" "NATIVE_JSON;OUTPUT_NAME" "" ${ARGN})
    add_library(${name} SHARED ${KP_UNPARSED_ARGUMENTS})
    target_include_directories(${name} PRIVATE "${KILN_INCLUDE_DIR}")
    target_compile_features(${name} PRIVATE cxx_std_20)
    if(MSVC)
        target_compile_definitions(${name} PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS UNICODE _UNICODE)
        target_compile_options(${name} PRIVATE "$<$<COMPILE_LANGUAGE:CXX>:/EHsc>" /Zi)
        target_link_options(${name} PRIVATE /DEBUG /OPT:REF /OPT:ICF /IGNORE:4099)
        set_property(TARGET ${name} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
    endif()
    if(KP_OUTPUT_NAME)
        set_target_properties(${name} PROPERTIES OUTPUT_NAME "${KP_OUTPUT_NAME}")
    endif()
    if(KP_NATIVE_JSON)
        # the mod id from native.json ("id": "..."), for the folder name
        file(READ "${KP_NATIVE_JSON}" _json)
        string(REGEX MATCH "\"id\"[ \t\r\n]*:[ \t\r\n]*\"([a-z0-9][a-z0-9._-]*)\"" _m "${_json}")
        if(NOT CMAKE_MATCH_1)
            message(FATAL_ERROR "kiln_plugin(${name}): ${KP_NATIVE_JSON} has no valid \"id\"")
        endif()
        set(_id "${CMAKE_MATCH_1}")
        set(_stage "${CMAKE_BINARY_DIR}/mod/${_id}")
        set_target_properties(${name} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${_stage}" PDB_OUTPUT_DIRECTORY "${_stage}")
        foreach(_cfg Debug Release RelWithDebInfo MinSizeRel)
            string(TOUPPER ${_cfg} _CFG)
            set_target_properties(${name} PROPERTIES RUNTIME_OUTPUT_DIRECTORY_${_CFG} "${_stage}" PDB_OUTPUT_DIRECTORY_${_CFG} "${_stage}")
        endforeach()
        configure_file("${KP_NATIVE_JSON}" "${_stage}/native.json" COPYONLY)
        set(_dest "${KILN_SERVER}/kiln/native/${_id}")
        set_property(TARGET ${name} PROPERTY KILN_MOD_ID "${_id}")
        set(_copy COMMAND ${CMAKE_COMMAND} -E make_directory "${_dest}"
                  COMMAND ${CMAKE_COMMAND} -E copy_directory "${_stage}" "${_dest}"
                  COMMAND ${CMAKE_COMMAND} -E echo "${name} -> ${_dest} (native mod ${_id})")
    else()
        set(_dest "${KILN_SERVER}/kiln/plugins")
        set(_copy COMMAND ${CMAKE_COMMAND} -E make_directory "${_dest}"
                  COMMAND ${CMAKE_COMMAND} -E copy "$<TARGET_FILE:${name}>" "${_dest}/"
                  COMMAND ${CMAKE_COMMAND} -E echo "${name} -> ${_dest} (plugin)")
    endif()
    if(KILN_SERVER)
        add_custom_target(deploy ${_copy} DEPENDS ${name} VERBATIM)
    else()
        add_custom_target(deploy COMMAND ${CMAKE_COMMAND} -E echo "deploy: configure with -DKILN_SERVER=<game or server folder with Kiln>"
                                 COMMAND ${CMAKE_COMMAND} -E false DEPENDS ${name} VERBATIM)
    endif()
endfunction()
