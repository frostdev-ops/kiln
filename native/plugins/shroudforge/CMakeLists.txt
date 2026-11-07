# SPDX-License-Identifier: MIT
# kiln-shroudforge.dll: hosts the player's installed ShroudForge runtime (github.com/bonsaibauer/shroudforge, MIT) on
# Kiln, so ShroudForge's Lua mods load unchanged. Deployed to <game>/kiln/plugins/. Links no ShroudForge code: it loads
# <game>/shroudforge/shroudforge-runtime.dll and kfc-runtime.dll through their exported C functions.
# Docs: docs/kiln/shroudforge-compat.md.
kiln_plugin(shroudforge
    src/plugin.cpp
    src/processes.cpp)
