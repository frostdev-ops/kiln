//! Server administration: KAP/1 over TCP (Kiln.Admin's listener, docs/kiln/launcher/admin-protocol.md), the file
//! bridge as fallback, and SSH tunnels to remote servers. One op table for all
//! transports: an op answers the same over TCP as over the file bridge. [`hub::AdminHub`] keeps one session per
//! server for the launcher (reconnects, re-subscribes, forwards pushes).

pub mod bridge;
pub mod client;
pub mod discover;
pub mod hub;
pub mod protocol;
pub mod tunnel;

#[cfg(all(test, feature = "admin"))]
mod tests;

/// Parses Kiln's relaxed JSON (`//` and `/* */` comments, trailing commas), as Kiln.Admin and Kiln.Network read their
/// config files. Comments inside strings are kept.
pub(crate) fn parse_jsonc(text: &str) -> crate::Result<serde_json::Value> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let b = text.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(text.len());
    let mut i = 0;
    let mut in_str = false;
    while i < b.len() {
        let c = b[i];
        if in_str {
            out.push(c);
            if c == b'\\' && i + 1 < b.len() {
                out.push(b[i + 1]);
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
                out.push(b'"');
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
                while j < b.len() && (b[j] as char).is_ascii_whitespace() {
                    j += 1;
                }
                if !matches!(b.get(j), Some(b'}') | Some(b']')) {
                    out.push(b',');
                }
                i += 1;
            }
            _ => {
                out.push(c);
                i += 1;
            }
        }
    }
    let fixed = String::from_utf8(out).map_err(|e| crate::Error::new(crate::ErrorCode::Parse, format!("not UTF-8: {e}")))?;
    Ok(serde_json::from_str(&fixed)?)
}

#[cfg(test)]
mod jsonc_tests {
    #[test]
    fn jsonc() {
        let v = super::parse_jsonc("{\n // c\n \"a\": \"x//y\", /* b */ \"b\": [1, 2,], \"é\": \"ü\",\n}").unwrap();
        assert_eq!(v["a"], "x//y");
        assert_eq!(v["b"], serde_json::json!([1, 2]));
        assert_eq!(v["é"], "ü");
    }
}
