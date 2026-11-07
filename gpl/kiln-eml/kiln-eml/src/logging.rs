// SPDX-License-Identifier: GPL-3.0-or-later
//! EML logs through `tracing`. This layer turns each event into one plain line ("message key=value ...") and hands it
//! to a sink: Kiln's log in the plugin, stderr in kiln-eml-run.

use std::fmt::Write as _;

use tracing::field::{Field, Visit};
use tracing::{Event, Level, Subscriber};
use tracing_subscriber::layer::{Context, Layer};

/// Receives (level, target, line).
pub type Sink = Box<dyn Fn(Level, &str, &str) + Send + Sync>;

pub struct LineLayer {
    sink: Sink,
    max: Level,
}

impl LineLayer {
    pub fn new(max: Level, sink: Sink) -> Self {
        Self { sink, max }
    }
}

#[derive(Default)]
struct Line {
    message: String,
    fields: String,
}

impl Visit for Line {
    fn record_str(&mut self, field: &Field, value: &str) {
        if field.name() == "message" {
            self.message.push_str(value);
        } else {
            let _ = write!(self.fields, " {}={}", field.name(), value);
        }
    }

    fn record_debug(&mut self, field: &Field, value: &dyn std::fmt::Debug) {
        if field.name() == "message" {
            let _ = write!(self.message, "{value:?}");
        } else {
            let _ = write!(self.fields, " {}={:?}", field.name(), value);
        }
    }
}

impl<S: Subscriber> Layer<S> for LineLayer {
    fn on_event(&self, event: &Event<'_>, _ctx: Context<'_, S>) {
        let level = *event.metadata().level();
        if level > self.max {
            return;
        }
        let mut line = Line::default();
        event.record(&mut line);
        let text = format!("{}{}", line.message, line.fields);
        (self.sink)(level, event.metadata().target(), &text);
    }
}
