//! The file bridge for external admin tools (mods/Kiln.Admin/Bridge.cs): `requests/<id>.json` {"op", ...} ->
//! `results/<id>.json` {ok, op, ms, data | error}; `state.json` about once a second. Default folder
//! `<server>/shroudforge/exports/emberwatch`, or kiln/data/kiln-admin/config.json "bridge".

use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime};

pub struct FileBridge {
    pub dir: PathBuf,
}

impl FileBridge {
    pub fn new(dir: impl Into<PathBuf>) -> FileBridge {
        FileBridge { dir: dir.into() }
    }

    /// The bridge folder of a server folder: kiln/data/kiln-admin/config.json `bridge` (a host path, or a `Z:\`
    /// path as the game sees it), else `<server>/shroudforge/exports/emberwatch`.
    pub fn locate(server_dir: &Path) -> Result<FileBridge> {
        let cfg = server_dir.join("kiln/data/kiln-admin/config.json");
        if let Ok(text) = std::fs::read_to_string(&cfg) {
            if let Ok(v) = super::parse_jsonc(&text) {
                if let Some(b) = v.get("bridge").and_then(|b| b.as_str()).filter(|b| !b.is_empty()) {
                    let host = crate::winpath::z_to_unix(b).unwrap_or_else(|| PathBuf::from(b));
                    let host = if host.is_relative() { server_dir.join(host) } else { host };
                    return Ok(FileBridge::new(host));
                }
            }
        }
        Ok(FileBridge::new(server_dir.join("shroudforge/exports/emberwatch")))
    }

    /// The bridge looks served: requests/ exists and state.json changed within `fresh`.
    pub fn alive(&self, fresh: Duration) -> bool {
        self.dir.join("requests").is_dir() && self.state_age().is_some_and(|a| a <= fresh)
    }

    /// How long ago state.json was written.
    pub fn state_age(&self) -> Option<Duration> {
        let m = std::fs::metadata(self.dir.join("state.json")).ok()?.modified().ok()?;
        Some(SystemTime::now().duration_since(m).unwrap_or_default())
    }

    /// Runs one op: writes the request atomically, waits for the result (polling every 50 ms), deletes it.
    /// Err(OpFailed) with the op's error text when it answers ok=false; Err(Timeout) when no result comes in time.
    pub fn call(&self, op: &str, args: serde_json::Map<String, serde_json::Value>, timeout: Duration) -> Result<serde_json::Value> {
        let result = self.call_raw(op, args, timeout)?;
        if result.get("ok").and_then(|v| v.as_bool()) == Some(true) {
            return Ok(result.get("data").cloned().unwrap_or(serde_json::Value::Null));
        }
        let msg = result.get("error").and_then(|e| e.as_str()).unwrap_or("the op failed").to_string();
        Err(Error::new(ErrorCode::OpFailed, msg).with_detail(serde_json::json!({ "op": op, "transport": "file" })))
    }

    /// The raw result object `{ok, op, ms, data | error}` (as an external tool reads it).
    pub fn call_raw(&self, op: &str, args: serde_json::Map<String, serde_json::Value>, timeout: Duration) -> Result<serde_json::Value> {
        let requests = self.dir.join("requests");
        let results = self.dir.join("results");
        if !requests.is_dir() {
            return Err(Error::new(ErrorCode::AdminUnavailable, format!("no file bridge at {}", self.dir.display())));
        }
        let id = request_id()?;
        let mut body = serde_json::Map::new();
        body.insert("op".into(), serde_json::Value::String(op.to_string()));
        for (k, v) in args {
            if k != "op" {
                body.insert(k, v);
            }
        }
        // the temp name does not match the bridge's [A-Za-z0-9-]{1,64}.json pattern
        let tmp = requests.join(format!(".{id}.tmp"));
        let target = requests.join(format!("{id}.json"));
        std::fs::write(&tmp, serde_json::to_vec(&body)?).map_err(crate::util::io_at(&tmp, "writing"))?;
        crate::util::rename_replace(&tmp, &target)?;
        let out = results.join(format!("{id}.json"));
        let start = Instant::now();
        loop {
            if let Ok(bytes) = std::fs::read(&out) {
                if let Ok(v) = serde_json::from_slice::<serde_json::Value>(&bytes) {
                    let _ = std::fs::remove_file(&out);
                    return Ok(v);
                }
            }
            if start.elapsed() >= timeout {
                // nobody took it: take the request back so it does not run later
                let taken = std::fs::remove_file(&target).is_ok();
                return Err(Error::new(
                    ErrorCode::Timeout,
                    if taken { format!("{op}: the server did not pick up the request (not running, or no Kiln.Admin)") } else { format!("{op}: no answer within {} s", timeout.as_secs()) },
                ));
            }
            std::thread::sleep(Duration::from_millis(50));
        }
    }

    /// state.json (None when absent).
    pub fn state(&self) -> Result<Option<serde_json::Value>> {
        let p = self.dir.join("state.json");
        match std::fs::read(&p) {
            Ok(b) => Ok(Some(serde_json::from_slice(&b).map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", p.display())))?)),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(crate::util::io_at(&p, "reading")(e)),
        }
    }
}

fn request_id() -> Result<String> {
    let b = crate::util::random_bytes::<8>()?;
    Ok(format!("kl-{}", crate::hash::hex(&b)))
}
