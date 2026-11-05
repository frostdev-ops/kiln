# speexdsp resampler (vendored subset)
- Source: https://github.com/xiph/speexdsp (official repository, Xiph.Org Foundation / Jean-Marc Valin), commit
  `7a158783df74efe7c2d1c6ee8363c1e695c71226` (master, fetched 2026-11-04).
- Files: `resample.c` and `arch.h` (from `libspeexdsp/`), `speex_resampler.h` (from `include/speex/`). Unmodified.
  Built standalone with `OUTSIDE_SPEEX`, `RANDOM_PREFIX=kiln_speex` (symbols `kiln_speex_resampler_*`),
  `FLOATING_POINT`; no SSE/NEON paths (`USE_SSE` off), so the output is the same on every platform built with
  `-ffp-contract=off` (`tools/soundcook/native/CMakeLists.txt`).
- Licence: BSD-3-Clause: `COPYING` (copied from the repository); authors in `AUTHORS`. Not the rest of speexdsp
  (echo canceller, preprocessor, jitter buffer) and nothing from Speex the codec.
- Used by: `tools/soundcook/native` (kiln-soundcook) to resample mod audio to the game's only rate, 48,000 Hz.
