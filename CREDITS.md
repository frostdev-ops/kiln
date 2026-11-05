# Credits

Kiln builds on the work of the Enshrouded modding community.

| Project | Author / licence | Used for |
|---|---|---|
| [ShroudForge](https://github.com/bonsaibauer/shroudforge) | bonsaibauer, MIT ([licence](licenses/ShroudForge-LICENSE.txt)) | Kiln started as a fork of ShroudForge 1.7.8. Ported ideas and code: the 14-byte entry gate, whole-procedure matching for profile discovery (sigport), named-service resolution (settings). The fork itself now lives separately. |
| EMF, Enshrouded Modding Framework 0.3.3 | EMF contributors, MIT ([licence](licenses/EMF-LICENSE.txt)) | Engine maps, signatures and reverse-engineering notes (`docs/re/emf-core.md`); the EMF API is the reference for Kiln's EMF compatibility. |
| Enshrouded Server Guardian, Enshrouded Save Editor, Enshrouded Interactive Map | Nicolas Ferreira, MIT ([editor](licenses/Enshrouded-Save-Editor-LICENSE.txt), [map](licenses/Enshrouded-Interactive-Map-LICENSE.txt)) | Call-site hook reference (Guardian); the launcher's character editor ([notes](launcher/src/editor/UPSTREAM.md)); the launcher's Leaflet map and search ([notes](launcher/src/admin/map/UPSTREAM.md)). The map's relief shader is Kiln's own. |
| kfc-parser | brabb3l, GPL-3 | Knowledge from its documentation only. No kfc-parser code is part of Kiln. |
| [safetyhook](https://github.com/cursey/safetyhook) | cursey, BSL-1.0; Zydis/Zycore, MIT | Detours and mid-function hooks (`native/third_party/safetyhook`). |
| [Dear ImGui](https://github.com/ocornut/imgui) | Omar Cornut, MIT | In-game overlay. |
| [nlohmann/json](https://github.com/nlohmann/json) | Niels Lohmann, MIT | JSON in the native core. |
| .NET | .NET Foundation, MIT | The private runtime Kiln hosts mods in. |
| [netcoredbg](https://github.com/Samsung/netcoredbg) 3.2.0-1092 | Samsung Electronics, MIT; its release includes dbgshim (.NET, MIT) and Roslyn 2.3 (Microsoft, Apache-2.0) ([licences](licenses/netcoredbg.txt)) | The mod debugger, bundled unmodified in the VS Code extension (`tools/vscode`). |
| [stb](https://github.com/nothings/stb), stb_vorbis | Sean Barrett, MIT or public domain ([stb](native/third_party/stb/LICENSE), [stb_vorbis](native/third_party/stb_vorbis/LICENSE)) | Image and Ogg Vorbis decoding (`native/third_party`). |
| [dr_libs](https://github.com/mackron/dr_libs) | David Reid, public domain or MIT-0 ([licence](native/third_party/dr_libs/LICENSE)) | Audio file decoding. |
| [speexdsp](https://github.com/xiph/speexdsp) | Xiph.org Foundation and contributors, BSD-3-Clause ([licence](native/third_party/speexdsp/COPYING)) | Audio resampling. |
| [bc7enc](https://github.com/richgel999/bc7enc_rdo) | Binomial LLC and contributors ([licence](native/third_party/bc7enc/LICENSE)) | Texture compression for icons and retextures. |
| [QOA](https://github.com/phoboslab/qoa) | Dominic Szablewski, MIT ([licence](licenses/QOA-LICENSE.txt)) | The sound encoder and decoder are ported from its reference code (`tools/soundcook/native`). |
| [Vulkan-Headers](https://github.com/KhronosGroup/Vulkan-Headers) | The Khronos Group, Apache-2.0 or MIT ([licence](native/third_party/imgui/vulkan-headers/LICENSE.md)) | Headers for the overlay's Vulkan backend. |
| [Inter](https://github.com/rsms/inter), [Fraunces](https://github.com/undercasetype/Fraunces), [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) | their authors, SIL OFL 1.1 ([Inter](launcher/src/assets/fonts/Inter-OFL.txt), [Fraunces](launcher/src/assets/fonts/Fraunces-OFL.txt), [JetBrains Mono](launcher/src/assets/fonts/JetBrainsMono-OFL.txt)) | The launcher's fonts. |
| frostdev | frostdev.io | Kiln launcher theme and design. |

Enshrouded is a trademark of Keen Games GmbH. Kiln is not affiliated with or endorsed by Keen Games.
