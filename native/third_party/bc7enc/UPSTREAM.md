# bc7enc_rdo (vendored subset)
- Source: https://github.com/richgel999/bc7enc_rdo (official repository), commit
  `b9438627eef73a1157e84201b6fa6eb2ffd6d9f0` (master, fetched 2026-11-04).
- Files: `bc7enc.cpp/.h` (BC7 encoder), `rgbcx.cpp/.h` + `rgbcx_table4*.h` (BC1/BC3/BC4/BC5 encoder),
  `bc7decomp.cpp/.h` (BC7 decoder, used by the self test). Unmodified.
- Licence: MIT or public domain (Unlicense), your choice: `LICENSE` (copied from the repository). Not vendored:
  `bc7e.ispc` (Apache 2.0), `lodepng` (zlib), `miniz`, the RDO encoder and the command-line tool.
- Used by: `native/services/assets` (kiln.assets) to encode PNG retextures to a texture's own BCn format.
  Also `native/services/vfx/cook` (VFX textures, materials) and `native/services/surfaces/cook` (building surfaces, which also decode a
  texture's last mip with `bc7decomp` and `rgbcx`).
