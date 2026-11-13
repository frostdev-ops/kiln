# SPDX-License-Identifier: MIT
# kiln-emf.dll: loads unmodified EMF mods (EMF 0.3.3 to 0.6.8, EmfApi v40) on Kiln. Deployed to <game>/kiln/plugins/.
# include/emf/emf_api.h is EMF's own header (MIT, EMF contributors; licenses/EMF-LICENSE.txt).
kiln_plugin(emf
    src/plugin.cpp
    src/mods.cpp
    src/settings.cpp
    src/api_core.cpp
    src/api_game.cpp
    src/api_events.cpp
    src/api_hooks.cpp
    src/commands.cpp
    src/api_attributes.cpp
    src/api_level.cpp
    src/api_components.cpp
    src/api_windows.cpp
    src/api_wheel.cpp
    src/api_net.cpp
    src/api_net_client.cpp
    src/api_portraits.cpp
    src/api_v40.cpp
    src/net/net_service.cpp
    src/net/net_link.cpp
    src/net/net_line.cpp
    src/net/net_peers.cpp
    src/v26_model.cpp
    src/seh.cpp
    src/util.cpp)
target_include_directories(kiln-emf PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include"
                                            "${CMAKE_SOURCE_DIR}/services/players")  # kiln_progression.h

# The conformance probe (an EMF mod, mods/tests/emf-test/probe): bin/emf-test/emf_probe.dll, for labs only
# (tools/emf/lab-check.py installs it into <game>/emf/mods/emf_probe/). Built against EMF's header alone.
add_library(emf-probe SHARED "${CMAKE_SOURCE_DIR}/../mods/tests/emf-test/probe/emf_probe.cpp")
target_include_directories(emf-probe PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
set_target_properties(emf-probe PROPERTIES OUTPUT_NAME emf_probe RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/emf-test")

# Mod messages conformance probe (mods/tests/emf-net-test): lab only, never packaged as a player mod.
add_library(emf-net-probe SHARED "${CMAKE_SOURCE_DIR}/../mods/tests/emf-net-test/probe.cpp")
target_include_directories(emf-net-probe PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
set_target_properties(emf-net-probe PROPERTIES OUTPUT_NAME emf_net_probe RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/emf-test")

# Headless stand-in process for the separate GPL portrait DLL export (never starts the game).
add_executable(emf-portrait-bridge-test tests/portrait_wine.cpp)
set_target_properties(emf-portrait-bridge-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/emf-test")
