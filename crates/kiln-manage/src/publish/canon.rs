//! Canonical JSON (RFC 8785, JCS) for the values a publish plan is made of: objects, arrays, strings, booleans, null
//! and integers. No fractions and no exponents: every size and id that matters is an integer or a string, so the
//! number rules of RFC 8785 (which follow ECMAScript's double formatting) never come into play. A value with any
//! other number is refused, not approximated.
//!
//! Rules applied: no whitespace; object members sorted by their keys' UTF-16 code units; strings with the minimal
//! escapes (`\"`, `\\`, `\b`, `\f`, `\n`, `\r`, `\t`, `\u00xx` for other control characters), everything else as
//! UTF-8; integers within ±(2^53 - 1) in plain decimal.

use crate::{Error, ErrorCode, Result};
use serde_json::Value;

/// The largest integer every JSON reader keeps exactly.
pub const MAX_SAFE_INTEGER: i64 = (1 << 53) - 1;

pub fn canonical(v: &Value) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    write(v, &mut out)?;
    Ok(out)
}

/// SHA-256 (lower-case hex) of the canonical bytes.
pub fn digest(v: &Value) -> Result<String> {
    Ok(crate::hash::sha256_hex(&canonical(v)?))
}

fn write(v: &Value, out: &mut Vec<u8>) -> Result<()> {
    match v {
        Value::Null => out.extend_from_slice(b"null"),
        Value::Bool(b) => out.extend_from_slice(if *b { b"true" } else { b"false" }),
        Value::Number(n) => {
            let i = n.as_i64().filter(|i| (-MAX_SAFE_INTEGER..=MAX_SAFE_INTEGER).contains(i)).ok_or_else(|| {
                Error::new(ErrorCode::InvalidArgument, format!("canonical JSON: {n} is not an integer within ±(2^53 - 1); write such a value as a string"))
            })?;
            out.extend_from_slice(i.to_string().as_bytes());
        }
        Value::String(s) => string(s, out),
        Value::Array(a) => {
            out.push(b'[');
            for (i, item) in a.iter().enumerate() {
                if i > 0 {
                    out.push(b',');
                }
                write(item, out)?;
            }
            out.push(b']');
        }
        Value::Object(o) => {
            let mut keys: Vec<&String> = o.keys().collect();
            keys.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
            out.push(b'{');
            for (i, k) in keys.iter().enumerate() {
                if i > 0 {
                    out.push(b',');
                }
                string(k, out);
                out.push(b':');
                write(&o[*k], out)?;
            }
            out.push(b'}');
        }
    }
    Ok(())
}

fn string(s: &str, out: &mut Vec<u8>) {
    out.push(b'"');
    for c in s.chars() {
        match c {
            '"' => out.extend_from_slice(b"\\\""),
            '\\' => out.extend_from_slice(b"\\\\"),
            '\u{08}' => out.extend_from_slice(b"\\b"),
            '\u{0c}' => out.extend_from_slice(b"\\f"),
            '\n' => out.extend_from_slice(b"\\n"),
            '\r' => out.extend_from_slice(b"\\r"),
            '\t' => out.extend_from_slice(b"\\t"),
            c if (c as u32) < 0x20 => out.extend_from_slice(format!("\\u{:04x}", c as u32).as_bytes()),
            c => out.extend_from_slice(c.encode_utf8(&mut [0u8; 4]).as_bytes()),
        }
    }
    out.push(b'"');
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn text(v: &Value) -> String {
        String::from_utf8(canonical(v).unwrap()).unwrap()
    }

    #[test]
    fn rfc8785_vectors_within_our_value_kinds() {
        // RFC 8785 section 3.2.2: literals, strings and nesting (its number examples are outside our kinds)
        // the string of the RFC's example after JSON parsing: euro sign, $, U+000F, line feed, A, ', B, ", \, \, ", /
        let v = json!({"literals": [null, true, false], "string": "\u{20ac}$\u{f}\nA'B\"\\\\\"/"});
        assert_eq!(text(&v), "{\"literals\":[null,true,false],\"string\":\"\u{20ac}$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}");
        // RFC 8785 section 3.2.3: members sorted by UTF-16 code units, not by code points or bytes
        let v: Value = serde_json::from_str(r#"{"€": "Euro Sign", "\r": "Carriage Return", "דּ": "Hebrew Letter Dalet With Dagesh", "1": "One", "😀": "Emoji: Grinning Face", "\u0080": "Control", "ö": "Latin Small Letter O With Diaeresis"}"#).unwrap();
        let order: Vec<&str> = ["Carriage Return", "One", "Control", "Latin Small Letter O With Diaeresis", "Euro Sign", "Emoji: Grinning Face", "Hebrew Letter Dalet With Dagesh"].to_vec();
        let t = text(&v);
        let positions: Vec<usize> = order.iter().map(|name| t.find(name).unwrap()).collect();
        assert!(positions.windows(2).all(|w| w[0] < w[1]), "{t}");
    }

    #[test]
    fn the_same_value_gives_the_same_bytes_whatever_the_input_order_or_spacing() {
        let a: Value = serde_json::from_str(r#"{"b": 2, "a": {"y": [1, 2], "x": "s"}}"#).unwrap();
        let b: Value = serde_json::from_str("{\n  \"a\": {\"x\": \"s\", \"y\": [1,2]},\n  \"b\": 2\n}").unwrap();
        assert_eq!(text(&a), r#"{"a":{"x":"s","y":[1,2]},"b":2}"#);
        assert_eq!(canonical(&a).unwrap(), canonical(&b).unwrap());
        assert_eq!(digest(&a).unwrap(), digest(&b).unwrap());
        assert_ne!(digest(&a).unwrap(), digest(&json!({"a": {"x": "s", "y": [2, 1]}, "b": 2})).unwrap());
    }

    #[test]
    fn integers_only() {
        assert_eq!(text(&json!([0, -1, 9007199254740991i64, -9007199254740991i64])), "[0,-1,9007199254740991,-9007199254740991]");
        for bad in [json!(1.5), json!(1e30), json!(9007199254740992i64), json!(u64::MAX), json!({"deep": [1, {"x": 0.1}]})] {
            assert!(canonical(&bad).is_err(), "{bad}");
        }
        // 1.0 is a fraction in the source text, not an integer
        assert!(canonical(&serde_json::from_str::<Value>("1.0").unwrap()).is_err());
    }
}
