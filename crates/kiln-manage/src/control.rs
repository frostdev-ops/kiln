//! The control folder `kiln/control/<name>.cmd` -> `<name>.result.json` (architecture.md: mods, load <folder>,
//! unload <id>, reload <id>, reload-test <id> <n>, gc, and service commands; Kiln.Host polls it on the tick thread)
//! and the console channel `kiln/control/console/<name>.cmd` -> `<name>.result.json` (Kiln.Chat: command lines as the
//! console, `#as <source>` directives, `#complete <partial>`). Works on clients and servers while the game runs.
//! The writer renames a finished `.tmp` to `.cmd`; Kiln writes `<name>.result.json.tmp` and renames it, so a result
//! file that exists is complete.

use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

pub const CONTROL: &str = "kiln/control";
pub const CONSOLE: &str = "kiln/control/console";

/// A unique request name: `<tag>-<pid>-<nanos>`.
pub fn request_name(tag: &str) -> String {
    let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
    format!("{tag}-{}-{nanos}", std::process::id())
}

/// Writes `kiln/control/<unique>.cmd` with `command` and waits up to `timeout` for its result.
pub fn send(game_dir: &Path, command: &str, timeout: Duration) -> Result<serde_json::Value> {
    let kiln = game_dir.join("kiln");
    if !kiln.is_dir() {
        return Err(Error::not_found(format!("{} has no kiln/ folder (deploy Kiln first)", game_dir.display())));
    }
    let raw = exchange(&game_dir.join(CONTROL), &request_name("kiln-manage"), &format!("{}\n", command.trim()), timeout)?;
    parse_result(&raw)
}

/// `--as` spec -> the console directive: console | remote:<name>[:user|:admin] | steam:<id>[:name] | player:<name>.
pub fn console_directive(spec: &str) -> Result<String> {
    let parts: Vec<&str> = spec.splitn(3, ':').collect();
    Ok(match parts.as_slice() {
        ["console"] => "#as console".into(),
        ["remote", name] => format!("#as remote \"{name}\" admin"),
        ["remote", name, "user"] => format!("#as remote \"{name}\" user"),
        ["remote", name, "admin"] => format!("#as remote \"{name}\" admin"),
        ["steam", id] => format!("#as steam {id}"),
        ["steam", id, name] => format!("#as steam {id} \"{name}\""),
        ["player", name] => format!("#as player \"{name}\""),
        _ => {
            return Err(Error::invalid(
                "--as takes console, remote:<name>[:user], steam:<SteamID64>[:name] or player:<name>",
            ));
        }
    })
}

/// The text of a console request: an optional `#as` directive line, then the line (or `#complete <line>`).
pub fn console_text(line: &str, source: Option<&str>, complete: bool) -> Result<String> {
    let mut text = String::new();
    if let Some(s) = source {
        text.push_str(&console_directive(s)?);
        text.push('\n');
    }
    let line = line.trim_start_matches('\n');
    if complete {
        text.push_str("#complete ");
        text.push_str(line);
    } else {
        text.push_str(line.trim());
    }
    text.push('\n');
    Ok(text)
}

/// Sends a console request (`console_text`) and returns the raw result JSON text.
pub fn console(game_dir: &Path, text: &str, timeout: Duration) -> Result<String> {
    let kiln = game_dir.join("kiln");
    if !kiln.is_dir() {
        return Err(Error::not_found(format!("{} has no kiln/ folder (deploy Kiln first)", game_dir.display())));
    }
    exchange(&game_dir.join(CONSOLE), &request_name("kilnctl"), text, timeout)
}

/// Writes `<dir>/<name>.tmp`, renames it to `.cmd`, waits for `<name>.result.json`, returns its text and deletes it.
/// On timeout the `.cmd` is removed and the error's detail says whether Kiln had picked the file up
/// (`{"picked": bool}`).
pub fn exchange(dir: &Path, name: &str, text: &str, timeout: Duration) -> Result<String> {
    std::fs::create_dir_all(dir).map_err(crate::util::io_at(dir, "creating"))?;
    let tmp = dir.join(format!("{name}.tmp"));
    let cmd = dir.join(format!("{name}.cmd"));
    let result = dir.join(format!("{name}.result.json"));
    std::fs::write(&tmp, text).map_err(crate::util::io_at(&tmp, "writing"))?;
    std::fs::rename(&tmp, &cmd).map_err(crate::util::io_at(&cmd, "renaming to"))?;
    let start = Instant::now();
    while !result.exists() {
        if start.elapsed() > timeout {
            let picked = !cmd.exists();
            let _ = std::fs::remove_file(&cmd);
            return Err(Error::new(
                ErrorCode::Timeout,
                format!(
                    "no answer within {} s{}",
                    timeout.as_secs(),
                    if picked { " although the file was picked up" } else { "" }
                ),
            )
            .with_detail(serde_json::json!({ "picked": picked })));
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let raw = std::fs::read_to_string(&result).map_err(crate::util::io_at(&result, "reading"))?;
    let _ = std::fs::remove_file(&result);
    Ok(raw)
}

/// A result file's JSON (`{ok, command, message, data...}`).
pub fn parse_result(raw: &str) -> Result<serde_json::Value> {
    serde_json::from_str(raw).map_err(|e| Error::new(ErrorCode::Parse, format!("the result is not JSON: {e}")))
}

/// Pending (not yet picked up) request files in a control folder: a sign that Kiln is not polling.
pub fn pending(dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "cmd"))
        .collect();
    v.sort();
    v
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn directives_and_text() {
        assert_eq!(console_directive("console").unwrap(), "#as console");
        assert_eq!(console_directive("remote:ew").unwrap(), "#as remote \"ew\" admin");
        assert_eq!(console_directive("remote:ew:user").unwrap(), "#as remote \"ew\" user");
        assert_eq!(console_directive("steam:7656:Fel").unwrap(), "#as steam 7656 \"Fel\"");
        assert_eq!(console_directive("player:Fel").unwrap(), "#as player \"Fel\"");
        assert!(console_directive("bogus").is_err());
        assert_eq!(console_text(" time set 7 ", Some("console"), false).unwrap(), "#as console\ntime set 7\n");
        assert_eq!(console_text("ti", None, true).unwrap(), "#complete ti\n");
    }

    #[test]
    fn exchange_round_trip_and_timeout() {
        let d = std::env::temp_dir().join(format!("km-control-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        let dir = d.join("kiln/control");
        std::fs::create_dir_all(&dir).unwrap();
        let answer = std::thread::spawn({
            let dir = dir.clone();
            move || {
                for _ in 0..200 {
                    if let Some(cmd) = pending(&dir).into_iter().next() {
                        let text = std::fs::read_to_string(&cmd).unwrap();
                        std::fs::remove_file(&cmd).unwrap();
                        let res = cmd.with_extension("result.json");
                        let tmp = cmd.with_extension("result.json.tmp");
                        std::fs::write(&tmp, format!("{{\"ok\": true, \"command\": {:?}}}", text.trim())).unwrap();
                        std::fs::rename(tmp, res).unwrap();
                        return;
                    }
                    std::thread::sleep(Duration::from_millis(10));
                }
            }
        });
        let v = send(&d, "mods", Duration::from_secs(5)).unwrap();
        answer.join().unwrap();
        assert_eq!(v["command"], "mods");
        let e = send(&d, "gc", Duration::from_millis(100)).unwrap_err();
        assert_eq!(e.code, ErrorCode::Timeout);
        assert_eq!(e.detail.unwrap()["picked"], false);
        assert!(pending(&dir).is_empty());
        let _ = std::fs::remove_dir_all(&d);
    }
}
