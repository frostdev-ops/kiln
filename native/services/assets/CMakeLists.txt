# kiln.assets: mod-supplied resources and overrides. Links bcrypt (SHA-256 content hashes); embeds stb_image and
# a BCn encoder subset of bc7enc_rdo (native/third_party/{stb,bc7enc}, MIT/Unlicense).
kiln_service(assets
    assets.cpp
    catalog.cpp
    contained.cpp
    cooktrust.cpp
    scan.cpp
    hooks.cpp
    store.cpp
    image.cpp
    util.cpp
    ${CMAKE_SOURCE_DIR}/third_party/bc7enc/bc7enc.cpp
    ${CMAKE_SOURCE_DIR}/third_party/bc7enc/rgbcx.cpp
    ${CMAKE_SOURCE_DIR}/third_party/bc7enc/bc7decomp.cpp)
target_link_libraries(kiln-assets PRIVATE bcrypt)
set_source_files_properties(
    ${CMAKE_SOURCE_DIR}/third_party/bc7enc/bc7enc.cpp
    ${CMAKE_SOURCE_DIR}/third_party/bc7enc/rgbcx.cpp
    ${CMAKE_SOURCE_DIR}/third_party/bc7enc/bc7decomp.cpp
    PROPERTIES COMPILE_OPTIONS "-w")
