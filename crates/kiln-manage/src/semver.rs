//! A port of managed/Kiln.Host/SemVer.cs (SemVersion, VersionRange). Parity: schema/fixtures/semver.
//!
//! Numeric parts follow C#'s `int`: a part above 2147483647 does not parse. Build metadata is kept but never
//! compared. The derived `PartialEq` compares every field (build included); semver equality is
//! `compare(..) == Equal`.

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;
use std::fmt;

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct SemVersion {
    pub major: u64,
    pub minor: u64,
    pub patch: u64,
    pub pre: Vec<String>,
    pub build: String,
}

/// `int.TryParse(s, NumberStyles.None, InvariantCulture)`: ASCII digits only, no sign, fits in an i32.
fn parse_i32(s: &str) -> Option<u64> {
    if s.is_empty() || !s.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    s.parse::<u64>().ok().filter(|&v| v <= i32::MAX as u64)
}

/// `char.IsAsciiLetterOrDigit(c) || c == '-'` for every char, non-empty.
fn is_identifier(s: &str) -> bool {
    !s.is_empty() && s.chars().all(|c| c.is_ascii_alphanumeric() || c == '-')
}

/// SemVersion.TryParsePartial: `[major, minor, patch]`, count of numeric parts before a wildcard, pre, build,
/// wildcard.
pub(crate) struct Partial {
    pub parts: [u64; 3],
    pub count: usize,
    pub pre: Vec<String>,
    pub build: String,
    pub wildcard: bool,
}

pub(crate) fn try_parse_partial(text: &str) -> Option<Partial> {
    if text.trim().is_empty() {
        return None;
    }
    let mut s = text.trim();
    if s.starts_with(['v', 'V']) {
        s = &s[1..];
    }
    let mut build = String::new();
    let mut pre: Vec<String> = Vec::new();
    if let Some(plus) = s.find('+') {
        build = s[plus + 1..].to_string();
        if build.is_empty() || !build.split('.').all(is_identifier) {
            return None;
        }
        s = &s[..plus];
    }
    if let Some(dash) = s.find('-') {
        let p: Vec<&str> = s[dash + 1..].split('.').collect();
        if !p.iter().all(|x| is_identifier(x)) {
            return None;
        }
        if p.iter().any(|x| x.len() > 1 && x.starts_with('0') && x.bytes().all(|b| b.is_ascii_digit())) {
            return None;
        }
        pre = p.into_iter().map(str::to_string).collect();
        s = &s[..dash];
    }
    let nums: Vec<&str> = s.split('.').collect();
    if nums.is_empty() || nums.len() > 3 {
        return None;
    }
    let mut parts = [0u64; 3];
    let mut count = 0;
    let mut wildcard = false;
    for (i, n) in nums.iter().enumerate() {
        if matches!(*n, "x" | "X" | "*") {
            wildcard = true;
            continue;
        }
        if wildcard {
            return None;
        }
        if n.is_empty() || !n.bytes().all(|b| b.is_ascii_digit()) || (n.len() > 1 && n.starts_with('0')) {
            return None;
        }
        parts[i] = parse_i32(n)?;
        count = i + 1;
    }
    if wildcard && !pre.is_empty() {
        return None;
    }
    Some(Partial { parts, count, pre, build, wildcard })
}

impl SemVersion {
    pub fn new(major: u64, minor: u64, patch: u64) -> SemVersion {
        SemVersion { major, minor, patch, pre: Vec::new(), build: String::new() }
    }

    fn with_pre(major: u64, minor: u64, patch: u64, pre: Vec<String>) -> SemVersion {
        SemVersion { major, minor, patch, pre, build: String::new() }
    }

    /// SemVersion.TryParse: `v` prefix and 1 / 1.2 partials allowed, surrounding whitespace trimmed.
    pub fn parse(text: &str) -> Result<SemVersion> {
        SemVersion::try_parse(text).ok_or_else(|| Error::new(ErrorCode::Parse, format!("'{text}' is not a semantic version")))
    }

    pub fn try_parse(text: &str) -> Option<SemVersion> {
        let p = try_parse_partial(text)?;
        if p.count == 0 || p.wildcard {
            return None;
        }
        Some(SemVersion {
            major: p.parts[0],
            minor: if p.count > 1 { p.parts[1] } else { 0 },
            patch: if p.count > 2 { p.parts[2] } else { 0 },
            pre: p.pre,
            build: p.build,
        })
    }

    /// semver.org §11 precedence (build metadata ignored).
    pub fn compare(&self, other: &SemVersion) -> Ordering {
        let c = self.major.cmp(&other.major).then(self.minor.cmp(&other.minor)).then(self.patch.cmp(&other.patch));
        if c != Ordering::Equal {
            return c;
        }
        let (a, b) = (&self.pre, &other.pre);
        if a.is_empty() || b.is_empty() {
            return b.len().cmp(&a.len()); // release > pre-release
        }
        for (x, y) in a.iter().zip(b.iter()) {
            let c = match (parse_i32(x), parse_i32(y)) {
                (Some(xi), Some(yi)) => xi.cmp(&yi),
                (Some(_), None) => Ordering::Less,
                (None, Some(_)) => Ordering::Greater,
                (None, None) => x.as_bytes().cmp(y.as_bytes()),
            };
            if c != Ordering::Equal {
                return c;
            }
        }
        a.len().cmp(&b.len())
    }

    pub fn is_prerelease(&self) -> bool {
        !self.pre.is_empty()
    }

    /// The same version without pre-release and build.
    pub fn release(&self) -> SemVersion {
        SemVersion { major: self.major, minor: self.minor, patch: self.patch, pre: Vec::new(), build: String::new() }
    }

    /// `X.Y.Z-0`: the lowest version of X.Y.Z (below every pre-release of it).
    pub fn lowest(&self) -> SemVersion {
        SemVersion::with_pre(self.major, self.minor, self.patch, vec!["0".into()])
    }
}

impl fmt::Display for SemVersion {
    /// C#'s ToString: `M.m.p[-pre][+build]`.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)?;
        if !self.pre.is_empty() {
            write!(f, "-{}", self.pre.join("."))?;
        }
        if !self.build.is_empty() {
            write!(f, "+{}", self.build)?;
        }
        Ok(())
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Op {
    Eq,
    Gt,
    Ge,
    Lt,
    Le,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct Comparator {
    op: Op,
    version: SemVersion,
}

impl Comparator {
    fn new(op: Op, version: SemVersion) -> Comparator {
        Comparator { op, version }
    }

    fn test(&self, v: &SemVersion) -> bool {
        let c = v.compare(&self.version);
        match self.op {
            Op::Eq => c == Ordering::Equal,
            Op::Gt => c == Ordering::Greater,
            Op::Ge => c != Ordering::Less,
            Op::Lt => c == Ordering::Less,
            Op::Le => c != Ordering::Greater,
        }
    }
}

/// A parsed range (`^1.2`, `>=1 <2`, `[1.0,2.0)`, `a || b`, ...). Only `parse` constructs one.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VersionRange {
    pub text: String,
    /// OR of ANDs; one empty set = any.
    sets: Vec<Vec<Comparator>>,
}

/// VersionRange's PartialVersion.
struct Pv {
    major: u64,
    minor: u64,
    patch: u64,
    count: usize,
    pre: Vec<String>,
    wildcard: bool,
}

impl Pv {
    fn floor(&self) -> SemVersion {
        SemVersion::with_pre(self.major, self.minor, self.patch, self.pre.clone())
    }

    /// The first version above every version matching the partial (1.2 -> 1.3.0, 1 -> 2.0.0).
    fn next(&self) -> SemVersion {
        match self.count {
            1 => SemVersion::new(self.major.saturating_add(1), 0, 0),
            2 => SemVersion::new(self.major, self.minor.saturating_add(1), 0),
            _ => SemVersion::new(self.major, self.minor, self.patch.saturating_add(1)),
        }
    }
}

fn partial(text: &str) -> Option<Pv> {
    let p = try_parse_partial(text)?;
    Some(Pv { major: p.parts[0], minor: p.parts[1], patch: p.parts[2], count: p.count, pre: p.pre, wildcard: p.wildcard })
}

/// Index of `i` (a char index) in UTF-16 code units, as C# reports positions.
fn utf16_index(chars: &[char], i: usize) -> usize {
    chars[..i.min(chars.len())].iter().map(|c| c.len_utf16()).sum()
}

impl VersionRange {
    pub fn parse(text: &str) -> Result<VersionRange> {
        VersionRange::try_parse(text).map_err(|e| Error::new(ErrorCode::Parse, e))
    }

    /// VersionRange.TryParse: the range, or C#'s error text.
    pub fn try_parse(text: &str) -> std::result::Result<VersionRange, String> {
        let s = text.trim();
        if s.is_empty() || s == "*" {
            return Ok(VersionRange::any());
        }
        let mut sets: Vec<Vec<Comparator>> = Vec::new();
        for alt in s.split("||") {
            let part = alt.trim();
            if part.is_empty() {
                return Err(format!("version range '{s}': empty alternative"));
            }
            if part.starts_with(['[', '(']) {
                parse_intervals(part, &mut sets)?;
                continue;
            }
            let mut set = Vec::new();
            parse_comparators(part, &mut set).map_err(|e| format!("version range '{s}': {e}"))?;
            sets.push(set);
        }
        Ok(VersionRange { text: s.to_string(), sets })
    }

    /// `*`: any version.
    pub fn any() -> VersionRange {
        VersionRange { text: "*".into(), sets: vec![Vec::new()] }
    }

    /// VersionRange.AtLeastSameMajor (a bare apiVersion): `v` or any later version with the same major.
    pub fn at_least_same_major(v: &SemVersion) -> VersionRange {
        VersionRange {
            text: v.to_string(),
            sets: vec![vec![
                Comparator::new(Op::Ge, v.release()),
                Comparator::new(Op::Lt, SemVersion::new(v.major.saturating_add(1), 0, 0).lowest()),
            ]],
        }
    }

    pub fn satisfies(&self, v: &SemVersion) -> bool {
        self.sets.iter().any(|set| set.iter().all(|c| c.test(v)))
    }

    pub fn is_any(&self) -> bool {
        self.sets.iter().any(|s| s.is_empty())
    }
}

impl fmt::Display for VersionRange {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.text)
    }
}

fn is_op_token(t: &str) -> bool {
    matches!(t, ">=" | "<=" | ">" | "<" | "=" | "^" | "~")
}

fn parse_comparators(part: &str, set: &mut Vec<Comparator>) -> std::result::Result<(), String> {
    let replaced = part.replace(',', " ");
    let mut tokens: Vec<String> = replaced.split(' ').filter(|t| !t.is_empty()).map(str::to_string).collect();
    // join operators written apart from their version (">= 1.0")
    let mut i = 0;
    while !tokens.is_empty() && i < tokens.len() - 1 {
        if is_op_token(&tokens[i]) {
            let next = tokens.remove(i + 1);
            tokens[i].push_str(&next);
        }
        i += 1;
    }
    let mut i = 0;
    while i < tokens.len() {
        if i + 2 < tokens.len() && tokens[i + 1] == "-" {
            // hyphen range
            let (Some(lo), Some(hi)) = (partial(&tokens[i]), partial(&tokens[i + 2])) else {
                return Err(format!("bad hyphen range '{} - {}'", tokens[i], tokens[i + 2]));
            };
            set.push(Comparator::new(Op::Ge, lo.floor()));
            set.push(if hi.count == 3 { Comparator::new(Op::Le, hi.floor()) } else { Comparator::new(Op::Lt, hi.next().lowest()) });
            i += 3;
            continue;
        }
        parse_one(&tokens[i], set)?;
        i += 1;
    }
    Ok(())
}

fn parse_one(token: &str, set: &mut Vec<Comparator>) -> std::result::Result<(), String> {
    let mut op: &str = if token.starts_with(">=") || token.starts_with("<=") {
        &token[..2]
    } else if token.starts_with(['>', '<', '=', '^', '~']) {
        &token[..1]
    } else {
        ""
    };
    let mut rest = &token[op.len()..];
    if op == "~" && rest.starts_with('>') {
        rest = &rest[1..]; // ~> (Ruby style) = ~
    }
    let Some(p) = partial(rest) else {
        return Err(format!("'{token}' is not a version or comparator"));
    };
    if p.count == 0 {
        // * / x
        if op == "<" || op == ">" {
            set.push(Comparator::new(Op::Lt, SemVersion::new(0, 0, 0).lowest())); // matches nothing
        }
        return Ok(());
    }
    if op.is_empty() && p.wildcard {
        op = "="; // 1.2.x / 1.* mean "any 1.2.z" / "any 1.y.z", not caret
    }
    match op {
        "" | "^" => {
            set.push(Comparator::new(Op::Ge, p.floor()));
            let upper = if p.major > 0 || p.count == 1 {
                SemVersion::new(p.major.saturating_add(1), 0, 0)
            } else if p.minor > 0 || p.count == 2 {
                SemVersion::new(0, p.minor.saturating_add(1), 0)
            } else {
                SemVersion::new(0, 0, p.patch.saturating_add(1))
            };
            set.push(Comparator::new(Op::Lt, upper.lowest()));
        }
        "~" => {
            set.push(Comparator::new(Op::Ge, p.floor()));
            let upper = if p.count == 1 {
                SemVersion::new(p.major.saturating_add(1), 0, 0)
            } else {
                SemVersion::new(p.major, p.minor.saturating_add(1), 0)
            };
            set.push(Comparator::new(Op::Lt, upper.lowest()));
        }
        "=" => {
            if p.count == 3 {
                set.push(Comparator::new(Op::Eq, p.floor()));
            } else {
                set.push(Comparator::new(Op::Ge, p.floor()));
                set.push(Comparator::new(Op::Lt, p.next().lowest()));
            }
        }
        ">=" => set.push(Comparator::new(Op::Ge, p.floor())),
        ">" => set.push(if p.count == 3 { Comparator::new(Op::Gt, p.floor()) } else { Comparator::new(Op::Ge, p.next().lowest()) }),
        "<" => {
            let f = p.floor();
            let v = if f.is_prerelease() { f } else { f.lowest() };
            set.push(Comparator::new(Op::Lt, v));
        }
        "<=" => set.push(if p.count == 3 { Comparator::new(Op::Le, p.floor()) } else { Comparator::new(Op::Lt, p.next().lowest()) }),
        _ => {}
    }
    Ok(())
}

fn parse_intervals(text: &str, sets: &mut Vec<Vec<Comparator>>) -> std::result::Result<(), String> {
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        while i < chars.len() && (chars[i] == ',' || chars[i].is_whitespace()) {
            i += 1;
        }
        if i >= chars.len() {
            break;
        }
        if chars[i] != '[' && chars[i] != '(' {
            return Err(format!("version range '{text}': expected '[' or '(' at {}", utf16_index(&chars, i)));
        }
        let Some(close) = (i..chars.len()).find(|&j| chars[j] == ']' || chars[j] == ')') else {
            return Err(format!("version range '{text}': unclosed interval"));
        };
        let lower_inclusive = chars[i] == '[';
        let upper_inclusive = chars[close] == ']';
        let body: String = chars[i + 1..close].iter().collect();
        let mut set = Vec::new();
        match body.find(',') {
            None => {
                let exact = SemVersion::try_parse(&body);
                match exact {
                    Some(v) if lower_inclusive && upper_inclusive => set.push(Comparator::new(Op::Eq, v)),
                    _ => return Err(format!("version range '{text}': '[{body}]' must be an exact version in square brackets")),
                }
            }
            Some(comma) => {
                let lo = body[..comma].trim();
                let hi = body[comma + 1..].trim();
                if !lo.is_empty() {
                    let Some(l) = SemVersion::try_parse(lo) else {
                        return Err(format!("version range '{text}': bad lower bound '{lo}'"));
                    };
                    set.push(Comparator::new(if lower_inclusive { Op::Ge } else { Op::Gt }, l));
                }
                if !hi.is_empty() {
                    let Some(h) = SemVersion::try_parse(hi) else {
                        return Err(format!("version range '{text}': bad upper bound '{hi}'"));
                    };
                    set.push(if upper_inclusive {
                        Comparator::new(Op::Le, h)
                    } else {
                        let v = if h.is_prerelease() { h } else { h.lowest() };
                        Comparator::new(Op::Lt, v)
                    });
                }
            }
        }
        sets.push(set);
        i = close + 1;
    }
    if sets.is_empty() {
        return Err(format!("version range '{text}': no interval"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(s: &str) -> SemVersion {
        SemVersion::parse(s).unwrap()
    }

    #[test]
    fn int32_limits_and_partials() {
        assert_eq!(v("2147483647.0.0").major, 2147483647);
        assert!(SemVersion::try_parse("2147483648").is_none());
        assert!(SemVersion::try_parse("1.x").is_none());
        assert_eq!(v(" v1.2 ").to_string(), "1.2.0");
        assert!(SemVersion::try_parse("1.0.0-01").is_none());
        assert!(SemVersion::try_parse("1..2").is_none());
    }

    #[test]
    fn ranges() {
        let r = VersionRange::parse(">= 1.0, <2").unwrap();
        assert!(r.satisfies(&v("1.5.0")) && !r.satisfies(&v("2.0.0-beta")));
        assert!(VersionRange::parse("").unwrap().is_any());
        assert_eq!(VersionRange::parse(" * ").unwrap().text, "*");
        assert_eq!(VersionRange::try_parse("1 ||").unwrap_err(), "version range '1 ||': empty alternative");
        assert_eq!(VersionRange::try_parse("[1.0").unwrap_err(), "version range '[1.0': unclosed interval");
        assert_eq!(VersionRange::try_parse("abc").unwrap_err(), "version range 'abc': 'abc' is not a version or comparator");
        assert!(VersionRange::parse("[1.0,2.0)").unwrap().satisfies(&v("1.9.9")));
        assert!(!VersionRange::parse("[1.0,2.0)").unwrap().satisfies(&v("2.0.0-rc")));
        assert!(VersionRange::parse("<*").unwrap().sets[0].len() == 1);
    }
}
