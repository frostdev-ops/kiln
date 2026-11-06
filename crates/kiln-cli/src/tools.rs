// SPDX-License-Identifier: MIT
//! Where things are: the Kiln repository (cook tools, labs), the MDK feed, the Kiln.Sdk native folder, external tools.

use crate::out::CliError;
use anyhow::Result;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitStatus};

const VERSION_PROPS: &str = include_str!("../../../managed/Kiln.Sdk/Sdk/Kiln.Version.props");

/// The MDK version this kiln came with: the release's (`KILN_SDK_VERSION` when it was built, tools/release/release.sh), else
/// Kiln.Sdk's base version (Kiln.Version.props), which a feed packed from a working tree extends (`<base>-dev.local.<date>.<time>`).
pub fn sdk_version() -> &'static str {
    if let Some(v) = option_env!("KILN_SDK_VERSION").filter(|v| !v.is_empty()) {
        return v;
    }
    let s = VERSION_PROPS;
    s.split("<KilnSdkVersion>").nth(1).and_then(|r| r.split("</KilnSdkVersion>").next()).unwrap_or("0.0.0").trim()
}

/// `<id>.<version>.nupkg` in a feed: this kiln's own version when the feed has it, else the newest package of the same base
/// version (by file time), which is what managed/Kiln.Sdk/pack.sh made last from a working tree. The version is the second value.
pub fn feed_package(feed: &Path, id: &str) -> Option<(PathBuf, String)> {
    let v = sdk_version();
    let exact = feed.join(format!("{id}.{v}.nupkg"));
    if exact.is_file() {
        return Some((exact, v.to_string()));
    }
    let base = v.split(['-', '+']).next().unwrap_or(v);
    let prefix = format!("{id}.{base}");
    std::fs::read_dir(feed)
        .ok()?
        .flatten()
        .filter_map(|e| {
            let name = e.file_name().to_string_lossy().into_owned();
            let ver = name.strip_suffix(".nupkg")?.strip_prefix(&format!("{id}."))?.to_string();
            let ok = name.starts_with(&prefix) && (ver == base || ver.starts_with(&format!("{base}-")) || ver.starts_with(&format!("{base}+")));
            ok.then(|| (e.metadata().and_then(|m| m.modified()).ok(), e.path(), ver))
        })
        .max_by_key(|(t, _, _)| *t)
        .map(|(_, p, ver)| (p, ver))
}

/// The Kiln repository: $KILN_REPO, else the checkout this kiln was built from (if it is still there; a release build,
/// with `KILN_RELEASE_BUILD` set while compiling, knows no checkout).
pub fn repo() -> Option<PathBuf> {
    if let Some(r) = std::env::var_os("KILN_REPO").filter(|v| !v.is_empty()) {
        return Some(PathBuf::from(r));
    }
    const TREE: &str = match option_env!("KILN_RELEASE_BUILD") {
        Some(_) => "",
        None => env!("CARGO_MANIFEST_DIR"),
    };
    if TREE.is_empty() {
        return None;
    }
    let built = Path::new(TREE).join("../..");
    built.join("managed/Kiln.Sdk/Sdk/Sdk.props").is_file().then(|| built.canonicalize().unwrap_or(built))
}

pub fn need_repo(what: &str) -> Result<PathBuf> {
    repo().ok_or_else(|| {
        CliError::new("not_found", format!("{what} needs a Kiln checkout: set KILN_REPO to it (the tools live in <repo>/tools)")).into()
    })
}

/// The local NuGet feed with Kiln.Sdk / Kiln.Api / Kiln.Templates: --feed, $KILN_FEED, $KILN_OUT/nuget,
/// <data>/sdk/nuget.
pub fn feed(explicit: Option<&Path>) -> Option<PathBuf> {
    if let Some(f) = explicit {
        return Some(f.to_path_buf());
    }
    if let Some(f) = std::env::var_os("KILN_FEED").filter(|v| !v.is_empty()) {
        return Some(PathBuf::from(f));
    }
    if let Some(o) = std::env::var_os("KILN_OUT").filter(|v| !v.is_empty()) {
        let f = PathBuf::from(o).join("nuget");
        if f.is_dir() {
            return Some(f);
        }
    }
    let d = kiln_manage::paths::DataDirs::locate().ok()?.root.join("sdk/nuget");
    d.is_dir().then_some(d)
}

fn nuget_packages() -> Option<PathBuf> {
    if let Some(p) = std::env::var_os("NUGET_PACKAGES").filter(|v| !v.is_empty()) {
        return Some(PathBuf::from(p));
    }
    Some(kiln_manage::util::home().join(".nuget/packages"))
}

/// Kiln.Sdk's native/ folder (KilnPlugin.cmake, the ABI headers, the Linux toolchain): $KILN_SDK_DIR, the restored
/// package, the package unpacked from the feed into <data>/sdk/, or the checkout's managed/Kiln.Sdk/native.
pub fn sdk_native_dir(feed_hint: Option<&Path>) -> Result<PathBuf> {
    if let Some(d) = std::env::var_os("KILN_SDK_DIR").filter(|v| !v.is_empty()) {
        return Ok(PathBuf::from(d));
    }
    let own = sdk_version();
    let found = feed(feed_hint).and_then(|f| feed_package(&f, "Kiln.Sdk"));
    let v = found.as_ref().map_or(own, |(_, v)| v.as_str());
    if let Some(p) = nuget_packages().map(|p| p.join("kiln.sdk").join(v.to_lowercase()).join("native")) {
        if p.join("cmake/KilnPlugin.cmake").is_file() {
            return Ok(p);
        }
    }
    if let Some((nupkg, _)) = &found {
        let dest = kiln_manage::paths::DataDirs::locate()?.root.join("sdk").join(format!("kiln.sdk-{v}"));
        if !dest.join("native/cmake/KilnPlugin.cmake").is_file() || newer(nupkg, &dest.join("native")) {
            let _ = unzip_native(nupkg, &dest); // an older package without native/: try the next source
        }
        if dest.join("native/cmake/KilnPlugin.cmake").is_file() {
            return Ok(dest.join("native"));
        }
    }
    if let Some(r) = repo() {
        let d = r.join("managed/Kiln.Sdk/native");
        if d.join("cmake/KilnPlugin.cmake").is_file() {
            return Ok(d);
        }
    }
    Err(CliError::new("not_found", "no Kiln native SDK: set KILN_SDK_DIR, KILN_FEED (the MDK feed) or KILN_REPO").into())
}

fn newer(a: &Path, b: &Path) -> bool {
    let m = |p: &Path| std::fs::metadata(p).and_then(|m| m.modified()).ok();
    matches!((m(a), m(b)), (Some(x), Some(y)) if x > y)
}

/// Unpacks `native/**` of a Kiln.Sdk .nupkg with the system's unzip (bsdtar on Windows 10+: `tar -xf`). Without the
/// entries' times: NuGet stores UTC, unzip reads it as local time, and a file from the future west of Greenwich makes
/// ninja re-run CMake forever ("manifest 'build.ninja' still dirty after 100 tries").
fn unzip_native(nupkg: &Path, dest: &Path) -> Result<()> {
    std::fs::create_dir_all(dest)?;
    let ok = if cfg!(windows) {
        Command::new("tar").arg("-xmf").arg(nupkg).arg("-C").arg(dest).arg("native").status()
    } else {
        Command::new("unzip").arg("-qq").arg("-DD").arg("-o").arg(nupkg).arg("native/*").arg("-d").arg(dest).stderr(std::process::Stdio::null()).status()
    };
    match ok {
        Ok(s) if s.success() => Ok(()),
        _ => Err(CliError::new("tool_missing", format!("could not unpack {} (unzip/tar)", nupkg.display())).into()),
    }
}

pub fn python() -> Result<String> {
    for p in ["python3", "python"] {
        if kiln_manage::util::which(p).is_some() {
            return Ok(p.to_string());
        }
    }
    Err(CliError::new("tool_missing", "python3 is needed for the cook tools").into())
}

pub fn require(tool: &str, hint: &str) -> Result<()> {
    if kiln_manage::util::which(tool).is_none() {
        return Err(CliError::new("tool_missing", format!("`{tool}` is not on PATH ({hint})")).into());
    }
    Ok(())
}

/// Runs a command with inherited stdout/stderr (stdout goes to stderr under --json so stdout stays one document).
pub fn run_passthrough(cmd: &mut Command, json: bool) -> Result<ExitStatus> {
    if json {
        cmd.stdout(std::process::Stdio::from(std::io::stderr()));
    }
    let shown = format!("{cmd:?}");
    cmd.status().map_err(|e| CliError::new("tool_missing", format!("cannot run {shown}: {e}")).into())
}

/// The installed Enshrouded client, for cook tools (they read the game; never write it): $KILN_GAME, else Steam.
pub fn client_dir() -> Option<PathBuf> {
    if let Some(g) = std::env::var_os("KILN_GAME").filter(|v| !v.is_empty()) {
        return Some(PathBuf::from(g));
    }
    kiln_manage::game::detect()
        .ok()?
        .into_iter()
        .find(|g| matches!(g.kind, kiln_manage::game::GameKind::Client))
        .map(|g| g.path)
}
