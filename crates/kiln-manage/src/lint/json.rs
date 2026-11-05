//! A JSON / JSONC reader that keeps byte spans for every value and key, for precise diagnostics.
//!
//! It is lenient on purpose: comments and trailing commas are always parsed, and reported only when the file's
//! dialect (schema/schemas.json `dialect`) says the real Kiln reader refuses them. Duplicate keys are kept (the
//! later one is what every reader Kiln uses ends up with) and reported.

use super::{Diagnostic, Fix, Span, TextEdit, codes};

/// What the real reader of a file accepts.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Dialect {
    pub comments: bool,
    pub trailing_commas: bool,
    /// A UTF-8 byte-order mark is skipped (nlohmann, System.Text.Json) instead of being an error (Python's json).
    pub bom: bool,
}

impl Dialect {
    pub const JSONC: Dialect = Dialect { comments: true, trailing_commas: true, bom: true };
    pub const STRICT: Dialect = Dialect { comments: false, trailing_commas: false, bom: false };
}

#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    Null,
    Bool(bool),
    /// The number and its source text (Kiln's readers tell `1` from `1.0`: only the first is an integer).
    Number(f64, String),
    String(String),
    Array(Vec<Node>),
    Object(Vec<Member>),
}

#[derive(Debug, Clone, PartialEq)]
pub struct Node {
    pub value: Value,
    pub span: Span,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Member {
    pub key: String,
    /// The key with its quotes.
    pub key_span: Span,
    pub value: Node,
}

impl Node {
    pub fn kind(&self) -> &'static str {
        match self.value {
            Value::Null => "null",
            Value::Bool(_) => "boolean",
            Value::Number(_, ref raw) => {
                if is_integer_text(raw) {
                    "integer"
                } else {
                    "number"
                }
            }
            Value::String(_) => "string",
            Value::Array(_) => "array",
            Value::Object(_) => "object",
        }
    }
    pub fn as_str(&self) -> Option<&str> {
        match &self.value {
            Value::String(s) => Some(s),
            _ => None,
        }
    }
    pub fn as_f64(&self) -> Option<f64> {
        match &self.value {
            Value::Number(n, _) => Some(*n),
            _ => None,
        }
    }
    pub fn as_bool(&self) -> Option<bool> {
        match &self.value {
            Value::Bool(b) => Some(*b),
            _ => None,
        }
    }
    pub fn as_array(&self) -> Option<&[Node]> {
        match &self.value {
            Value::Array(a) => Some(a),
            _ => None,
        }
    }
    pub fn as_object(&self) -> Option<&[Member]> {
        match &self.value {
            Value::Object(m) => Some(m),
            _ => None,
        }
    }
    /// The last member with this key (what Kiln's readers see when a key repeats).
    pub fn get(&self, key: &str) -> Option<&Node> {
        self.as_object()?.iter().rev().find(|m| m.key == key).map(|m| &m.value)
    }
    pub fn member(&self, key: &str) -> Option<&Member> {
        self.as_object()?.iter().rev().find(|m| m.key == key)
    }
    pub fn str_of(&self, key: &str) -> Option<&str> {
        self.get(key).and_then(Node::as_str)
    }
    /// Follows a JSON pointer ("/items/add/0/guid").
    pub fn pointer(&self, ptr: &str) -> Option<&Node> {
        let mut n = self;
        for raw in ptr.split('/').skip(1) {
            let seg = raw.replace("~1", "/").replace("~0", "~");
            n = match &n.value {
                Value::Object(_) => n.get(&seg)?,
                Value::Array(a) => a.get(seg.parse::<usize>().ok()?)?,
                _ => return None,
            };
        }
        Some(n)
    }
    /// serde_json form (for code that wants plain values).
    pub fn to_json(&self) -> serde_json::Value {
        match &self.value {
            Value::Null => serde_json::Value::Null,
            Value::Bool(b) => serde_json::Value::Bool(*b),
            Value::Number(n, raw) => {
                if is_integer_text(raw) {
                    if let Ok(i) = raw.parse::<i64>() {
                        return serde_json::Value::from(i);
                    }
                    if let Ok(u) = raw.parse::<u64>() {
                        return serde_json::Value::from(u);
                    }
                }
                serde_json::Number::from_f64(*n).map(serde_json::Value::Number).unwrap_or(serde_json::Value::Null)
            }
            Value::String(s) => serde_json::Value::String(s.clone()),
            Value::Array(a) => serde_json::Value::Array(a.iter().map(Node::to_json).collect()),
            Value::Object(m) => {
                let mut o = serde_json::Map::new();
                for x in m {
                    o.insert(x.key.clone(), x.value.to_json());
                }
                serde_json::Value::Object(o)
            }
        }
    }
}

pub fn is_integer_text(raw: &str) -> bool {
    !raw.contains(['.', 'e', 'E'])
}

/// Escapes a key for a JSON pointer segment.
pub fn ptr_seg(s: &str) -> String {
    s.replace('~', "~0").replace('/', "~1")
}

/// The result of reading one document: the tree (None after a syntax error) and what was found on the way.
pub struct Parsed {
    pub root: Option<Node>,
    pub diagnostics: Vec<Diagnostic>,
    /// Byte offset where the JSON text starts inside the file (after a BOM, or inside a binary container).
    pub base: usize,
}

/// Parses `text` (which starts at byte `base` of the file `file`) with the given dialect.
pub fn parse(file: &str, text: &str, base: usize, dialect: Dialect) -> Parsed {
    let mut p = Parser { file, src: text.as_bytes(), text, pos: 0, base, dialect, diags: Vec::new() };
    let mut start = 0;
    if text.starts_with('\u{feff}') {
        start = 3;
        if !dialect.bom {
            let span = Span::new(base, base + 3);
            p.diags.push(
                Diagnostic::new(file, codes::BOM, span, "a UTF-8 byte-order mark: this file's reader (Python's json) refuses it")
                    .with_fix(Fix::new("Remove the byte-order mark", vec![TextEdit::new(file, span, "")])),
            );
        }
    }
    p.pos = start;
    p.skip_ws();
    if p.pos >= p.src.len() {
        p.diags.push(Diagnostic::new(file, codes::SYNTAX, Span::new(base + p.pos, base + p.pos), "empty file: expected a JSON value"));
        return Parsed { root: None, diagnostics: p.diags, base };
    }
    let root = match p.value(0) {
        Ok(n) => {
            p.skip_ws();
            if p.pos < p.src.len() {
                let at = p.pos;
                p.error(at, at + p.char_len(at), "unexpected text after the JSON value");
                None
            } else {
                Some(n)
            }
        }
        Err(()) => None,
    };
    Parsed { root, diagnostics: p.diags, base }
}

struct Parser<'a> {
    file: &'a str,
    src: &'a [u8],
    text: &'a str,
    pos: usize,
    base: usize,
    dialect: Dialect,
    diags: Vec<Diagnostic>,
}

const MAX_DEPTH: usize = 256;

impl Parser<'_> {
    fn span(&self, a: usize, b: usize) -> Span {
        Span::new(self.base + a, self.base + b)
    }
    fn error(&mut self, a: usize, b: usize, msg: &str) {
        let span = self.span(a, b.max(a));
        self.diags.push(Diagnostic::new(self.file, codes::SYNTAX, span, msg));
    }
    fn char_len(&self, at: usize) -> usize {
        self.text[at..].chars().next().map(char::len_utf8).unwrap_or(0)
    }
    fn found(&self) -> String {
        match self.text[self.pos..].chars().next() {
            None => "the end of the file".into(),
            Some(c) => format!("'{}'", c.escape_debug()),
        }
    }

    fn skip_ws(&mut self) {
        loop {
            while self.pos < self.src.len() && matches!(self.src[self.pos], b' ' | b'\t' | b'\n' | b'\r') {
                self.pos += 1;
            }
            if self.pos + 1 < self.src.len() && self.src[self.pos] == b'/' && (self.src[self.pos + 1] == b'/' || self.src[self.pos + 1] == b'*') {
                let start = self.pos;
                if self.src[self.pos + 1] == b'/' {
                    while self.pos < self.src.len() && self.src[self.pos] != b'\n' {
                        self.pos += 1;
                    }
                } else {
                    self.pos += 2;
                    let mut closed = false;
                    while self.pos + 1 < self.src.len() {
                        if self.src[self.pos] == b'*' && self.src[self.pos + 1] == b'/' {
                            self.pos += 2;
                            closed = true;
                            break;
                        }
                        self.pos += 1;
                    }
                    if !closed {
                        self.pos = self.src.len();
                        self.error(start, start + 2, "unterminated /* comment");
                    }
                }
                if !self.dialect.comments {
                    let span = self.span(start, self.pos);
                    self.diags.push(
                        Diagnostic::new(self.file, codes::COMMENT, span, "comments are not allowed in this file (its reader takes plain JSON)")
                            .with_fix(Fix::new("Remove the comment", vec![TextEdit::new(self.file, span, "")])),
                    );
                }
                continue;
            }
            break;
        }
    }

    fn value(&mut self, depth: usize) -> Result<Node, ()> {
        if depth > MAX_DEPTH {
            let at = self.pos;
            self.error(at, at + 1, "nested too deeply");
            return Err(());
        }
        let start = self.pos;
        let Some(&c) = self.src.get(self.pos) else {
            let at = self.pos;
            self.error(at, at, "unexpected end of the file: expected a value");
            return Err(());
        };
        let value = match c {
            b'{' => return self.object(depth),
            b'[' => return self.array(depth),
            b'"' => Value::String(self.string()?),
            b'-' | b'0'..=b'9' => self.number()?,
            b't' | b'f' | b'n' => {
                let word: &[u8] = match c {
                    b't' => b"true",
                    b'f' => b"false",
                    _ => b"null",
                };
                if self.src[self.pos..].starts_with(word) && !self.src.get(self.pos + word.len()).is_some_and(|b| b.is_ascii_alphanumeric() || *b == b'_') {
                    self.pos += word.len();
                    match c {
                        b't' => Value::Bool(true),
                        b'f' => Value::Bool(false),
                        _ => Value::Null,
                    }
                } else {
                    return Err(self.bad_token(start));
                }
            }
            b'\'' => {
                let at = self.pos;
                self.error(at, at + 1, "strings use double quotes \" in JSON");
                return Err(());
            }
            _ => return Err(self.bad_token(start)),
        };
        Ok(Node { value, span: self.span(start, self.pos) })
    }

    fn bad_token(&mut self, start: usize) {
        let mut end = start;
        while end < self.src.len() && (self.src[end].is_ascii_alphanumeric() || self.src[end] == b'_' || self.src[end] >= 0x80) {
            end += 1;
        }
        if end == start {
            end = start + self.char_len(start);
        }
        let word = &self.text[start..end];
        let msg = match word {
            "NaN" | "Infinity" | "undefined" => format!("{word} is not a JSON value"),
            "True" | "False" | "TRUE" | "FALSE" => format!("{word}: JSON writes true / false in lower case"),
            "None" | "NULL" | "Null" => format!("{word}: JSON writes null in lower case"),
            _ => format!("expected a value, found {}", if word.is_empty() { "the end of the file".to_string() } else { format!("'{word}'") }),
        };
        self.error(start, end, &msg);
    }

    fn number(&mut self) -> Result<Value, ()> {
        let start = self.pos;
        let s = self.src;
        let mut i = self.pos;
        if s.get(i) == Some(&b'-') {
            i += 1;
        }
        match s.get(i) {
            Some(b'0') => {
                i += 1;
                if s.get(i).is_some_and(u8::is_ascii_digit) {
                    self.error(start, i + 1, "numbers do not start with 0 in JSON");
                    return Err(());
                }
                if s.get(i).is_some_and(|b| *b == b'x' || *b == b'X') {
                    self.error(start, i + 1, "hex numbers are not JSON: write \"0x...\" as a string");
                    return Err(());
                }
            }
            Some(b'1'..=b'9') => {
                while s.get(i).is_some_and(u8::is_ascii_digit) {
                    i += 1;
                }
            }
            _ => {
                self.error(start, i + 1, "invalid number");
                return Err(());
            }
        }
        if s.get(i) == Some(&b'.') {
            i += 1;
            if !s.get(i).is_some_and(u8::is_ascii_digit) {
                self.error(start, i, "a digit must follow the decimal point");
                return Err(());
            }
            while s.get(i).is_some_and(u8::is_ascii_digit) {
                i += 1;
            }
        }
        if s.get(i).is_some_and(|b| *b == b'e' || *b == b'E') {
            i += 1;
            if s.get(i).is_some_and(|b| *b == b'+' || *b == b'-') {
                i += 1;
            }
            if !s.get(i).is_some_and(u8::is_ascii_digit) {
                self.error(start, i, "invalid exponent");
                return Err(());
            }
            while s.get(i).is_some_and(u8::is_ascii_digit) {
                i += 1;
            }
        }
        self.pos = i;
        let raw = &self.text[start..i];
        let n: f64 = raw.parse().unwrap_or(f64::NAN);
        Ok(Value::Number(n, raw.to_string()))
    }

    fn string(&mut self) -> Result<String, ()> {
        let start = self.pos;
        self.pos += 1; // opening quote
        let mut out = String::new();
        loop {
            let Some(&b) = self.src.get(self.pos) else {
                self.error(start, self.pos, "unterminated string");
                return Err(());
            };
            match b {
                b'"' => {
                    self.pos += 1;
                    return Ok(out);
                }
                b'\\' => {
                    let esc = self.pos;
                    self.pos += 1;
                    let Some(&e) = self.src.get(self.pos) else {
                        self.error(start, self.pos, "unterminated string");
                        return Err(());
                    };
                    self.pos += 1;
                    match e {
                        b'"' => out.push('"'),
                        b'\\' => out.push('\\'),
                        b'/' => out.push('/'),
                        b'b' => out.push('\u{8}'),
                        b'f' => out.push('\u{c}'),
                        b'n' => out.push('\n'),
                        b'r' => out.push('\r'),
                        b't' => out.push('\t'),
                        b'u' => {
                            let hi = self.hex4(esc)?;
                            if (0xD800..0xDC00).contains(&hi) {
                                if self.src.get(self.pos) == Some(&b'\\') && self.src.get(self.pos + 1) == Some(&b'u') {
                                    self.pos += 2;
                                    let lo = self.hex4(esc)?;
                                    if (0xDC00..0xE000).contains(&lo) {
                                        let c = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                                        out.push(char::from_u32(c).unwrap_or('\u{fffd}'));
                                    } else {
                                        self.error(esc, self.pos, "invalid surrogate pair");
                                        return Err(());
                                    }
                                } else {
                                    self.error(esc, self.pos, "a lone high surrogate");
                                    return Err(());
                                }
                            } else if (0xDC00..0xE000).contains(&hi) {
                                self.error(esc, self.pos, "a lone low surrogate");
                                return Err(());
                            } else {
                                out.push(char::from_u32(hi).unwrap_or('\u{fffd}'));
                            }
                        }
                        _ => {
                            self.error(esc, self.pos, &format!("invalid escape \\{}", e as char));
                            return Err(());
                        }
                    }
                }
                0..=0x1f => {
                    let at = self.pos;
                    let msg = if b == b'\n' { "unterminated string (a line break inside a string must be written \\n)" } else { "control character in a string (escape it)" };
                    self.error(at, at + 1, msg);
                    return Err(());
                }
                _ => {
                    let c = self.text[self.pos..].chars().next().unwrap();
                    out.push(c);
                    self.pos += c.len_utf8();
                }
            }
        }
    }

    fn hex4(&mut self, esc: usize) -> Result<u32, ()> {
        let h = self.src.get(self.pos..self.pos + 4).and_then(|x| std::str::from_utf8(x).ok()).and_then(|x| u32::from_str_radix(x, 16).ok());
        match h {
            Some(v) => {
                self.pos += 4;
                Ok(v)
            }
            None => {
                self.error(esc, (self.pos + 4).min(self.src.len()), "invalid \\u escape (four hex digits)");
                Err(())
            }
        }
    }

    fn trailing_comma(&mut self, comma: usize) {
        if !self.dialect.trailing_commas {
            let span = self.span(comma, comma + 1);
            self.diags.push(
                Diagnostic::new(self.file, codes::TRAILING_COMMA, span, "trailing comma: this file's reader refuses it")
                    .with_fix(Fix::new("Remove the comma", vec![TextEdit::new(self.file, span, "")])),
            );
        }
    }

    fn object(&mut self, depth: usize) -> Result<Node, ()> {
        let start = self.pos;
        self.pos += 1;
        let mut members: Vec<Member> = Vec::new();
        let mut last_comma: Option<usize> = None;
        loop {
            self.skip_ws();
            match self.src.get(self.pos) {
                Some(b'}') => {
                    if let Some(c) = last_comma {
                        self.trailing_comma(c);
                    }
                    self.pos += 1;
                    break;
                }
                Some(b'"') => {}
                None => {
                    self.error(start, start + 1, "unclosed object: '}' expected");
                    return Err(());
                }
                Some(_) => {
                    let at = self.pos;
                    let msg = if members.is_empty() || last_comma.is_some() {
                        format!("expected a key in double quotes or '}}', found {}", self.found())
                    } else {
                        format!("expected ',' or '}}', found {}", self.found())
                    };
                    self.error(at, at + self.char_len(at), &msg);
                    return Err(());
                }
            }
            if !members.is_empty() && last_comma.is_none() {
                let at = self.pos;
                self.error(at, at + 1, "expected ',' between members");
                return Err(());
            }
            let ks = self.pos;
            let key = self.string()?;
            let key_span = self.span(ks, self.pos);
            self.skip_ws();
            if self.src.get(self.pos) != Some(&b':') {
                let at = self.pos;
                let msg = format!("expected ':' after the key, found {}", self.found());
                self.error(at, at + self.char_len(at).max(1), &msg);
                return Err(());
            }
            self.pos += 1;
            self.skip_ws();
            let value = self.value(depth + 1)?;
            if let Some(first) = members.iter().find(|m| m.key == key) {
                self.diags.push(
                    Diagnostic::new(self.file, codes::DUPLICATE_KEY, key_span, &format!("duplicate key \"{key}\": the later value is the one Kiln reads"))
                        .with_related(self.file, first.key_span, "first defined here"),
                );
            }
            members.push(Member { key, key_span, value });
            self.skip_ws();
            last_comma = None;
            if self.src.get(self.pos) == Some(&b',') {
                last_comma = Some(self.pos);
                self.pos += 1;
            }
        }
        Ok(Node { value: Value::Object(members), span: self.span(start, self.pos) })
    }

    fn array(&mut self, depth: usize) -> Result<Node, ()> {
        let start = self.pos;
        self.pos += 1;
        let mut items = Vec::new();
        let mut last_comma: Option<usize> = None;
        loop {
            self.skip_ws();
            match self.src.get(self.pos) {
                Some(b']') => {
                    if let Some(c) = last_comma {
                        self.trailing_comma(c);
                    }
                    self.pos += 1;
                    break;
                }
                None => {
                    self.error(start, start + 1, "unclosed array: ']' expected");
                    return Err(());
                }
                Some(b',') if items.is_empty() || last_comma.is_some() => {
                    let at = self.pos;
                    self.error(at, at + 1, "expected a value before ','");
                    return Err(());
                }
                _ => {}
            }
            if !items.is_empty() && last_comma.is_none() {
                let at = self.pos;
                let msg = format!("expected ',' or ']', found {}", self.found());
                self.error(at, at + self.char_len(at).max(1), &msg);
                return Err(());
            }
            items.push(self.value(depth + 1)?);
            self.skip_ws();
            last_comma = None;
            if self.src.get(self.pos) == Some(&b',') {
                last_comma = Some(self.pos);
                self.pos += 1;
            }
        }
        Ok(Node { value: Value::Array(items), span: self.span(start, self.pos) })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn codes_of(p: &Parsed) -> Vec<&str> {
        p.diagnostics.iter().map(|d| d.code.as_str()).collect()
    }

    #[test]
    fn spans_and_values() {
        let t = r#"{ "a": [1, 2.5, "xé"], "b": {"c": null} }"#;
        let p = parse("f", t, 0, Dialect::STRICT);
        assert!(p.diagnostics.is_empty());
        let r = p.root.unwrap();
        let a = r.get("a").unwrap();
        assert_eq!(&t[a.span.start..a.span.end], r#"[1, 2.5, "xé"]"#);
        assert_eq!(a.as_array().unwrap()[2].as_str(), Some("xé"));
        assert_eq!(a.as_array().unwrap()[0].kind(), "integer");
        assert_eq!(a.as_array().unwrap()[1].kind(), "number");
        let m = r.member("b").unwrap();
        assert_eq!(&t[m.key_span.start..m.key_span.end], "\"b\"");
        assert_eq!(r.pointer("/b/c").unwrap().value, Value::Null);
    }

    #[test]
    fn dialects() {
        let t = "{\n // c\n \"a\": 1, /* x */ \"b\": [1,],\n}";
        let strict = parse("f", t, 0, Dialect::STRICT);
        assert_eq!(codes_of(&strict), vec![codes::COMMENT, codes::COMMENT, codes::TRAILING_COMMA, codes::TRAILING_COMMA]);
        assert!(strict.root.is_some());
        let content = parse("f", t, 0, Dialect { comments: true, trailing_commas: false, bom: true });
        assert_eq!(codes_of(&content), vec![codes::TRAILING_COMMA, codes::TRAILING_COMMA]);
        assert!(parse("f", t, 0, Dialect::JSONC).diagnostics.is_empty());
    }

    #[test]
    fn errors() {
        for (t, want) in [
            ("{\"a\" 1}", "expected ':'"),
            ("{\"a\": 1 \"b\": 2}", "expected ','"),
            ("[1 2]", "expected ',' or ']'"),
            ("{'a': 1}", "expected a key"),
            ("[NaN]", "NaN is not a JSON value"),
            ("[True]", "lower case"),
            ("[01]", "do not start with 0"),
            ("[0x10]", "hex numbers"),
            ("{\"a\": \"x", "unterminated string"),
            ("{\"a\": 1", "unclosed object"),
            ("", "empty file"),
            ("{} x", "after the JSON value"),
        ] {
            let p = parse("f", t, 0, Dialect::JSONC);
            assert!(p.root.is_none(), "{t}");
            assert!(p.diagnostics.iter().any(|d| d.message.contains(want)), "{t}: {:?}", p.diagnostics);
        }
    }

    #[test]
    fn duplicates_and_bom() {
        let t = "\u{feff}{\"a\": 1, \"a\": 2}";
        let p = parse("f", t, 0, Dialect::STRICT);
        assert_eq!(codes_of(&p), vec![codes::BOM, codes::DUPLICATE_KEY]);
        assert_eq!(p.root.unwrap().get("a").unwrap().as_f64(), Some(2.0));
        assert_eq!(codes_of(&parse("f", t, 0, Dialect::JSONC)), vec![codes::DUPLICATE_KEY]);
    }
}
