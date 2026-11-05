# dr_libs: dr_wav, dr_flac, dr_mp3 (vendored subset)
- Source: https://github.com/mackron/dr_libs (official repository, David Reid), commit
  `35b5c392cc15456736c440a68dbe073178bbffe0` (master, fetched 2026-11-04).
- Files: `dr_wav.h` v0.14.6, `dr_flac.h` v0.13.4, `dr_mp3.h` v0.7.4. Unmodified.
- Licence: public domain (Unlicense) or MIT No Attribution (MIT-0), your choice: `LICENSE` (copied from the
  repository).
- Used by: `tools/soundcook/native` (kiln-soundcook, the offline sound cooker) to decode mod WAV, FLAC and MP3 sources.
  Not linked into the game-side DLLs.
