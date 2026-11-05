//! Runs per-file work on a few threads with progress and cancel: workers pull items from a shared counter; this
//! thread reports progress (the launcher's `Progress` is not `Sync`) and turns a cancel into a stop flag the workers
//! check between items.

use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use std::collections::BTreeMap;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::mpsc;
use std::time::Duration;

/// What a run made: files per group, bytes, failures (`label: message`).
#[derive(Debug, Default)]
pub struct RunReport {
    pub written: BTreeMap<String, u64>,
    pub bytes: u64,
    pub failed: Vec<String>,
}

impl RunReport {
    pub fn merge(&mut self, o: RunReport) {
        for (k, v) in o.written {
            *self.written.entry(k).or_default() += v;
        }
        self.bytes += o.bytes;
        self.failed.extend(o.failed);
    }
}

enum Msg {
    Done(&'static str, u64),
    Failed(String),
}

pub fn threads(t: Option<usize>) -> usize {
    t.unwrap_or_else(|| std::thread::available_parallelism().map_or(4, |x| x.get()).min(8)).max(1)
}

/// Runs `f` on every item. `f` returns (group, bytes written) or an error (recorded, the run goes on; a panic in a
/// decoder counts as an error). Progress: fraction `base..base+span`, message `"<verb>: <done> of <n> files"`.
#[allow(clippy::too_many_arguments)]
pub fn run_parallel<T: Sync>(
    items: &[T],
    threads_opt: Option<usize>,
    p: &mut dyn Progress,
    base: f32,
    span: f32,
    verb: &str,
    f: impl Fn(&T) -> Result<(&'static str, u64)> + Sync,
    label: impl Fn(&T) -> String + Sync,
) -> Result<RunReport> {
    let n = items.len();
    let next = AtomicUsize::new(0);
    let stop = AtomicBool::new(false);
    let (tx, rx) = mpsc::channel::<Msg>();
    let mut rep = RunReport::default();
    let mut canceled = false;
    let (f, label) = (&f, &label);
    std::thread::scope(|s| {
        for _ in 0..threads(threads_opt).min(n.max(1)) {
            let tx = tx.clone();
            let (next, stop) = (&next, &stop);
            s.spawn(move || {
                loop {
                    if stop.load(Ordering::Relaxed) {
                        break;
                    }
                    let i = next.fetch_add(1, Ordering::Relaxed);
                    let Some(item) = items.get(i) else { break };
                    let m = match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| f(item))) {
                        Ok(Ok((g, b))) => Msg::Done(g, b),
                        Ok(Err(e)) => Msg::Failed(format!("{}: {}", label(item), e.message)),
                        Err(_) => Msg::Failed(format!("{}: decoder panic", label(item))),
                    };
                    if tx.send(m).is_err() {
                        break;
                    }
                }
            });
        }
        drop(tx);
        let mut done = 0usize;
        loop {
            match rx.recv_timeout(Duration::from_millis(100)) {
                Ok(m) => {
                    done += 1;
                    match m {
                        Msg::Done(g, b) => {
                            *rep.written.entry(g.to_string()).or_default() += 1;
                            rep.bytes += b;
                        }
                        Msg::Failed(e) => rep.failed.push(e),
                    }
                    p.report(Some(base + span * done as f32 / n.max(1) as f32), &format!("{verb}: {done} of {n} files"));
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }
            if !canceled && p.canceled() {
                canceled = true;
                stop.store(true, Ordering::Relaxed);
            }
        }
    });
    if canceled {
        return Err(Error::new(ErrorCode::Canceled, "Canceled."));
    }
    Ok(rep)
}

/// Writes `bytes` to `path`, creating its folder; returns the length.
pub fn write_file(path: &std::path::Path, bytes: &[u8]) -> Result<u64> {
    if let Some(d) = path.parent() {
        std::fs::create_dir_all(d).map_err(crate::util::io_at(d, "creating"))?;
    }
    std::fs::write(path, bytes).map_err(crate::util::io_at(path, "writing"))?;
    Ok(bytes.len() as u64)
}
