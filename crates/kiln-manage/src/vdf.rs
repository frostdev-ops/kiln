//! Valve KeyValues (text VDF: libraryfolders.vdf, appmanifest_*.acf, config.vdf, localconfig.vdf). Read-only:
//! Kiln never writes Steam's files.
//!
//! Grammar handled: quoted tokens (`"..."` with `\"`, `\\`, `\n`, `\t` escapes), bare tokens (up to whitespace, `{`,
//! `}` or `"`), `{ }` blocks, `//` comments, a UTF-8 BOM, CRLF line ends, and conditionals after a value or block
//! (`"key" "value" [$WIN32]`), which are skipped (the entry is kept whatever the condition says).

use crate::{Error, ErrorCode, Result};

/// A KeyValues node: a string, or an ordered map (keys may repeat; lookups are case-insensitive, first match).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Vdf {
    Str(String),
    Map(Vec<(String, Vdf)>),
}

impl Vdf {
    pub fn get(&self, key: &str) -> Option<&Vdf> {
        match self {
            Vdf::Map(items) => items.iter().find(|(k, _)| k.eq_ignore_ascii_case(key)).map(|(_, v)| v),
            Vdf::Str(_) => None,
        }
    }

    /// Follows a path of keys.
    pub fn path(&self, keys: &[&str]) -> Option<&Vdf> {
        keys.iter().try_fold(self, |node, k| node.get(k))
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Vdf::Str(s) => Some(s),
            Vdf::Map(_) => None,
        }
    }

    pub fn entries(&self) -> &[(String, Vdf)] {
        match self {
            Vdf::Map(items) => items,
            Vdf::Str(_) => &[],
        }
    }

    /// The string at a path of keys (case-insensitive).
    pub fn str_at(&self, keys: &[&str]) -> Option<&str> {
        self.path(keys)?.as_str()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Tok {
    Str(String),
    Open,
    Close,
    Cond,
}

struct Lexer<'a> {
    s: &'a [u8],
    i: usize,
    line: usize,
}

fn err(line: usize, msg: impl std::fmt::Display) -> Error {
    Error::new(ErrorCode::Parse, format!("VDF line {line}: {msg}"))
}

impl<'a> Lexer<'a> {
    fn skip_space(&mut self) {
        while self.i < self.s.len() {
            match self.s[self.i] {
                b'\n' => {
                    self.line += 1;
                    self.i += 1;
                }
                b' ' | b'\t' | b'\r' => self.i += 1,
                b'/' if self.s.get(self.i + 1) == Some(&b'/') => {
                    while self.i < self.s.len() && self.s[self.i] != b'\n' {
                        self.i += 1;
                    }
                }
                _ => break,
            }
        }
    }

    /// The next token with the line it starts on; None at the end.
    fn next(&mut self) -> Result<Option<(Tok, usize)>> {
        self.skip_space();
        if self.i >= self.s.len() {
            return Ok(None);
        }
        let line = self.line;
        let c = self.s[self.i];
        match c {
            b'{' => {
                self.i += 1;
                Ok(Some((Tok::Open, line)))
            }
            b'}' => {
                self.i += 1;
                Ok(Some((Tok::Close, line)))
            }
            b'[' => {
                // conditional: [$WIN32], [!$X360 && $PS3] ...
                while self.i < self.s.len() && self.s[self.i] != b']' && self.s[self.i] != b'\n' {
                    self.i += 1;
                }
                if self.s.get(self.i) != Some(&b']') {
                    return Err(err(line, "unclosed conditional '['"));
                }
                self.i += 1;
                Ok(Some((Tok::Cond, line)))
            }
            b'"' => {
                self.i += 1;
                let mut out: Vec<u8> = Vec::new();
                loop {
                    let Some(&b) = self.s.get(self.i) else {
                        return Err(err(line, "unterminated string"));
                    };
                    self.i += 1;
                    match b {
                        b'"' => break,
                        b'\\' => {
                            let Some(&e) = self.s.get(self.i) else {
                                return Err(err(line, "unterminated string"));
                            };
                            self.i += 1;
                            match e {
                                b'n' => out.push(b'\n'),
                                b't' => out.push(b'\t'),
                                b'r' => out.push(b'\r'),
                                b'"' => out.push(b'"'),
                                b'\\' => out.push(b'\\'),
                                other => {
                                    // Steam keeps unknown escapes as written (paths like "C:\Games" in old files)
                                    out.push(b'\\');
                                    out.push(other);
                                }
                            }
                        }
                        b'\n' => {
                            self.line += 1;
                            out.push(b);
                        }
                        _ => out.push(b),
                    }
                }
                Ok(Some((Tok::Str(String::from_utf8_lossy(&out).into_owned()), line)))
            }
            _ => {
                let start = self.i;
                while self.i < self.s.len() {
                    let b = self.s[self.i];
                    if matches!(b, b' ' | b'\t' | b'\r' | b'\n' | b'{' | b'}' | b'"') {
                        break;
                    }
                    if b == b'/' && self.s.get(self.i + 1) == Some(&b'/') {
                        break;
                    }
                    self.i += 1;
                }
                Ok(Some((Tok::Str(String::from_utf8_lossy(&self.s[start..self.i]).into_owned()), line)))
            }
        }
    }
}

struct Parser<'a> {
    lex: Lexer<'a>,
    peeked: Option<Option<(Tok, usize)>>,
}

impl Parser<'_> {
    fn peek(&mut self) -> Result<Option<&(Tok, usize)>> {
        if self.peeked.is_none() {
            self.peeked = Some(self.lex.next()?);
        }
        Ok(self.peeked.as_ref().and_then(|p| p.as_ref()))
    }

    fn take(&mut self) -> Result<Option<(Tok, usize)>> {
        match self.peeked.take() {
            Some(p) => Ok(p),
            None => self.lex.next(),
        }
    }

    fn skip_cond(&mut self) -> Result<()> {
        while let Some((Tok::Cond, _)) = self.peek()? {
            self.take()?;
        }
        Ok(())
    }

    /// Entries until `}` (nested) or the end (root).
    fn map(&mut self, nested: bool, open_line: usize) -> Result<Vec<(String, Vdf)>> {
        let mut items = Vec::new();
        loop {
            let Some((tok, line)) = self.take()? else {
                if nested {
                    return Err(err(open_line, "'{' is never closed"));
                }
                return Ok(items);
            };
            let key = match tok {
                Tok::Close if nested => return Ok(items),
                Tok::Close => return Err(err(line, "unexpected '}'")),
                Tok::Open => return Err(err(line, "'{' without a key")),
                Tok::Cond => continue,
                Tok::Str(k) => k,
            };
            self.skip_cond()?;
            match self.take()? {
                Some((Tok::Str(v), _)) => items.push((key, Vdf::Str(v))),
                Some((Tok::Open, l)) => {
                    let m = self.map(true, l)?;
                    items.push((key, Vdf::Map(m)));
                }
                Some((Tok::Close, l)) => return Err(err(l, format!("key \"{key}\" has no value"))),
                Some((Tok::Cond, l)) => return Err(err(l, "misplaced conditional")),
                None => return Err(err(line, format!("key \"{key}\" has no value"))),
            }
            self.skip_cond()?;
        }
    }
}

/// Parses text KeyValues (quoted and bare tokens, `{ }`, `//` comments, `\"` `\\` escapes). The root is a map.
pub fn parse(text: &str) -> Result<Vdf> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let mut p = Parser { lex: Lexer { s: text.as_bytes(), i: 0, line: 1 }, peeked: None };
    Ok(Vdf::Map(p.map(false, 1)?))
}

/// Reads and parses a VDF file (lossy UTF-8).
pub fn read(path: &std::path::Path) -> Result<Vdf> {
    let bytes = std::fs::read(path).map_err(|e| Error::from(e).with_detail(path.display().to_string()))?;
    parse(&String::from_utf8_lossy(&bytes)).map_err(|e| Error::new(e.code, format!("{}: {}", path.display(), e.message)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nested_quoted_bare_comments_escapes() {
        let t = "\u{feff}// comment\r\n\"AppState\"\r\n{\r\n\t\"appid\"\t\t\"1203620\"\r\n\tname \"En \\\"shrouded\\\"\" // x\r\n\
                 \t\"Inner\" { \"a\" \"1\" [$WIN32] \"b\" \"C:\\\\Games\" }\r\n}\r\n";
        let v = parse(t).unwrap();
        assert_eq!(v.str_at(&["appstate", "APPID"]), Some("1203620"));
        assert_eq!(v.str_at(&["AppState", "name"]), Some("En \"shrouded\""));
        assert_eq!(v.str_at(&["AppState", "Inner", "a"]), Some("1"));
        assert_eq!(v.str_at(&["AppState", "Inner", "b"]), Some("C:\\Games"));
    }

    #[test]
    fn repeated_keys_keep_order_and_first_match_wins() {
        let v = parse("\"r\" { \"k\" \"1\" \"k\" \"2\" }").unwrap();
        assert_eq!(v.get("r").unwrap().entries().len(), 2);
        assert_eq!(v.str_at(&["r", "k"]), Some("1"));
    }

    #[test]
    fn errors_name_the_line() {
        let e = parse("\"a\"\n{\n\"b\" \"c\"\n").unwrap_err();
        assert_eq!(e.code, ErrorCode::Parse);
        assert!(e.message.contains("line 2"), "{}", e.message);
        assert!(parse("\"a\" \"unterminated").is_err());
        assert!(parse("}").is_err());
        assert!(parse("\"lonely\"").is_err());
        assert_eq!(parse("").unwrap(), Vdf::Map(vec![]));
    }
}
