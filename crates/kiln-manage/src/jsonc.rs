//! JSONC (comments, trailing commas), Kiln's own lossless reader: an order-preserving concrete syntax tree with the
//! byte span of every value, key, colon and comma. It accepts what System.Text.Json accepts with
//! `CommentHandling.Skip` + `AllowTrailingCommas` (Kiln.Host's Manifest.cs, Kiln.Api's Config): `//` and `/* */`
//! comments wherever whitespace may stand, a trailing comma in objects and arrays; otherwise strict JSON (no single
//! quotes, no NaN, whitespace = space/tab/CR/LF, nesting depth at most 64). Duplicate keys are kept in document order:
//! lookups take the last one (JsonElement.TryGetProperty), enumeration yields all.
//!
//! Edits ([`set_value`], [`remove_value`], [`ConfigDoc`]) splice only value tokens (and, for a new member, one new
//! line), so comments, layout and Kiln's meta comment lines stay byte-identical. Works in the default build (no
//! dependency); the `jsonc` feature is not needed for it.

use crate::{Error, ErrorCode, Result};
use std::fmt;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PathSeg {
    Key(String),
    Index(usize),
}

/// Splits a dotted config path (`limits.multiplier`, `list[2]`) into segments.
pub fn split_path(path: &str) -> Vec<PathSeg> {
    let mut out = Vec::new();
    for part in path.split('.').filter(|p| !p.is_empty()) {
        let (key, rest) = part.split_once('[').map(|(k, r)| (k, Some(r))).unwrap_or((part, None));
        if !key.is_empty() {
            out.push(PathSeg::Key(key.to_string()));
        }
        if let Some(rest) = rest {
            for idx in rest.split('[') {
                if let Ok(i) = idx.trim_end_matches(']').parse() {
                    out.push(PathSeg::Index(i));
                }
            }
        }
    }
    out
}

/// The maximum nesting depth (System.Text.Json's default MaxDepth).
pub const MAX_DEPTH: usize = 64;

/// Byte range `start..end` in the parsed text.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Span {
    pub start: usize,
    pub end: usize,
}

impl Span {
    pub fn slice<'a>(&self, text: &'a str) -> &'a str {
        &text[self.start..self.end]
    }
}

/// A JSON value of the tree. Numbers keep their source text.
#[derive(Debug, Clone, PartialEq)]
pub enum JValue {
    Null,
    Bool(bool),
    Number(String),
    String(String),
    Array(Vec<Element>),
    Object(Vec<Member>),
}

/// A value and where it stands in the text.
#[derive(Debug, Clone, PartialEq)]
pub struct Node {
    pub span: Span,
    pub value: JValue,
}

/// An array element: the value and the byte offset of the comma after it, if any.
#[derive(Debug, Clone, PartialEq)]
pub struct Element {
    pub node: Node,
    pub comma: Option<usize>,
}

/// An object member: decoded key, the key token's span (quotes included), the colon's offset, the value, and the
/// offset of the comma after the value, if any.
#[derive(Debug, Clone, PartialEq)]
pub struct Member {
    pub key: String,
    pub key_span: Span,
    pub colon: usize,
    pub value: Node,
    pub comma: Option<usize>,
}

/// A syntax error: message (System.Text.Json style), 0-based line and byte position in that line, byte offset.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SyntaxError {
    pub message: String,
    pub line: usize,
    pub column: usize,
    pub offset: usize,
}

impl fmt::Display for SyntaxError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} LineNumber: {} | BytePositionInLine: {}.", self.message, self.line, self.column)
    }
}

impl std::error::Error for SyntaxError {}

impl From<SyntaxError> for Error {
    fn from(e: SyntaxError) -> Self {
        Error::new(ErrorCode::Parse, format!("not valid JSON ({e})"))
    }
}

impl Node {
    pub fn kind_name(&self) -> &'static str {
        match self.value {
            JValue::Null => "null",
            JValue::Bool(_) => "bool",
            JValue::Number(_) => "number",
            JValue::String(_) => "string",
            JValue::Array(_) => "array",
            JValue::Object(_) => "object",
        }
    }

    pub fn is_null(&self) -> bool {
        matches!(self.value, JValue::Null)
    }

    pub fn as_str(&self) -> Option<&str> {
        match &self.value {
            JValue::String(s) => Some(s),
            _ => None,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match self.value {
            JValue::Bool(b) => Some(b),
            _ => None,
        }
    }

    /// The number's source text.
    pub fn as_number(&self) -> Option<&str> {
        match &self.value {
            JValue::Number(n) => Some(n),
            _ => None,
        }
    }

    pub fn as_f64(&self) -> Option<f64> {
        self.as_number().and_then(|n| n.parse().ok())
    }

    pub fn members(&self) -> &[Member] {
        match &self.value {
            JValue::Object(m) => m,
            _ => &[],
        }
    }

    pub fn elements(&self) -> &[Element] {
        match &self.value {
            JValue::Array(e) => e,
            _ => &[],
        }
    }

    pub fn is_object(&self) -> bool {
        matches!(self.value, JValue::Object(_))
    }

    pub fn is_array(&self) -> bool {
        matches!(self.value, JValue::Array(_))
    }

    /// The member's value; with duplicate keys the last one (JsonElement.TryGetProperty).
    pub fn get(&self, key: &str) -> Option<&Node> {
        self.members().iter().rev().find(|m| m.key == key).map(|m| &m.value)
    }

    /// Like [`Node::get`] with the key compared case-insensitively (Kiln.Api's config reader).
    pub fn get_ci(&self, key: &str) -> Option<&Node> {
        self.member_ci(key).map(|m| &m.value)
    }

    /// The last member whose key equals `key` case-insensitively.
    pub fn member_ci(&self, key: &str) -> Option<&Member> {
        self.members().iter().rev().find(|m| eq_ci(&m.key, key))
    }

    pub fn index(&self, i: usize) -> Option<&Node> {
        self.elements().get(i).map(|e| &e.node)
    }

    /// Follows a path (keys exact, last duplicate).
    pub fn at(&self, path: &[PathSeg]) -> Option<&Node> {
        path.iter().try_fold(self, |n, seg| match seg {
            PathSeg::Key(k) => n.get(k),
            PathSeg::Index(i) => n.index(*i),
        })
    }

    /// Follows a path with keys compared case-insensitively.
    pub fn at_ci(&self, path: &[PathSeg]) -> Option<&Node> {
        path.iter().try_fold(self, |n, seg| match seg {
            PathSeg::Key(k) => n.get_ci(k),
            PathSeg::Index(i) => n.index(*i),
        })
    }

    /// The exact source text of this value.
    pub fn raw<'a>(&self, text: &'a str) -> &'a str {
        self.span.slice(text)
    }

    /// As a serde_json value (duplicate keys: the last one wins; numbers through serde_json's number parser).
    pub fn to_value(&self) -> serde_json::Value {
        use serde_json::Value;
        match &self.value {
            JValue::Null => Value::Null,
            JValue::Bool(b) => Value::Bool(*b),
            JValue::Number(n) => serde_json::from_str::<Value>(n).unwrap_or_else(|_| {
                n.parse::<f64>().ok().and_then(serde_json::Number::from_f64).map(Value::Number).unwrap_or(Value::Null)
            }),
            JValue::String(s) => Value::String(s.clone()),
            JValue::Array(items) => Value::Array(items.iter().map(|e| e.node.to_value()).collect()),
            JValue::Object(members) => {
                let mut map = serde_json::Map::new();
                for m in members {
                    map.insert(m.key.clone(), m.value.to_value());
                }
                Value::Object(map)
            }
        }
    }
}

/// Case-insensitive key comparison (OrdinalIgnoreCase, close enough: simple lower-case mapping).
pub fn eq_ci(a: &str, b: &str) -> bool {
    a.len() == b.len() && a.eq_ignore_ascii_case(b) || a.to_lowercase() == b.to_lowercase()
}

// ---------------------------------------------------------------------------------------------------- parser -----

const DEPTH_NOT_ZERO: &str =
    "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed.";

struct Parser<'a> {
    text: &'a str,
    b: &'a [u8],
    pos: usize,
    depth: usize,
}

impl<'a> Parser<'a> {
    fn err(&self, at: usize, message: impl Into<String>) -> SyntaxError {
        let at = at.min(self.b.len());
        let line_start = self.b[..at].iter().rposition(|&c| c == b'\n').map(|i| i + 1).unwrap_or(0);
        let line = self.b[..at].iter().filter(|&&c| c == b'\n').count();
        SyntaxError { message: message.into(), line, column: at - line_start, offset: at }
    }

    fn peek(&self) -> Option<u8> {
        self.b.get(self.pos).copied()
    }

    fn describe(&self, at: usize) -> String {
        match self.b.get(at) {
            Some(&c) if c.is_ascii() && !c.is_ascii_control() => format!("'{}'", c as char),
            Some(&c) => format!("'0x{c:02X}'"),
            None => "end of data".into(),
        }
    }

    /// Whitespace and comments.
    fn skip(&mut self) -> std::result::Result<(), SyntaxError> {
        loop {
            match self.peek() {
                Some(b' ' | b'\t' | b'\r' | b'\n') => self.pos += 1,
                Some(b'/') => match self.b.get(self.pos + 1) {
                    Some(b'/') => {
                        self.pos += 2;
                        while let Some(c) = self.peek() {
                            if c == b'\n' || c == b'\r' {
                                break;
                            }
                            self.pos += 1;
                        }
                    }
                    Some(b'*') => {
                        let start = self.pos;
                        self.pos += 2;
                        loop {
                            match self.peek() {
                                None => return Err(self.err(start, "Expected end of comment, but instead reached end of data.")),
                                Some(b'*') if self.b.get(self.pos + 1) == Some(&b'/') => {
                                    self.pos += 2;
                                    break;
                                }
                                Some(_) => self.pos += 1,
                            }
                        }
                    }
                    _ => {
                        return Err(self.err(self.pos, "'/' is invalid after a value. Expected either ',', '}', or ']'."));
                    }
                },
                _ => return Ok(()),
            }
        }
    }

    fn value(&mut self) -> std::result::Result<Node, SyntaxError> {
        self.skip()?;
        let start = self.pos;
        let Some(c) = self.peek() else {
            if self.depth > 0 {
                return Err(self.err(start, DEPTH_NOT_ZERO));
            }
            return Err(self.err(start, "Expected a value, but instead reached end of data."));
        };
        match c {
            b'{' => self.object(),
            b'[' => self.array(),
            b'"' => {
                let s = self.string()?;
                Ok(Node { span: Span { start, end: self.pos }, value: JValue::String(s) })
            }
            b't' => self.literal("true", JValue::Bool(true)),
            b'f' => self.literal("false", JValue::Bool(false)),
            b'n' => self.literal("null", JValue::Null),
            b'-' | b'0'..=b'9' => self.number(),
            _ => Err(self.err(start, format!("{} is an invalid start of a value.", self.describe(start)))),
        }
    }

    fn literal(&mut self, word: &str, value: JValue) -> std::result::Result<Node, SyntaxError> {
        let start = self.pos;
        if self.b[start..].starts_with(word.as_bytes()) {
            self.pos += word.len();
            if let Some(c) = self.peek()
                && (c.is_ascii_alphanumeric() || c == b'_')
            {
                return Err(self.err(self.pos, format!("'{}' is invalid after a value.", c as char)));
            }
            return Ok(Node { span: Span { start, end: self.pos }, value });
        }
        Err(self.err(start, format!("'{}' is an invalid JSON literal. Expected the literal '{word}'.", self.text[start..]
            .chars().take_while(|c| c.is_ascii_alphanumeric()).collect::<String>())))
    }

    fn number(&mut self) -> std::result::Result<Node, SyntaxError> {
        let start = self.pos;
        let digits = |p: &mut Self| {
            let s = p.pos;
            while p.peek().is_some_and(|c| c.is_ascii_digit()) {
                p.pos += 1;
            }
            p.pos - s
        };
        if self.peek() == Some(b'-') {
            self.pos += 1;
        }
        match self.peek() {
            Some(b'0') => {
                self.pos += 1;
                if self.peek().is_some_and(|c| c.is_ascii_digit()) {
                    return Err(self.err(self.pos, "Invalid leading zeros in a number."));
                }
            }
            Some(b'1'..=b'9') => {
                digits(self);
            }
            _ => return Err(self.err(self.pos, "'-' is not followed by a digit: the JSON number is not valid.")),
        }
        if self.peek() == Some(b'.') {
            self.pos += 1;
            if digits(self) == 0 {
                return Err(self.err(self.pos, "Expected a digit after the decimal point in the JSON number."));
            }
        }
        if matches!(self.peek(), Some(b'e' | b'E')) {
            self.pos += 1;
            if matches!(self.peek(), Some(b'+' | b'-')) {
                self.pos += 1;
            }
            if digits(self) == 0 {
                return Err(self.err(self.pos, "Expected a digit in the exponent of the JSON number."));
            }
        }
        if let Some(c) = self.peek()
            && (c.is_ascii_alphanumeric() || c == b'.' || c == b'_')
        {
            return Err(self.err(self.pos, format!("'{}' is invalid within a number.", c as char)));
        }
        Ok(Node { span: Span { start, end: self.pos }, value: JValue::Number(self.text[start..self.pos].to_string()) })
    }

    /// A string token starting at '"'; returns the decoded text.
    fn string(&mut self) -> std::result::Result<String, SyntaxError> {
        let start = self.pos;
        self.pos += 1;
        let mut out = String::new();
        let mut run = self.pos;
        loop {
            let Some(c) = self.peek() else {
                return Err(self.err(start, "Expected end of string, but instead reached end of data."));
            };
            match c {
                b'"' => {
                    out.push_str(&self.text[run..self.pos]);
                    self.pos += 1;
                    return Ok(out);
                }
                b'\\' => {
                    out.push_str(&self.text[run..self.pos]);
                    let esc = self.b.get(self.pos + 1).copied();
                    self.pos += 2;
                    match esc {
                        Some(b'"') => out.push('"'),
                        Some(b'\\') => out.push('\\'),
                        Some(b'/') => out.push('/'),
                        Some(b'b') => out.push('\u{8}'),
                        Some(b'f') => out.push('\u{c}'),
                        Some(b'n') => out.push('\n'),
                        Some(b'r') => out.push('\r'),
                        Some(b't') => out.push('\t'),
                        Some(b'u') => {
                            let hi = self.hex4()?;
                            if (0xD800..0xDC00).contains(&hi) {
                                if self.b.get(self.pos) == Some(&b'\\') && self.b.get(self.pos + 1) == Some(&b'u') {
                                    self.pos += 2;
                                    let lo = self.hex4()?;
                                    if (0xDC00..0xE000).contains(&lo) {
                                        let cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                                        out.push(char::from_u32(cp).unwrap_or('\u{FFFD}'));
                                    } else {
                                        out.push('\u{FFFD}');
                                        out.push(char::from_u32(lo).unwrap_or('\u{FFFD}'));
                                    }
                                } else {
                                    out.push('\u{FFFD}');
                                }
                            } else {
                                out.push(char::from_u32(hi).unwrap_or('\u{FFFD}'));
                            }
                        }
                        _ => {
                            return Err(self.err(self.pos - 1, "'\\' is not followed by a valid escape character within a string."));
                        }
                    }
                    run = self.pos;
                }
                0..=0x1F => {
                    return Err(self.err(self.pos, format!("'0x{c:02X}' is invalid within a JSON string. The string should be correctly escaped.")));
                }
                _ => self.pos += 1,
            }
        }
    }

    fn hex4(&mut self) -> std::result::Result<u32, SyntaxError> {
        let h = self.b.get(self.pos..self.pos + 4).filter(|h| h.iter().all(|c| c.is_ascii_hexdigit()));
        let Some(h) = h else {
            return Err(self.err(self.pos, "'\\u' is not followed by four hexadecimal digits within a string."));
        };
        let v = u32::from_str_radix(std::str::from_utf8(h).unwrap_or("0"), 16).unwrap_or(0);
        self.pos += 4;
        Ok(v)
    }

    fn enter(&mut self) -> std::result::Result<(), SyntaxError> {
        self.depth += 1;
        if self.depth > MAX_DEPTH {
            return Err(self.err(self.pos, format!("The maximum configured depth of {MAX_DEPTH} has been exceeded. Cannot read next JSON object.")));
        }
        Ok(())
    }

    fn object(&mut self) -> std::result::Result<Node, SyntaxError> {
        let start = self.pos;
        self.enter()?;
        self.pos += 1;
        let mut members: Vec<Member> = Vec::new();
        loop {
            self.skip()?;
            match self.peek() {
                Some(b'}') => break,
                Some(b'"') => {}
                None => return Err(self.err(self.pos, DEPTH_NOT_ZERO)),
                Some(_) => {
                    return Err(self.err(self.pos, format!("{} is an invalid start of a property name. Expected a '\"'.", self.describe(self.pos))));
                }
            }
            if let Some(last) = members.last()
                && last.comma.is_none()
            {
                return Err(self.err(self.pos, "'\"' is invalid after a value. Expected either ',', '}', or ']'."));
            }
            let ks = self.pos;
            let key = self.string()?;
            let key_span = Span { start: ks, end: self.pos };
            self.skip()?;
            if self.peek() != Some(b':') {
                return Err(self.err(self.pos, format!("{} is invalid after a property name. Expected a ':'.", self.describe(self.pos))));
            }
            let colon = self.pos;
            self.pos += 1;
            let value = self.value()?;
            self.skip()?;
            let comma = match self.peek() {
                Some(b',') => {
                    self.pos += 1;
                    Some(self.pos - 1)
                }
                Some(b'}') => None,
                None => return Err(self.err(self.pos, DEPTH_NOT_ZERO)),
                Some(_) => {
                    return Err(self.err(self.pos, format!("{} is invalid after a value. Expected either ',', '}}', or ']'.", self.describe(self.pos))));
                }
            };
            members.push(Member { key, key_span, colon, value, comma });
        }
        self.pos += 1;
        self.depth -= 1;
        Ok(Node { span: Span { start, end: self.pos }, value: JValue::Object(members) })
    }

    fn array(&mut self) -> std::result::Result<Node, SyntaxError> {
        let start = self.pos;
        self.enter()?;
        self.pos += 1;
        let mut items: Vec<Element> = Vec::new();
        loop {
            self.skip()?;
            match self.peek() {
                Some(b']') => break,
                None => return Err(self.err(self.pos, DEPTH_NOT_ZERO)),
                _ => {}
            }
            if let Some(last) = items.last()
                && last.comma.is_none()
            {
                return Err(self.err(self.pos, "Expected either ',', '}', or ']' after a value."));
            }
            let node = self.value()?;
            self.skip()?;
            let comma = match self.peek() {
                Some(b',') => {
                    self.pos += 1;
                    Some(self.pos - 1)
                }
                Some(b']') => None,
                None => return Err(self.err(self.pos, DEPTH_NOT_ZERO)),
                Some(_) => {
                    return Err(self.err(self.pos, format!("{} is invalid after a value. Expected either ',', '}}', or ']'.", self.describe(self.pos))));
                }
            };
            items.push(Element { node, comma });
        }
        self.pos += 1;
        self.depth -= 1;
        Ok(Node { span: Span { start, end: self.pos }, value: JValue::Array(items) })
    }
}

/// Parses JSONC into the syntax tree (one value, comments around it allowed).
pub fn parse_tree(text: &str) -> std::result::Result<Node, SyntaxError> {
    let mut p = Parser { text, b: text.as_bytes(), pos: 0, depth: 0 };
    p.skip()?;
    if p.peek().is_none() {
        return Err(p.err(p.pos, "The input does not contain any JSON tokens. Expected the input to start with a valid JSON token, while isFinalBlock is true."));
    }
    let root = p.value()?;
    p.skip()?;
    if p.pos < p.b.len() {
        return Err(p.err(p.pos, format!("{} is invalid after a single JSON value. Expected end of data.", p.describe(p.pos))));
    }
    Ok(root)
}

/// Parses JSONC bytes: invalid UTF-8 is refused; a UTF-8 BOM is skipped.
pub fn parse_tree_bytes(bytes: &[u8]) -> std::result::Result<(String, Node), SyntaxError> {
    let bytes = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(bytes);
    let text = match std::str::from_utf8(bytes) {
        Ok(t) => t.to_string(),
        Err(e) => {
            let at = e.valid_up_to();
            return Err(SyntaxError {
                message: "The input is not valid UTF-8.".into(),
                line: bytes[..at].iter().filter(|&&c| c == b'\n').count(),
                column: at - bytes[..at].iter().rposition(|&c| c == b'\n').map(|i| i + 1).unwrap_or(0),
                offset: at,
            });
        }
    };
    let node = parse_tree(&text)?;
    Ok((text, node))
}

pub fn parse(text: &str) -> Result<serde_json::Value> {
    Ok(parse_tree(text)?.to_value())
}

// ---------------------------------------------------------------------------------------------------- writer -----

/// A value as JSON text the way Kiln writes it: strings with relaxed escaping, `[a, b]` for scalar lists, objects
/// over several lines indented with `indent` + 2 spaces.
pub fn render(value: &serde_json::Value, indent: &str) -> String {
    use serde_json::Value;
    match value {
        Value::Null => "null".into(),
        Value::Bool(b) => b.to_string(),
        Value::Number(n) => render_number(n),
        Value::String(s) => quote(s),
        Value::Array(items) => {
            if items.is_empty() {
                return "[]".into();
            }
            if items.iter().all(|v| !v.is_array() && !v.is_object()) {
                return format!("[{}]", items.iter().map(|v| render(v, indent)).collect::<Vec<_>>().join(", "));
            }
            let inner = format!("{indent}  ");
            let body: Vec<String> = items.iter().map(|v| format!("{inner}{}", render(v, &inner))).collect();
            format!("[\n{}\n{indent}]", body.join(",\n"))
        }
        Value::Object(map) => {
            if map.is_empty() {
                return "{}".into();
            }
            let inner = format!("{indent}  ");
            let body: Vec<String> = map.iter().map(|(k, v)| format!("{inner}{}: {}", quote(k), render(v, &inner))).collect();
            format!("{{\n{}\n{indent}}}", body.join(",\n"))
        }
    }
}

fn render_number(n: &serde_json::Number) -> String {
    if let Some(f) = n.as_f64().filter(|_| n.is_f64())
        && f.is_finite()
        && f.fract() == 0.0
        && f.abs() < 1e15
    {
        return format!("{}", f as i64);
    }
    n.to_string()
}

/// A JSON string literal (quotes, backslash and control characters escaped; nothing else).
pub fn quote(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04X}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

// ---------------------------------------------------------------------------------------------------- edits ------

/// One replacement: `start..end` becomes `text`.
struct Splice {
    start: usize,
    end: usize,
    text: String,
}

fn apply(text: &str, edits: Vec<Splice>) -> String {
    // back to front; inserts at the same offset keep their order in the text (the later one is applied first)
    let mut edits: Vec<(usize, Splice)> = edits.into_iter().enumerate().collect();
    edits.sort_by(|(ia, a), (ib, b)| b.start.cmp(&a.start).then(ib.cmp(ia)));
    let mut out = text.to_string();
    for (_, e) in edits {
        out.replace_range(e.start..e.end, &e.text);
    }
    out
}

fn line_start(text: &str, at: usize) -> usize {
    text[..at].rfind('\n').map(|i| i + 1).unwrap_or(0)
}

/// The whitespace before `at` on its line, when only whitespace precedes it there.
fn indent_at(text: &str, at: usize) -> Option<&str> {
    let ls = line_start(text, at);
    let pre = &text[ls..at];
    pre.chars().all(|c| c == ' ' || c == '\t').then_some(pre)
}

/// The indentation of the line holding `at`.
fn line_indent(text: &str, at: usize) -> &str {
    let ls = line_start(text, at);
    let rest = &text[ls..];
    let n = rest.len() - rest.trim_start_matches([' ', '\t']).len();
    &rest[..n]
}

/// From `at`, skips spaces, tabs and a `//` comment on the same line; returns the offset of the line's end (the '\n'
/// or "\r\n") when only that stands between, else `at`.
fn end_of_line_after(text: &str, at: usize) -> usize {
    let b = text.as_bytes();
    let mut i = at;
    while i < b.len() && (b[i] == b' ' || b[i] == b'\t') {
        i += 1;
    }
    if b.get(i) == Some(&b'/') && b.get(i + 1) == Some(&b'/') {
        while i < b.len() && b[i] != b'\n' && b[i] != b'\r' {
            i += 1;
        }
    }
    match b.get(i) {
        Some(b'\r') | Some(b'\n') | None => i,
        _ => at,
    }
}

fn newline_of(text: &str) -> &'static str {
    if text.contains("\r\n") { "\r\n" } else { "\n" }
}

/// A value (or the nested members for the rest of `path`) as text, for a new member or element.
fn nested_value(path: &[PathSeg], value: &serde_json::Value) -> Result<serde_json::Value> {
    let mut v = value.clone();
    for seg in path.iter().rev() {
        v = match seg {
            PathSeg::Key(k) => {
                let mut m = serde_json::Map::new();
                m.insert(k.clone(), v);
                serde_json::Value::Object(m)
            }
            PathSeg::Index(0) => serde_json::Value::Array(vec![v]),
            PathSeg::Index(i) => return Err(Error::invalid(format!("index [{i}] is past the end of a new list"))),
        };
    }
    Ok(v)
}

fn render_at(text: &str, value: &serde_json::Value, indent: &str) -> String {
    let r = render(value, indent);
    if newline_of(text) == "\r\n" { r.replace('\n', "\r\n") } else { r }
}

/// Edits that insert member `key` (with the rest of the path nested) into the object `obj`.
fn insert_member(text: &str, obj: &Node, key: &str, rest: &[PathSeg], value: &serde_json::Value) -> Result<Vec<Splice>> {
    let nl = newline_of(text);
    let v = nested_value(rest, value)?;
    let members = obj.members();
    let mut edits = Vec::new();
    if let Some(last) = members.last() {
        let indent = indent_at(text, last.key_span.start).map(str::to_string);
        let multiline = indent.is_some();
        let indent = indent.unwrap_or_else(|| line_indent(text, last.key_span.start).to_string());
        let body = format!("{}: {}", quote(key), render_at(text, &v, &indent));
        let trailing = last.comma.is_some();
        let after = last.comma.map(|c| c + 1).unwrap_or(last.value.span.end);
        if multiline {
            let at = end_of_line_after(text, after);
            if !trailing {
                edits.push(Splice { start: last.value.span.end, end: last.value.span.end, text: ",".into() });
            }
            edits.push(Splice { start: at, end: at, text: format!("{nl}{indent}{body}{}", if trailing { "," } else { "" }) });
        } else if trailing {
            edits.push(Splice { start: after, end: after, text: format!(" {body},") });
        } else {
            edits.push(Splice { start: after, end: after, text: format!(", {body}") });
        }
        return Ok(edits);
    }
    // empty object
    let open = obj.span.start;
    let close = obj.span.end - 1;
    let outer = line_indent(text, open).to_string();
    let inner = format!("{outer}  ");
    let body = format!("{}: {}", quote(key), render_at(text, &v, &inner));
    let interior = &text[open + 1..close];
    if interior.trim_matches([' ', '\t', '\r', '\n']).is_empty() {
        edits.push(Splice { start: open + 1, end: close, text: format!("{nl}{inner}{body}{nl}{outer}") });
    } else if indent_at(text, close).is_some() {
        let ls = line_start(text, close);
        edits.push(Splice { start: ls, end: ls, text: format!("{inner}{body}{nl}") });
    } else {
        edits.push(Splice { start: close, end: close, text: format!("{nl}{inner}{body}{nl}{outer}") });
    }
    Ok(edits)
}

/// Edits that append an element to the array `arr`.
fn append_element(text: &str, arr: &Node, rest: &[PathSeg], value: &serde_json::Value) -> Result<Vec<Splice>> {
    let nl = newline_of(text);
    let v = nested_value(rest, value)?;
    let items = arr.elements();
    let mut edits = Vec::new();
    if let Some(last) = items.last() {
        let indent = indent_at(text, last.node.span.start).map(str::to_string);
        let trailing = last.comma.is_some();
        let after = last.comma.map(|c| c + 1).unwrap_or(last.node.span.end);
        match indent {
            Some(indent) => {
                let body = render_at(text, &v, &indent);
                let at = end_of_line_after(text, after);
                if !trailing {
                    edits.push(Splice { start: last.node.span.end, end: last.node.span.end, text: ",".into() });
                }
                edits.push(Splice { start: at, end: at, text: format!("{nl}{indent}{body}{}", if trailing { "," } else { "" }) });
            }
            None => {
                let body = render_at(text, &v, line_indent(text, last.node.span.start));
                edits.push(Splice { start: after, end: after, text: if trailing { format!(" {body},") } else { format!(", {body}") } });
            }
        }
        return Ok(edits);
    }
    let open = arr.span.start;
    let close = arr.span.end - 1;
    let body = render_at(text, &v, line_indent(text, open));
    if text[open + 1..close].trim_matches([' ', '\t', '\r', '\n']).is_empty() {
        edits.push(Splice { start: open + 1, end: close, text: body });
    } else {
        edits.push(Splice { start: close, end: close, text: body });
    }
    Ok(edits)
}

fn find_member<'n>(obj: &'n Node, key: &str, ci: bool) -> Option<(usize, &'n Member)> {
    obj.members().iter().enumerate().rev().find(|(_, m)| if ci { eq_ci(&m.key, key) } else { m.key == key })
}

fn set_edits(text: &str, node: &Node, path: &[PathSeg], value: &serde_json::Value, ci: bool) -> Result<Vec<Splice>> {
    let Some((seg, rest)) = path.split_first() else {
        let indent = line_indent(text, node.span.start).to_string();
        return Ok(vec![Splice { start: node.span.start, end: node.span.end, text: render_at(text, value, &indent) }]);
    };
    match (seg, &node.value) {
        (PathSeg::Key(k), JValue::Object(_)) => match find_member(node, k, ci) {
            Some((_, m)) => set_edits(text, &m.value, rest, value, ci),
            None => insert_member(text, node, k, rest, value),
        },
        (PathSeg::Index(i), JValue::Array(items)) => {
            if *i < items.len() {
                set_edits(text, &items[*i].node, rest, value, ci)
            } else if *i == items.len() {
                append_element(text, node, rest, value)
            } else {
                Err(Error::invalid(format!("index [{i}] is past the end of a list of {}", items.len())))
            }
        }
        (PathSeg::Key(k), _) => Err(Error::new(ErrorCode::Schema, format!("cannot set '{k}': the value there is a {}, not an object", node.kind_name()))),
        (PathSeg::Index(i), _) => Err(Error::new(ErrorCode::Schema, format!("cannot set [{i}]: the value there is a {}, not a list", node.kind_name()))),
    }
}

/// Removes `start..end` and, when the line holds nothing else, the whole line.
fn remove_span(text: &str, start: usize, end: usize) -> Splice {
    if indent_at(text, start).is_some() {
        let eol = end_of_line_after(text, end);
        let b = text.as_bytes();
        if eol != end || matches!(b.get(end), Some(b'\r' | b'\n') | None) {
            // the rest of the line is blank (a trailing // comment counts as part of the member)
            let ls = line_start(text, start);
            let mut e = eol;
            if b.get(e) == Some(&b'\r') {
                e += 1;
            }
            if b.get(e) == Some(&b'\n') {
                e += 1;
            }
            return Splice { start: ls, end: e, text: String::new() };
        }
    }
    let b = text.as_bytes();
    let mut e = end;
    while e < b.len() && (b[e] == b' ' || b[e] == b'\t') {
        e += 1;
    }
    Splice { start, end: e, text: String::new() }
}

/// Edits removing an item `start..value_end` (+ its comma); the last item also takes the comma before it.
fn removal(text: &str, start: usize, value_end: usize, comma: Option<usize>, prev_comma: Option<usize>) -> Vec<Splice> {
    if comma.is_none()
        && let Some(pc) = prev_comma
        && indent_at(text, start).is_none()
    {
        return vec![Splice { start: pc, end: value_end, text: String::new() }];
    }
    let end = comma.map(|c| c + 1).unwrap_or(value_end);
    let mut edits = vec![remove_span(text, start, end)];
    if comma.is_none()
        && let Some(pc) = prev_comma
    {
        edits.push(Splice { start: pc, end: pc + 1, text: String::new() });
    }
    edits
}

fn remove_edits(text: &str, node: &Node, path: &[PathSeg], ci: bool) -> Result<Option<Vec<Splice>>> {
    let Some((seg, rest)) = path.split_first() else {
        return Err(Error::invalid("an empty path cannot be removed"));
    };
    if !rest.is_empty() {
        let child = match seg {
            PathSeg::Key(k) => find_member(node, k, ci).map(|(_, m)| &m.value),
            PathSeg::Index(i) => node.index(*i),
        };
        return match child {
            Some(c) => remove_edits(text, c, rest, ci),
            None => Ok(None),
        };
    }
    match (seg, &node.value) {
        (PathSeg::Key(k), JValue::Object(members)) => {
            let Some((i, m)) = find_member(node, k, ci) else { return Ok(None) };
            let prev = if i > 0 { members[i - 1].comma } else { None };
            Ok(Some(removal(text, m.key_span.start, m.value.span.end, m.comma, prev)))
        }
        (PathSeg::Index(i), JValue::Array(items)) => {
            let Some(el) = items.get(*i) else { return Ok(None) };
            let prev = if *i > 0 { items[*i - 1].comma } else { None };
            Ok(Some(removal(text, el.node.span.start, el.node.span.end, el.comma, prev)))
        }
        _ => Ok(None),
    }
}

/// `text` with the value at `path` replaced by `value` (members/elements created when missing). Keys match exactly.
pub fn set_value(text: &str, path: &[PathSeg], value: &serde_json::Value) -> Result<String> {
    let root = parse_tree(text)?;
    Ok(apply(text, set_edits(text, &root, path, value, false)?))
}

/// `text` with the member at `path` removed (a value back to its default). Unchanged when there is none.
pub fn remove_value(text: &str, path: &[PathSeg]) -> Result<String> {
    let root = parse_tree(text)?;
    Ok(match remove_edits(text, &root, path, false)? {
        Some(edits) => apply(text, edits),
        None => text.to_string(),
    })
}

/// A JSONC document being edited: the text and its syntax tree, kept in step. Every edit splices value tokens only.
#[derive(Debug, Clone)]
pub struct ConfigDoc {
    text: String,
    root: Node,
    /// Match keys case-insensitively (Kiln.Api's config reader does).
    pub case_insensitive: bool,
}

impl ConfigDoc {
    pub fn parse(text: impl Into<String>) -> Result<ConfigDoc> {
        let text = text.into();
        let root = parse_tree(&text)?;
        Ok(ConfigDoc { text, root, case_insensitive: false })
    }

    /// A document whose keys match case-insensitively (Kiln config files).
    pub fn parse_ci(text: impl Into<String>) -> Result<ConfigDoc> {
        let mut d = ConfigDoc::parse(text)?;
        d.case_insensitive = true;
        Ok(d)
    }

    pub fn text(&self) -> &str {
        &self.text
    }

    pub fn into_text(self) -> String {
        self.text
    }

    pub fn root(&self) -> &Node {
        &self.root
    }

    /// The node at `path` (keys exact unless the document is case-insensitive).
    pub fn get(&self, path: &[PathSeg]) -> Option<&Node> {
        if self.case_insensitive { self.root.at_ci(path) } else { self.root.at(path) }
    }

    pub fn value(&self, path: &[PathSeg]) -> Option<serde_json::Value> {
        self.get(path).map(Node::to_value)
    }

    /// Every value (comments dropped).
    pub fn values(&self) -> serde_json::Value {
        self.root.to_value()
    }

    /// The exact source text of the value at `path`.
    pub fn raw(&self, path: &[PathSeg]) -> Option<&str> {
        self.get(path).map(|n| n.raw(&self.text))
    }

    pub fn set(&mut self, path: &[PathSeg], value: &serde_json::Value) -> Result<()> {
        let edits = set_edits(&self.text, &self.root, path, value, self.case_insensitive)?;
        self.replace(apply(&self.text, edits))
    }

    /// Removes the member or element at `path`; false when there was none.
    pub fn remove(&mut self, path: &[PathSeg]) -> Result<bool> {
        match remove_edits(&self.text, &self.root, path, self.case_insensitive)? {
            Some(edits) => {
                let t = apply(&self.text, edits);
                self.replace(t)?;
                Ok(true)
            }
            None => Ok(false),
        }
    }

    fn replace(&mut self, text: String) -> Result<()> {
        let root = parse_tree(&text).map_err(|e| Error::new(ErrorCode::Internal, format!("an edit broke the document: {e}")))?;
        self.text = text;
        self.root = root;
        Ok(())
    }
}

impl fmt::Display for ConfigDoc {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.text)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn p(path: &str) -> Vec<PathSeg> {
        split_path(path)
    }

    #[test]
    fn parses_jsonc_like_system_text_json() {
        let t = "// head\n{ /* c */ \"a\": [1, 2,], \"b\": {\"c\": null,}, \"a\": \"last\", }\n// tail";
        let n = parse_tree(t).unwrap();
        assert_eq!(n.members().len(), 3);
        assert_eq!(n.get("a").unwrap().as_str(), Some("last"));
        assert_eq!(n.members()[0].value.elements().len(), 2);
        assert_eq!(n.members()[0].value.raw(t), "[1, 2,]");
        for bad in ["", "{", "{'a': 1}", "[NaN]", "{\"a\" 1}", "[1 2]", "{\"a\":1}{}", "[01]", "[1.]", "/* x", "\"\t\"", "[,]", "{,}"] {
            assert!(parse_tree(bad).is_err(), "accepted {bad:?}");
        }
        let deep = format!("{}{}", "[".repeat(64), "]".repeat(64));
        assert!(parse_tree(&deep).is_ok());
        let deeper = format!("{}{}", "[".repeat(65), "]".repeat(65));
        assert!(parse_tree(&deeper).unwrap_err().message.contains("depth"));
        assert_eq!(parse("{\"s\": \"\\u00e9\\ud83d\\ude00\\n\"}").unwrap(), json!({"s": "é😀\n"}));
        assert!(parse_tree_bytes(b"{\"a\": \"\xff\"}").is_err());
    }

    #[test]
    fn syntax_error_positions() {
        let e = parse_tree("{\"id\":").unwrap_err();
        assert_eq!((e.line, e.column), (0, 6));
        assert!(e.to_string().starts_with("Expected depth to be zero"));
        let e = parse_tree("{\n  \"a\": x\n}").unwrap_err();
        assert_eq!((e.line, e.column), (1, 7));
    }

    #[test]
    fn set_replaces_only_the_value_token() {
        let t = "// Kiln header\n{\n  // Default: 2 | Range: 1 .. 100\n  \"multiplier\": 2, // trailing\n  \"name\": \"x\"\n}\n";
        let out = set_value(t, &p("multiplier"), &json!(5)).unwrap();
        assert_eq!(out, t.replace("\"multiplier\": 2,", "\"multiplier\": 5,"));
        let out = set_value(t, &p("name"), &json!("a \"q\"")).unwrap();
        assert!(out.contains("\"name\": \"a \\\"q\\\"\"\n}"));
    }

    #[test]
    fn set_creates_members_with_sibling_indentation() {
        let t = "{\n    \"a\": 1 // one\n}\n";
        let out = set_value(t, &p("b.c"), &json!(true)).unwrap();
        assert_eq!(out, "{\n    \"a\": 1, // one\n    \"b\": {\n      \"c\": true\n    }\n}\n");
        let t2 = "{\n  \"a\": 1,\n}\n";
        assert_eq!(set_value(t2, &p("b"), &json!([1, 2])).unwrap(), "{\n  \"a\": 1,\n  \"b\": [1, 2],\n}\n");
        assert_eq!(set_value("{}", &p("a"), &json!(1)).unwrap(), "{\n  \"a\": 1\n}");
        assert_eq!(set_value("{\"a\": 1}", &p("b"), &json!(2)).unwrap(), "{\"a\": 1, \"b\": 2}");
        assert_eq!(set_value("{\"l\": [1]}", &p("l[1]"), &json!(2)).unwrap(), "{\"l\": [1, 2]}");
        assert_eq!(set_value("{\"l\": []}", &p("l[0]"), &json!("x")).unwrap(), "{\"l\": [\"x\"]}");
        assert!(set_value("{\"l\": []}", &p("l[3]"), &json!(1)).is_err());
        assert!(set_value("{\"l\": 5}", &p("l.x"), &json!(1)).is_err());
        let crlf = "{\r\n  \"a\": 1\r\n}\r\n";
        assert_eq!(set_value(crlf, &p("b"), &json!(2)).unwrap(), "{\r\n  \"a\": 1,\r\n  \"b\": 2\r\n}\r\n");
    }

    #[test]
    fn remove_members_and_elements() {
        let t = "{\n  // keep me\n  \"a\": 1,\n  \"b\": 2\n}\n";
        assert_eq!(remove_value(t, &p("b")).unwrap(), "{\n  // keep me\n  \"a\": 1\n}\n");
        assert_eq!(remove_value(t, &p("a")).unwrap(), "{\n  // keep me\n  \"b\": 2\n}\n");
        assert_eq!(remove_value(t, &p("zz")).unwrap(), t);
        assert_eq!(remove_value("{\"l\": [1, 2, 3]}", &p("l[1]")).unwrap(), "{\"l\": [1, 3]}");
        assert_eq!(remove_value("{\"l\": [1, 2]}", &p("l[1]")).unwrap(), "{\"l\": [1]}");
        assert_eq!(remove_value("{\"a\": 1, \"b\": 2}", &p("a")).unwrap(), "{\"b\": 2}");
    }

    #[test]
    fn config_doc_is_case_insensitive_when_asked() {
        let mut d = ConfigDoc::parse_ci("{\n  \"Limits\": {\n    \"Multiplier\": 2\n  }\n}\n").unwrap();
        d.set(&p("limits.multiplier"), &json!(3.5)).unwrap();
        assert_eq!(d.text(), "{\n  \"Limits\": {\n    \"Multiplier\": 3.5\n  }\n}\n");
        assert_eq!(d.raw(&p("limits")).unwrap().lines().count(), 3);
        assert!(d.remove(&p("LIMITS.MULTIPLIER")).unwrap());
        assert_eq!(d.values(), json!({"Limits": {}}));
    }
}
