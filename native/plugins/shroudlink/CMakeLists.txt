# SPDX-License-Identifier: MIT
# kiln-shroudlink.dll: shroudlink's server core (bjck/shroudlink v0.8.0, MIT; upstream/) hosted as a Kiln plugin, so
# shroudlink mods (shroudlink_mods/*.dll, profile mods.shroudlink) run without its steam_api64.dll proxy. A Rust
# cdylib: build.sh builds it with cargo xwin into bin/plugins/. Not part of "all" (needs cargo xwin); deploy-server.sh
# and the packager build each native/plugins/<dir> as its own target kiln-<dir>, which runs this.
# CARGO_TARGET_DIR defaults to <KILN_OUT>/cargo-shroudlink (build.sh).
get_filename_component(KILN_SHROUDLINK_OUT "${CMAKE_BINARY_DIR}" DIRECTORY)
add_custom_target(kiln-shroudlink
    COMMAND ${CMAKE_COMMAND} -E env "KILN_OUT=${KILN_SHROUDLINK_OUT}"
            "${CMAKE_CURRENT_SOURCE_DIR}/build.sh" --out "${CMAKE_BINARY_DIR}/bin"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    USES_TERMINAL
    COMMENT "kiln-shroudlink (cargo xwin)")
