# SPDX-License-Identifier: MIT
# kiln-greeneye.dll: loads unmodified Greeneye Loader mods (GreeneyeHost API 2) on Kiln. Deployed to <game>/kiln/plugins/.
# include/greeneye/*.h are Greeneye Loader's own headers and src/hot.cpp is ported from it (MIT, Greeneye0;
# licenses/GreeneyeLoader-LICENSE.txt).
kiln_plugin(greeneye
    src/plugin.cpp
    src/hot.cpp)
target_include_directories(kiln-greeneye PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
