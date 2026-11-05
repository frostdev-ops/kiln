//! Progress reporting for long operations (install, import, modpack download, server start). The launcher turns
//! reports into `kiln://task` events; kilnctl prints them.

/// Receives progress from a long operation. `fraction` is 0..=1, or None when unknown.
pub trait Progress {
    fn report(&mut self, fraction: Option<f32>, message: &str);
    /// Polled between steps; an operation that sees true stops and returns `ErrorCode::Canceled`.
    fn canceled(&self) -> bool {
        false
    }
}

/// Ignores every report.
pub struct NoProgress;

impl Progress for NoProgress {
    fn report(&mut self, _fraction: Option<f32>, _message: &str) {}
}

impl<F: FnMut(Option<f32>, &str)> Progress for F {
    fn report(&mut self, fraction: Option<f32>, message: &str) {
        self(fraction, message)
    }
}
