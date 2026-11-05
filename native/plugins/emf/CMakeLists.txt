# SPDX-License-Identifier: MIT
# kiln-emf.dll: loads unmodified EMF 0.3.3 mods (EmfApi v21) on Kiln. Deployed to <game>/kiln/plugins/.
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
    src/seh.cpp
    src/util.cpp)
target_include_directories(kiln-emf PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")

# The conformance probe (an EMF mod, mods/tests/emf-test/probe): bin/emf-test/emf_probe.dll, for labs only
# (tools/emf/lab-check.sh installs it into <game>/emf/mods/emf_probe/). Built against EMF's header alone.
add_library(emf-probe SHARED "${CMAKE_SOURCE_DIR}/../mods/tests/emf-test/probe/emf_probe.cpp")
target_include_directories(emf-probe PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
set_target_properties(emf-probe PROPERTIES OUTPUT_NAME emf_probe RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/emf-test")
