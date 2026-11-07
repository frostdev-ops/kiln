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
    Num(u64),
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
        out.push(if digits { Chunk::Num(cur.parse().unwrap_or(u64::MAX)) } else { Chunk::Word(cur.to_ascii_lowercase()) });
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

/// Compares two versions as written on Nexus Mods.
pub fn compare(a: &str, b: &str) -> Ordering {
    let (a, b) = (clean(a), clean(b));
    if let (Some(x), Some(y)) = (crate::semver::SemVersion::try_parse(a), crate::semver::SemVersion::try_parse(b)) {
        return x.compare(&y);
    }
    let (ca, cb) = (chunks(a), chunks(b));
    let n = ca.len().max(cb.len());
    for i in 0..n {
        let o = match (ca.get(i), cb.get(i)) {
            (Some(Chunk::Num(x)), Some(Chunk::Num(y))) => x.cmp(y),
            (Some(Chunk::Word(x)), Some(Chunk::Word(y))) => x.cmp(y),
            // a number beats a word at the same place ("1.2.1" > "1.2.beta")
            (Some(Chunk::Num(_)), Some(Chunk::Word(_))) => Ordering::Greater,
            (Some(Chunk::Word(_)), Some(Chunk::Num(_))) => Ordering::Less,
            // "1.2" vs "1.2.0": equal when the rest is zeros; "1.2" vs "1.2-beta": the release is newer
            (None, Some(Chunk::Num(y))) => 0.cmp(y),
            (Some(Chunk::Num(x)), None) => x.cmp(&0),
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
}
