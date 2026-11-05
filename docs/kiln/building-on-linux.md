# Building Kiln on Linux

Every Windows x64 binary in this repository cross-builds on Linux. No Visual Studio, no Windows VM.

```sh
native/build.sh [targets…]          # Kiln's native DLLs: proxy, core, services, plugins (incremental)
dotnet build managed/…              # managed side (see docs/kiln/modding/mdk.md)
cargo build                         # kilnctl, kiln-manage, the launcher backend (root Cargo workspace)
```

## Toolchain
| Need | Arch package / command |
|---|---|
| Rust + MSVC target | `rustup target add x86_64-pc-windows-msvc` |
| cargo-xwin (downloads the MSVC CRT + Windows SDK to `~/.cache/cargo-xwin/xwin`) | `cargo install cargo-xwin` |
| clang-cl, lld-link, llvm-rc, llvm-lib, llvm-mt | `clang lld llvm` |
| CMake + Ninja | `cmake ninja` |
| Node + npm (modloader/updater frontend, vite) | `nodejs npm` |
| .NET SDK 10 (managed side) | `dotnet-sdk` |

The xwin cache only has the release CRT, so everything links the static release runtime (`/MT`).

