// SPDX-License-Identifier: MIT
//! A mod project: a Kiln.Sdk C# project (one *.csproj) or a native CMake project (CMakeLists.txt with kiln_plugin).

use crate::out::CliError;
use anyhow::{Context, Result};
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Dotnet,
    Native,
}

#[derive(Debug, Clone)]
pub struct Project {
    pub kind: Kind,
    /// The csproj, or the folder with CMakeLists.txt.
    pub path: PathBuf,
    pub dir: PathBuf,
}

impl Project {
    /// `arg`: a project file or a folder (default: the current folder).
    pub fn find(arg: Option<&Path>) -> Result<Project> {
        let start = match arg {
            Some(p) => p.to_path_buf(),
            None => std::env::current_dir()?,
        };
        let start = std::path::absolute(&start).unwrap_or(start);
        if start.is_file() {
            let dir = start.parent().map(Path::to_path_buf).unwrap_or_default();
            return match start.extension().and_then(|e| e.to_str()) {
                Some("csproj") => Ok(Project { kind: Kind::Dotnet, path: start, dir }),
                _ if start.file_name().is_some_and(|n| n == "CMakeLists.txt") => Ok(Project { kind: Kind::Native, path: dir.clone(), dir }),
                _ => Err(CliError::usage(format!("{} is not a .csproj or CMakeLists.txt", start.display())).into()),
            };
        }
        if !start.is_dir() {
            return Err(CliError::new("not_found", format!("{} does not exist", start.display())).into());
        }
        let mut csprojs: Vec<PathBuf> = std::fs::read_dir(&start)?
            .flatten()
            .map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|e| e == "csproj"))
            .collect();
        csprojs.sort();
        if csprojs.len() == 1 {
            return Ok(Project { kind: Kind::Dotnet, path: csprojs.remove(0), dir: start });
        }
        if csprojs.len() > 1 {
            return Err(CliError::usage(format!(
                "{} has {} projects; name one ({})",
                start.display(),
                csprojs.len(),
                csprojs.iter().filter_map(|p| p.file_name()).map(|n| n.to_string_lossy()).collect::<Vec<_>>().join(", ")
            ))
            .into());
        }
        let cmake = start.join("CMakeLists.txt");
        if cmake.is_file() && std::fs::read_to_string(&cmake).unwrap_or_default().contains("kiln_plugin(") {
            return Ok(Project { kind: Kind::Native, path: start.clone(), dir: start });
        }
        Err(CliError::new("not_found", format!("no mod project in {} (a .csproj, or a CMakeLists.txt calling kiln_plugin)", start.display())).into())
    }

    /// MSBuild properties of a dotnet project, evaluated without building (`dotnet msbuild -getProperty`).
    pub fn properties(&self, names: &[&str], config: Option<&str>) -> Result<serde_json::Map<String, Value>> {
        let mut cmd = Command::new("dotnet");
        cmd.arg("msbuild").arg(&self.path).arg("-nologo");
        if let Some(c) = config {
            cmd.arg(format!("-p:Configuration={c}"));
        }
        for n in names {
            cmd.arg(format!("-getProperty:{n}"));
        }
        let out = cmd.output().context("running dotnet msbuild (is the .NET SDK installed?)")?;
        let text = String::from_utf8_lossy(&out.stdout).to_string();
        if !out.status.success() {
            return Err(CliError::new("build_failed", format!("dotnet msbuild could not evaluate {}", self.path.display()))
                .with_detail(Value::String(format!("{text}{}", String::from_utf8_lossy(&out.stderr))))
                .into());
        }
        // one property: its value on one line; several: {"Properties": {...}}
        if names.len() == 1 {
            let mut m = serde_json::Map::new();
            m.insert(names[0].to_string(), Value::String(text.trim().to_string()));
            return Ok(m);
        }
        let v: Value = serde_json::from_str(text.trim()).with_context(|| format!("dotnet msbuild -getProperty output: {text}"))?;
        Ok(v.get("Properties").and_then(Value::as_object).cloned().unwrap_or_default())
    }

    pub fn property(&self, name: &str) -> Result<String> {
        Ok(self.properties(&[name], None)?.get(name).and_then(Value::as_str).unwrap_or("").to_string())
    }

    /// The native mod's id (native.json "id") and dll name.
    pub fn native_manifest(&self) -> Result<Value> {
        let p = self.dir.join("native.json");
        let text = std::fs::read_to_string(&p).with_context(|| format!("reading {}", p.display()))?;
        jsonc(&text).with_context(|| format!("parsing {}", p.display()))
    }

    /// The cook recipes of the project: assets/cook.json, impact/cook.json, audio/cook.json (any `*/cook.json`).
    pub fn recipes(&self) -> Vec<PathBuf> {
        let mut v: Vec<PathBuf> = std::fs::read_dir(&self.dir)
            .into_iter()
            .flatten()
            .flatten()
            .map(|e| e.path().join("cook.json"))
            .filter(|p| p.is_file())
            .collect();
        v.sort();
        v
    }
}

/// JSON with `//` and `/* */` comments and trailing commas (kiln.mod.json, native.json, cook.json, content packs).
pub fn jsonc(text: &str) -> Result<Value> {
    let mut out = String::with_capacity(text.len());
    let b = text.as_bytes();
    let (mut i, mut in_str) = (0, false);
    while i < b.len() {
        let c = b[i];
        if in_str {
            out.push(c as char);
            if c == b'\\' && i + 1 < b.len() {
                out.push(b[i + 1] as char);
                i += 2;
                continue;
            }
            if c == b'"' {
                in_str = false;
            }
            i += 1;
            continue;
        }
        match c {
            b'"' => {
                in_str = true;
                out.push('"');
                i += 1;
            }
            b'/' if b.get(i + 1) == Some(&b'/') => {
                while i < b.len() && b[i] != b'\n' {
                    i += 1;
                }
            }
            b'/' if b.get(i + 1) == Some(&b'*') => {
                i += 2;
                while i + 1 < b.len() && !(b[i] == b'*' && b[i + 1] == b'/') {
                    i += 1;
                }
                i += 2;
            }
            b',' => {
                let mut j = i + 1;
                while j < b.len() && (b[j] as char).is_whitespace() {
                    j += 1;
                }
                if j < b.len() && (b[j] == b'}' || b[j] == b']') {
                    i += 1; // trailing comma
                } else {
                    out.push(',');
                    i += 1;
                }
            }
            _ => {
                out.push(c as char);
                i += 1;
            }
        }
    }
    // the byte loop pushed UTF-8 bytes one by one as chars: rebuild the string from bytes instead
    let bytes: Vec<u8> = out.chars().map(|c| c as u32 as u8).collect();
    Ok(serde_json::from_slice(&bytes)?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn jsonc_strips_comments_and_trailing_commas() {
        let v = jsonc("{ // c\n \"a\": \"x // not a comment\", /* b */ \"b\": [1, 2,], \"ü\": \"é\", }").unwrap();
        assert_eq!(v["a"], "x // not a comment");
        assert_eq!(v["b"], serde_json::json!([1, 2]));
        assert_eq!(v["ü"], "é");
    }
}
