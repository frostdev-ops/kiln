//! A small backtracking matcher for the ECMA-262 `pattern`s in schema/*.schema.json (JSON Schema patterns are
//! unanchored searches). It covers what Kiln's schemas use: classes, \d \w \s and their negations, groups
//! (capturing, `(?:`, lookahead `(?=` / `(?!`), alternation, `* + ? {n} {n,} {n,m}` (lazy forms parse as greedy:
//! the same strings match), `^ $ .`. Unsupported syntax is an error, so a schema cannot silently mean something else.

#[derive(Debug, Clone)]
enum Re {
    Char(char),
    Any,
    Class(Vec<(char, char)>, bool),
    Start,
    End,
    Look(Box<Re>, bool),
    Concat(Vec<Re>),
    Alt(Vec<Re>),
    Repeat(Box<Re>, u32, Option<u32>),
}

#[derive(Debug, Clone)]
pub struct Regex {
    re: Re,
}

impl Regex {
    pub fn new(pattern: &str) -> Result<Regex, String> {
        let chars: Vec<char> = pattern.chars().collect();
        let mut p = P { c: &chars, i: 0 };
        let re = p.alt()?;
        if p.i != chars.len() {
            return Err(format!("unexpected '{}' at {}", chars[p.i], p.i));
        }
        Ok(Regex { re })
    }

    /// True when the pattern matches anywhere in `s` (JSON Schema `pattern` semantics).
    pub fn is_match(&self, s: &str) -> bool {
        let chars: Vec<char> = s.chars().collect();
        (0..=chars.len()).any(|i| mat(&self.re, &chars, i, &mut |_| true))
    }
}

struct P<'a> {
    c: &'a [char],
    i: usize,
}

impl P<'_> {
    fn peek(&self) -> Option<char> {
        self.c.get(self.i).copied()
    }
    fn alt(&mut self) -> Result<Re, String> {
        let mut alts = vec![self.concat()?];
        while self.peek() == Some('|') {
            self.i += 1;
            alts.push(self.concat()?);
        }
        Ok(if alts.len() == 1 { alts.pop().unwrap() } else { Re::Alt(alts) })
    }
    fn concat(&mut self) -> Result<Re, String> {
        let mut items = Vec::new();
        while let Some(c) = self.peek() {
            if c == '|' || c == ')' {
                break;
            }
            let atom = self.atom()?;
            let atom = self.quantifier(atom)?;
            items.push(atom);
        }
        Ok(Re::Concat(items))
    }
    fn quantifier(&mut self, atom: Re) -> Result<Re, String> {
        let (min, max) = match self.peek() {
            Some('*') => (0, None),
            Some('+') => (1, None),
            Some('?') => (0, Some(1)),
            Some('{') => {
                let save = self.i;
                self.i += 1;
                let a = self.int();
                let (lo, hi) = match (a, self.peek()) {
                    (Some(a), Some('}')) => (a, Some(a)),
                    (Some(a), Some(',')) => {
                        self.i += 1;
                        let b = self.int();
                        if self.peek() != Some('}') {
                            return Err("bad {n,m}".into());
                        }
                        (a, b)
                    }
                    _ => {
                        // not a quantifier: a literal '{' (as in JS without the u flag)
                        self.i = save;
                        return Ok(atom);
                    }
                };
                if let Some(h) = hi
                    && h < lo
                {
                    return Err("{n,m} with m < n".into());
                }
                (lo, hi)
            }
            _ => return Ok(atom),
        };
        self.i += 1;
        if self.peek() == Some('?') {
            self.i += 1; // lazy: same set of matching strings
        }
        if matches!(atom, Re::Start | Re::End | Re::Look(..)) {
            return Err("quantifier after an assertion".into());
        }
        Ok(Re::Repeat(Box::new(atom), min, max))
    }
    fn int(&mut self) -> Option<u32> {
        let s = self.i;
        while self.peek().is_some_and(|c| c.is_ascii_digit()) {
            self.i += 1;
        }
        if s == self.i { None } else { self.c[s..self.i].iter().collect::<String>().parse().ok() }
    }
    fn atom(&mut self) -> Result<Re, String> {
        let c = self.peek().ok_or("unexpected end")?;
        self.i += 1;
        Ok(match c {
            '^' => Re::Start,
            '$' => Re::End,
            '.' => Re::Any,
            '(' => {
                let mut look = None;
                if self.peek() == Some('?') {
                    match self.c.get(self.i + 1) {
                        Some(':') => self.i += 2,
                        Some('=') => {
                            self.i += 2;
                            look = Some(false)
                        }
                        Some('!') => {
                            self.i += 2;
                            look = Some(true)
                        }
                        _ => return Err("unsupported group".into()),
                    }
                }
                let inner = self.alt()?;
                if self.peek() != Some(')') {
                    return Err("missing )".into());
                }
                self.i += 1;
                match look {
                    Some(neg) => Re::Look(Box::new(inner), neg),
                    None => inner,
                }
            }
            '[' => self.class()?,
            '\\' => self.escape(false)?,
            '*' | '+' | '?' => return Err(format!("nothing to repeat before '{c}'")),
            ')' => return Err("unbalanced )".into()),
            c => Re::Char(c),
        })
    }
    fn escape(&mut self, in_class: bool) -> Result<Re, String> {
        let c = self.peek().ok_or("trailing \\")?;
        self.i += 1;
        let digit = vec![('0', '9')];
        let word = vec![('a', 'z'), ('A', 'Z'), ('0', '9'), ('_', '_')];
        let space = vec![(' ', ' '), ('\t', '\r'), ('\u{a0}', '\u{a0}'), ('\u{feff}', '\u{feff}'), ('\u{2028}', '\u{2029}'), ('\u{1680}', '\u{1680}'), ('\u{2000}', '\u{200a}'), ('\u{202f}', '\u{202f}'), ('\u{205f}', '\u{205f}'), ('\u{3000}', '\u{3000}')];
        Ok(match c {
            'd' => Re::Class(digit, false),
            'D' => Re::Class(digit, true),
            'w' => Re::Class(word, false),
            'W' => Re::Class(word, true),
            's' => Re::Class(space, false),
            'S' => Re::Class(space, true),
            'n' => Re::Char('\n'),
            'r' => Re::Char('\r'),
            't' => Re::Char('\t'),
            'f' => Re::Char('\u{c}'),
            'v' => Re::Char('\u{b}'),
            '0' => Re::Char('\0'),
            'b' if in_class => Re::Char('\u{8}'),
            'u' => {
                let h: String = self.c.get(self.i..self.i + 4).ok_or("bad \\u")?.iter().collect();
                self.i += 4;
                Re::Char(char::from_u32(u32::from_str_radix(&h, 16).map_err(|_| "bad \\u")?).ok_or("bad \\u")?)
            }
            c if c.is_ascii_alphanumeric() => return Err(format!("unsupported escape \\{c}")),
            c => Re::Char(c),
        })
    }
    fn class(&mut self) -> Result<Re, String> {
        let mut neg = false;
        if self.peek() == Some('^') {
            neg = true;
            self.i += 1;
        }
        let mut ranges: Vec<(char, char)> = Vec::new();
        let mut first = true;
        loop {
            let c = self.peek().ok_or("missing ]")?;
            if c == ']' && !first {
                self.i += 1;
                break;
            }
            first = false;
            self.i += 1;
            let lo = if c == '\\' {
                match self.escape(true)? {
                    Re::Char(x) => x,
                    Re::Class(r, false) => {
                        ranges.extend(r);
                        continue;
                    }
                    Re::Class(r, true) => {
                        // a negated set inside a class: add its complement
                        let mut prev = '\0';
                        let mut sorted = r.clone();
                        sorted.sort();
                        for (a, b) in sorted {
                            if a > prev {
                                ranges.push((prev, char::from_u32(a as u32 - 1).unwrap_or(prev)));
                            }
                            prev = char::from_u32(b as u32 + 1).unwrap_or(b);
                        }
                        ranges.push((prev, char::MAX));
                        continue;
                    }
                    _ => return Err("bad class escape".into()),
                }
            } else {
                c
            };
            if self.peek() == Some('-') && self.c.get(self.i + 1).is_some_and(|x| *x != ']') {
                self.i += 1;
                let h = self.peek().ok_or("missing ]")?;
                self.i += 1;
                let hi = if h == '\\' {
                    match self.escape(true)? {
                        Re::Char(x) => x,
                        _ => return Err("bad range".into()),
                    }
                } else {
                    h
                };
                if hi < lo {
                    return Err("range out of order".into());
                }
                ranges.push((lo, hi));
            } else {
                ranges.push((lo, lo));
            }
        }
        Ok(Re::Class(ranges, neg))
    }
}

fn in_class(ranges: &[(char, char)], c: char) -> bool {
    ranges.iter().any(|(a, b)| *a <= c && c <= *b)
}

fn mat(re: &Re, s: &[char], i: usize, k: &mut dyn FnMut(usize) -> bool) -> bool {
    match re {
        Re::Char(c) => i < s.len() && s[i] == *c && k(i + 1),
        Re::Any => i < s.len() && !matches!(s[i], '\n' | '\r' | '\u{2028}' | '\u{2029}') && k(i + 1),
        Re::Class(r, neg) => i < s.len() && in_class(r, s[i]) != *neg && k(i + 1),
        Re::Start => i == 0 && k(i),
        Re::End => i == s.len() && k(i),
        Re::Look(inner, neg) => {
            let found = mat(inner, s, i, &mut |_| true);
            found != *neg && k(i)
        }
        Re::Concat(items) => seq(items, s, i, k),
        Re::Alt(alts) => alts.iter().any(|a| mat(a, s, i, k)),
        Re::Repeat(inner, min, max) => rep(inner, *min, *max, 0, s, i, k),
    }
}

fn seq(items: &[Re], s: &[char], i: usize, k: &mut dyn FnMut(usize) -> bool) -> bool {
    match items.split_first() {
        None => k(i),
        Some((first, rest)) => mat(first, s, i, &mut |j| seq(rest, s, j, k)),
    }
}

fn rep(inner: &Re, min: u32, max: Option<u32>, n: u32, s: &[char], i: usize, k: &mut dyn FnMut(usize) -> bool) -> bool {
    if max.is_none_or(|m| n < m) && mat(inner, s, i, &mut |j| (j != i || n < min) && rep(inner, min, max, n + 1, s, j, k)) {
        return true;
    }
    n >= min && k(i)
}

#[cfg(test)]
mod tests {
    use super::Regex;

    fn m(p: &str, s: &str) -> bool {
        Regex::new(p).unwrap().is_match(s)
    }

    #[test]
    fn basics() {
        assert!(m("^[a-z0-9][a-z0-9._-]{0,63}$", "my-mod"));
        assert!(!m("^[a-z0-9][a-z0-9._-]{0,63}$", "My-mod"));
        assert!(!m("^[a-z0-9][a-z0-9._-]{0,63}$", &"a".repeat(65)));
        assert!(m("[*?]", "Material_*"));
        assert!(!m("[*?]", "Material"));
        assert!(m("\\.[pP][nN][gG]$", "icons/x.PNG"));
        assert!(m("^(\\*|[A-Za-z0-9][A-Za-z0-9_-]{1,11})$", "pt-BR"));
        assert!(m("^(\\*|[A-Za-z0-9][A-Za-z0-9_-]{1,11})$", "*"));
        assert!(!m("^(\\*|[A-Za-z0-9][A-Za-z0-9_-]{1,11})$", "-x"));
        assert!(m("^item:[^.]+(\\..+)?$", "item:Weapon_X.perkReferences[1]"));
        assert!(!m("^item:[^.]+(\\..+)?$", "item:"));
        assert!(m("^0[xX][0-9a-fA-F]{1,8}$", "0xcdbe9c80"));
        assert!(m("a{2}b", "xaab"));
        assert!(m("a{", "a{"));
    }

    #[test]
    fn lookahead() {
        // common.schema.json relPath / gamePath
        let rel = "^(?![\\s\\S]*(^|/)\\.\\.(/|$))(?!/)[^\\\\:*?\"<>|]+$";
        assert!(m(rel, "kiln/isle-icons"));
        assert!(!m(rel, "../x"));
        assert!(!m(rel, "a/../x"));
        assert!(!m(rel, "/abs"));
        assert!(m(rel, "a/..b/c"));
        let file = "^(?![\\s\\S]*(^|[\\\\/])\\.\\.([\\\\/]|$))(?![\\\\/])(?![A-Za-z]:)[\\s\\S]+$";
        assert!(m(file, "cooked/x.bin"));
        assert!(!m(file, "C:/x.bin"));
        assert!(!m(file, "a\\..\\b"));
        assert!(m("^(?!kiln\\.)", "mymod.Ping"));
        assert!(!m("^(?!kiln\\.)", "kiln.Log"));
    }

    #[test]
    fn rejects_unknown_syntax() {
        assert!(Regex::new("(?<n>x)").is_err());
        assert!(Regex::new("\\p{L}").is_err());
        assert!(Regex::new("*a").is_err());
    }
}
