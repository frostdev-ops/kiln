# SPDX-License-Identifier: MIT
# Kiln's Linux release build environment (docs/kiln/launcher/releasing.md, "Linux builds"). release.sh compiles and
# bundles every Linux ELF of a release in here: the launcher (AppImage, deb, rpm), kilnctl and kiln. A binary needs the
# glibc it was linked against or newer, and the AppImage carries the build system's libraries (WebKitGTK, GTK,
# sqlite, ...), so building on Ubuntu 22.04 (glibc 2.35, the oldest base Tauri 2's WebKitGTK 4.1 exists on) gives
# a glibc baseline suitable for SteamOS and newer distros; their other desktop libraries are still required.
# A build on a rolling distro needs its brand-new glibc (2.44 on the build machine in 2026-11) and fails everywhere else.
#
# The image is tagged kiln-linux-build:<first 12 hex of this file's sha256> (tools/release/linux-build/image.sh), so an
# edit here makes a new image. RUST_VERSION below is the release's Rust version: release.sh reads it from this file
# and builds the Windows files on the host with the same toolchain (RUSTUP_TOOLCHAIN), so it is raised here and
# nowhere else (the host needs that toolchain with the x86_64-pc-windows-msvc target; release.sh says how when it is
# missing). bun is the host's bun (2026-11-06: 1.4.2). Advance APT_SNAPSHOT deliberately for security
# updates (especially WebKitGTK); rebuilding an unchanged Dockerfile uses the same archive snapshot.

# ubuntu:22.04 (jammy), pinned by the digest of its multi-arch index (2026-10-25 build)
FROM ubuntu:22.04@sha256:076e7bf82319619f28dd0aa9c02ab34b055ad37100395b4f49a2ffba04fb5e11

ARG DEBIAN_FRONTEND=noninteractive
ARG APT_SNAPSHOT=20261106T000000Z
# Tauri 2's Linux prerequisites (libwebkit2gtk-4.1-dev brings GTK 3, libsoup 3 and JavaScriptCore), the tray library
# (libayatana-appindicator3-dev), dbus (the `dbus` crate), the AppImage tools' needs (file, patchelf, the gdk-pixbuf
# loaders and SVG support the GTK plugin bundles), python3 for notices.py (the AppImage's libraries come from this
# image's dpkg database). The snapshot service is https only and the base image has no CA certificates, so
# ca-certificates comes first from the base image's own archive; everything else (ca-certificates again included)
# comes from the pinned snapshot.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && sed -i "s/^deb /deb [snapshot=${APT_SNAPSHOT}] /" /etc/apt/sources.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      build-essential pkg-config ca-certificates curl unzip file patchelf \
      libwebkit2gtk-4.1-dev libgtk-3-dev libayatana-appindicator3-dev librsvg2-dev libdbus-1-dev \
      libgdk-pixbuf2.0-bin librsvg2-common gsettings-desktop-schemas shared-mime-info \
      python3 \
 && rm -rf /var/lib/apt/lists/*

# Rust: rustup-init (checked by sha256), the minimal profile, one pinned toolchain. CARGO_HOME is overridden at run
# time (run.sh: a cache folder on the host); the proxies stay in /usr/local/cargo/bin.
ENV RUSTUP_HOME=/usr/local/rustup CARGO_HOME=/usr/local/cargo PATH=/usr/local/cargo/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
ARG RUSTUP_VERSION=1.29.2
ARG RUSTUP_SHA256=5be191e8c70a5007074254cb74d78dbfdd25cd5a61c27de7b8e5410122ec24f4
ARG RUST_VERSION=1.99.1
RUN curl -fsSLo /tmp/rustup-init "https://static.rust-lang.org/rustup/archive/${RUSTUP_VERSION}/x86_64-unknown-linux-gnu/rustup-init" \
 && echo "${RUSTUP_SHA256}  /tmp/rustup-init" | sha256sum -c - \
 && chmod +x /tmp/rustup-init \
 && /tmp/rustup-init -y --no-modify-path --profile minimal --default-toolchain "${RUST_VERSION}" \
 && rm /tmp/rustup-init \
 && chmod -R a+rX "$RUSTUP_HOME" "$CARGO_HOME" \
 && rustc --version && cargo --version

# bun: runs the Tauri CLI and the launcher's web build (build-linux.sh), as on the host
ARG BUN_VERSION=1.4.2
ARG BUN_SHA256=36368faef7527875d5ffa52e53cd48021741f2a83eb6208a8dd64068d422a913
RUN curl -fsSLo /tmp/bun.zip "https://github.com/oven-sh/bun/releases/download/bun-v${BUN_VERSION}/bun-linux-x64.zip" \
 && echo "${BUN_SHA256}  /tmp/bun.zip" | sha256sum -c - \
 && unzip -q -j /tmp/bun.zip bun-linux-x64/bun -d /usr/local/bin \
 && rm /tmp/bun.zip \
 && chmod 755 /usr/local/bin/bun \
 && bun --version

# linuxdeploy (Tauri's AppImage tool) is an AppImage itself: extract-and-run needs no FUSE in the container
ENV APPIMAGE_EXTRACT_AND_RUN=1 NO_STRIP=true
LABEL org.opencontainers.image.title="kiln-linux-build" \
      org.opencontainers.image.description="Kiln's Linux release build environment (Ubuntu 22.04, glibc 2.35)" \
      org.opencontainers.image.source="https://github.com/frostdev-ops/kiln"
