# SPDX-License-Identifier: MIT
# kiln-flame.dll: starts Flame mods (mods/<id>/flame.json, e.g. Creative Mode) on Kiln and publishes kiln.flame
# (include/kiln_flame.h). Kiln's own code: nothing of Flame's loader is used. Deployed to <game>/kiln/plugins/.
kiln_plugin(flame src/plugin.cpp)
target_include_directories(kiln-flame PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
