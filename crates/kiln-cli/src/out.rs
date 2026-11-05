// SPDX-License-Identifier: MIT
//! Output helpers: `--json` prints one JSON document; errors become `{"ok":false,"error":{code,message,detail?}}`.

use serde_json::{Value, json};
use std::fmt;

/// An error with a stable code (`usage` exits 2, everything else 1).
#[derive(Debug)]
pub struct CliError {
    pub code: String,
    pub message: String,
    pub detail: Option<Value>,
}

impl CliError {
    pub fn new(code: &str, message: impl Into<String>) -> CliError {
        CliError { code: code.into(), message: message.into(), detail: None }
    }
    pub fn usage(message: impl Into<String>) -> CliError {
        CliError::new("usage", message)
    }
    pub fn with_detail(mut self, d: Value) -> CliError {
        self.detail = Some(d);
        self
    }
}

impl fmt::Display for CliError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for CliError {}

/// Prints the error and returns the exit code.
pub fn fail(e: &anyhow::Error, json: bool) -> i32 {
    let (code, message, detail) = if let Some(c) = e.downcast_ref::<CliError>() {
        (c.code.clone(), c.message.clone(), c.detail.clone())
    } else if let Some(k) = e.downcast_ref::<kiln_manage::Error>() {
        (k.code.as_str().to_string(), k.message.clone(), k.detail.clone())
    } else {
        ("error".to_string(), format!("{e:#}"), None)
    };
    if json {
        let mut err = json!({ "code": code, "message": message });
        if let Some(d) = detail {
            err["detail"] = d;
        }
        println!("{}", json!({ "ok": false, "error": err }));
    } else {
        eprintln!("kiln: {message}");
    }
    if code == "usage" { 2 } else { 1 }
}

/// Prints a JSON value (pretty) to stdout.
pub fn print_json(v: &Value) {
    println!("{}", serde_json::to_string_pretty(v).unwrap_or_else(|_| v.to_string()));
}

/// Human-only line (suppressed with --json so stdout stays one document).
pub fn say(json: bool, text: impl AsRef<str>) {
    if !json {
        println!("{}", text.as_ref());
    } else {
        eprintln!("{}", text.as_ref());
    }
}
