//! Version strings as Nexus Mods pages carry them: free text ("1", "v1.2", "0.5.3-beta", "2024.11.02"). Semantic
//! versions compare as semver; anything else compares chunk by chunk (numbers as numbers, words as words, a release
//! before its pre-release words: "1.2" > "1.2-beta").

use std::cmp::Ordering;

fn clean(v: &str) -> &str {
    let v = v.trim();
    let v = v.strip_prefix(['v', 'V']).filter(|r| r.starts_with(|c: char| c.is_ascii_digit())).unwrap_or(v);
    v.split('+').next().unwrap_or(v)
}

#[derive(Debug, PartialEq, Eq)]
enum Chunk {
    Num(String),
    Word(String),
}

fn chunks(v: &str) -> Vec<Chunk> {
    let mut out = Vec::new();
    let mut cur = String::new();
    let mut digits = false;
    let flush = |cur: &mut String, digits: bool, out: &mut Vec<Chunk>| {
        if cur.is_empty() {
            return;
        }
        out.push(if digits { Chunk::Num(cur.trim_start_matches('0').to_string()) } else { Chunk::Word(cur.to_ascii_lowercase()) });
        cur.clear();
    };
    for c in v.chars() {
        if c.is_ascii_alphanumeric() {
            let d = c.is_ascii_digit();
            if !cur.is_empty() && d != digits {
                flush(&mut cur, digits, &mut out);
            }
            digits = d;
            cur.push(c);
        } else {
            flush(&mut cur, digits, &mut out);
        }
    }
    flush(&mut cur, digits, &mut out);
    out
}

fn numeric(a: &str, b: &str) -> Ordering {
    let (a, b) = (a.trim_start_matches('0'), b.trim_start_matches('0'));
    a.len().cmp(&b.len()).then_with(|| a.cmp(b))
}

/// Like Kiln's partial semver parser, but without its C# int32 limits: Nexus versions are free text and numeric
/// prerelease identifiers may be arbitrarily large under semver. Preserve identifier case for semver precedence.
fn semantic(v: &str) -> Option<([&str; 3], Vec<&str>)> {
    let (release, pre) = v.split_once('-').map_or((v, None), |(r, p)| (r, Some(p)));
    let parts: Vec<_> = release.split('.').collect();
    let number = |s: &str| !s.is_empty() && s.bytes().all(|b| b.is_ascii_digit()) && (s.len() == 1 || !s.starts_with('0'));
    if parts.is_empty() || parts.len() > 3 || !parts.iter().all(|s| number(s)) {
        return None;
    }
    let mut numbers = ["0"; 3];
    numbers[..parts.len()].copy_from_slice(&parts);
    let pre: Vec<_> = pre.map(|p| p.split('.').collect()).unwrap_or_default();
    if pre.iter().any(|s: &&str| s.is_empty() || !s.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
        || (s.bytes().all(|b| b.is_ascii_digit()) && !number(s)))
    {
        return None;
    }
    Some((numbers, pre))
}

fn semantic_compare(a: ([&str; 3], Vec<&str>), b: ([&str; 3], Vec<&str>)) -> Ordering {
    for (x, y) in a.0.iter().zip(b.0.iter()) {
        let c = numeric(x, y);
        if c != Ordering::Equal {
            return c;
        }
    }
    if a.1.is_empty() || b.1.is_empty() {
        return b.1.len().cmp(&a.1.len());
    }
    for (x, y) in a.1.iter().zip(b.1.iter()) {
        let c = match (x.bytes().all(|v| v.is_ascii_digit()), y.bytes().all(|v| v.is_ascii_digit())) {
            (true, true) => numeric(x, y),
            (true, false) => Ordering::Less,
            (false, true) => Ordering::Greater,
            (false, false) => x.cmp(y),
        };
        if c != Ordering::Equal {
            return c;
        }
    }
    a.1.len().cmp(&b.1.len())
}

/// Compares two versions as written on Nexus Mods.
pub fn compare(a: &str, b: &str) -> Ordering {
    let (a, b) = (clean(a), clean(b));
    if let (Some(x), Some(y)) = (semantic(a), semantic(b)) {
        return semantic_compare(x, y);
    }
    let (ca, cb) = (chunks(a), chunks(b));
    let n = ca.len().max(cb.len());
    for i in 0..n {
        let o = match (ca.get(i), cb.get(i)) {
            (Some(Chunk::Num(x)), Some(Chunk::Num(y))) => numeric(x, y),
            (Some(Chunk::Word(x)), Some(Chunk::Word(y))) => x.cmp(y),
            // a number beats a word at the same place ("1.2.1" > "1.2.beta")
            (Some(Chunk::Num(_)), Some(Chunk::Word(_))) => Ordering::Greater,
            (Some(Chunk::Word(_)), Some(Chunk::Num(_))) => Ordering::Less,
            // "1.2" vs "1.2.0": equal when the rest is zeros; "1.2" vs "1.2-beta": the release is newer
            (None, Some(Chunk::Num(y))) => numeric("0", y),
            (Some(Chunk::Num(x)), None) => numeric(x, "0"),
            (None, Some(Chunk::Word(_))) => Ordering::Greater,
            (Some(Chunk::Word(_)), None) => Ordering::Less,
            (None, None) => Ordering::Equal,
        };
        if o != Ordering::Equal {
            return o;
        }
    }
    Ordering::Equal
}

/// `latest` is newer than `installed`.
pub fn is_newer(latest: &str, installed: &str) -> bool {
    compare(latest, installed) == Ordering::Greater
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nexus_versions() {
        assert!(is_newer("1.2.1", "1.2.0"));
        assert!(is_newer("v1.10", "v1.9"));
        assert!(is_newer("2", "1.9.9"));
        assert!(is_newer("1.2", "1.2-beta"));
        assert!(is_newer("0.2.0-dev.16", "0.2.0-dev.15"));
        assert!(is_newer("2024.11.02", "2024.9.30"));
        assert_eq!(compare("1.2", "1.2.0"), Ordering::Equal);
        assert_eq!(compare("V1.0", "1.0"), Ordering::Equal);
        assert_eq!(compare("1.0.0+build5", "1.0.0"), Ordering::Equal);
        assert!(!is_newer("1.0", "1.0"));
        assert!(!is_newer("1.0-beta", "1.0"));
        assert!(is_newer("1.2.1", "1.2.beta"));
    }

    #[test]
    fn numeric_components_do_not_overflow_or_change_semver_precedence() {
        assert!(is_newer("1.0.0-10000000000", "1.0.0-9999999999"));
        assert!(is_newer("1.0.0-alpha", "1.0.0-9999999999999999999999999999"));
        assert!(is_newer("18446744073709551617", "18446744073709551616"));
        assert!(is_newer("build18446744073709551617", "build18446744073709551616"));
        assert!(is_newer("1.0.0-alpha.2", "1.0.0-alpha.1"));
        assert!(is_newer("1.0.0-alpha.1", "1.0.0-alpha"));
        assert!(is_newer("1.0.0-a", "1.0.0-A"));
    }
}
