//! SteamCMD: install / update app 2278520 (anonymous) into a server folder.

use crate::task::Progress;
use crate::{Error, Result};
use std::path::Path;

/// `app_update 2278520 validate` into `dir` (anonymous). Progress from SteamCMD's "progress: NN.NN" lines.
pub fn install(dir: &Path, progress: &mut dyn Progress) -> Result<()> {
    std::fs::create_dir_all(dir).map_err(crate::util::io_at(dir, "creating"))?;
    let mut canceled = false;
    let r = app_update(dir, &mut |l: &str| {
        if canceled || l.trim().is_empty() {
            return;
        }
        canceled = progress.canceled();
        progress.report(parse_progress(l), l.trim());
    });
    r?;
    if !dir.join(super::game::EXE).exists() {
        return Err(Error::new(crate::ErrorCode::Io, format!("steamcmd reported success but {} is missing", dir.join(super::game::EXE).display())));
    }
    Ok(())
}

/// Like install; returns the new build id when it changed.
pub fn update(dir: &Path, progress: &mut dyn Progress) -> Result<Option<String>> {
    let before = buildid(dir);
    install(dir, progress)?;
    let after = buildid(dir);
    Ok(if after != before { after } else { None })
}

/// `Update state (0x61) downloading, progress: 45.23 (...)` -> 0.4523
pub fn parse_progress(line: &str) -> Option<f32> {
    let i = line.find("progress: ")? + "progress: ".len();
    let num: String = line[i..].chars().take_while(|c| c.is_ascii_digit() || *c == '.').collect();
    num.parse::<f32>().ok().map(|p| (p / 100.0).clamp(0.0, 1.0))
}

// ---- kilnctl's SteamCMD calls ------------------------------
// Anonymous login; the dedicated server is app 2278520, a Windows depot.

use crate::ErrorCode;
use std::io::{BufRead, BufReader};
use std::process::Stdio;

pub const APP_ID: &str = "2278520";

pub fn steamcmd_args(dir: &Path) -> Vec<String> {
    [
        "+@sSteamCmdForcePlatformType",
        "windows",
        "+force_install_dir",
        &dir.display().to_string(),
        "+login",
        "anonymous",
        "+app_update",
        APP_ID,
        "validate",
        "+quit",
    ]
    .iter()
    .map(|s| s.to_string())
    .collect()
}

/// Drops ANSI escape sequences (SteamCMD colours its output even into a pipe).
pub fn strip_ansi(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\u{1b}' {
            if chars.peek() == Some(&'[') {
                chars.next();
                for d in chars.by_ref() {
                    if d.is_ascii_alphabetic() {
                        break;
                    }
                }
            }
            continue;
        }
        out.push(c);
    }
    out
}

/// SteamCMD's exit status is unreliable (non-zero after a self-update, zero after some failures); its success line is.
pub fn is_success(line: &str) -> bool {
    line.contains(&format!("Success! App '{APP_ID}'"))
}

/// Installs or validates the server in `dir`; `line` receives the command line first (`steamcmd <args>`), then every
/// line SteamCMD prints (ANSI codes stripped).
pub fn app_update(dir: &Path, line: &mut dyn FnMut(&str)) -> Result<()> {
    let args = steamcmd_args(dir);
    line(&format!("steamcmd {}", args.join(" ")));
    let mut child = crate::hostenv::command("steamcmd")
        .args(&args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .spawn()
        .map_err(|e| Error::new(ErrorCode::NotFound, format!("starting steamcmd (is it installed and on PATH?): {e}")))?;
    let mut ok = false;
    let mut last_error = None;
    let out = child.stdout.take().ok_or_else(|| Error::new(ErrorCode::Internal, "steamcmd stdout"))?;
    for l in BufReader::new(out).split(b'\n') {
        let l = strip_ansi(&String::from_utf8_lossy(&l?).replace('\r', ""));
        line(&l);
        ok |= is_success(&l);
        if l.contains("ERROR!") || l.contains("Error!") {
            last_error = Some(l.trim().to_string());
        }
    }
    let st = child.wait()?;
    if !ok {
        return Err(Error::new(
            ErrorCode::Network,
            format!("steamcmd did not report success ({st}{})", last_error.map(|e| format!(", last error: {e}")).unwrap_or_default()),
        ));
    }
    Ok(())
}

/// `"buildid"` from an appmanifest (VDF).
pub fn parse_buildid(acf: &str) -> Option<String> {
    acf.lines().find_map(|l| {
        let mut parts = l.split('"').filter(|s| !s.trim().is_empty());
        (parts.next()? == "buildid").then(|| parts.next().map(str::to_string))?
    })
}

pub fn buildid(dir: &Path) -> Option<String> {
    let acf = std::fs::read_to_string(dir.join(format!("steamapps/appmanifest_{APP_ID}.acf"))).ok()?;
    parse_buildid(&acf)
}
