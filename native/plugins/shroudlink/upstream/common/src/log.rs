//! Minimal append-only file logger (`chestsort.log` next to the executable).

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

static LOG: Mutex<Option<File>> = Mutex::new(None);
/// kiln: a second destination for every line (Kiln's log), set by the Kiln plugin before logging starts.
static SINK: std::sync::OnceLock<fn(&str)> = std::sync::OnceLock::new();

/// kiln: every later line also goes to `sink` (once; a second call is ignored).
pub fn set_sink(sink: fn(&str)) {
    let _ = SINK.set(sink);
}

pub fn init(file_name: &str) {
    let path = crate::game_dir().join(file_name);
    if let Ok(file) = OpenOptions::new().create(true).append(true).open(path) {
        *LOG.lock().unwrap() = Some(file);
    }
}

pub fn write(msg: std::fmt::Arguments) {
    let secs = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    if let Some(file) = LOG.lock().unwrap().as_mut() {
        let _ = writeln!(file, "[{secs}] {msg}");
        let _ = file.flush();
    }
    if let Some(sink) = SINK.get() {
        sink(&msg.to_string()); // kiln
    }
}

#[macro_export]
macro_rules! log {
    ($($arg:tt)*) => { $crate::log::write(format_args!($($arg)*)) };
}
