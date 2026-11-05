//! "Did you mean" suggestions and globs.

/// Optimal string alignment distance (Damerau-Levenshtein with adjacent swaps), case-insensitive on ASCII.
pub fn distance(a: &str, b: &str) -> usize {
    let a: Vec<char> = a.chars().map(|c| c.to_ascii_lowercase()).collect();
    let b: Vec<char> = b.chars().map(|c| c.to_ascii_lowercase()).collect();
    let (n, m) = (a.len(), b.len());
    let mut d = vec![vec![0usize; m + 1]; n + 1];
    for (i, row) in d.iter_mut().enumerate() {
        row[0] = i;
    }
    for j in 0..=m {
        d[0][j] = j;
    }
    for i in 1..=n {
        for j in 1..=m {
            let cost = usize::from(a[i - 1] != b[j - 1]);
            d[i][j] = (d[i - 1][j] + 1).min(d[i][j - 1] + 1).min(d[i - 1][j - 1] + cost);
            if i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1] {
                d[i][j] = d[i][j].min(d[i - 2][j - 2] + 1);
            }
        }
    }
    d[n][m]
}

/// The closest candidate within a distance that grows with the word (1 for short words, about a third of the
/// length for long ones), or a case-insensitive match. None when nothing is close or two are equally close.
pub fn best<'a>(word: &str, candidates: &[&'a str]) -> Option<&'a str> {
    if word.is_empty() {
        return None;
    }
    if let Some(c) = candidates.iter().find(|c| c.eq_ignore_ascii_case(word) && **c != word) {
        return Some(c);
    }
    let limit = (word.chars().count() / 3).clamp(1, 4);
    let mut best: Option<(&str, usize)> = None;
    let mut tie = false;
    for c in candidates {
        if *c == word || c.len().abs_diff(word.len()) > limit {
            continue;
        }
        let d = distance(word, c);
        if d > limit {
            continue;
        }
        match best {
            Some((_, bd)) if d > bd => {}
            Some((_, bd)) if d == bd => tie = true,
            _ => {
                best = Some((c, d));
                tie = false;
            }
        }
    }
    match best {
        Some((c, _)) if !tie => Some(c),
        _ => None,
    }
}

/// Kiln's selector glob: '*' and '?', case-insensitive (native/services/content/packs.cpp glob_ci).
pub fn glob_ci(pattern: &str, s: &str) -> bool {
    let p: Vec<char> = pattern.chars().map(|c| c.to_ascii_lowercase()).collect();
    let s: Vec<char> = s.chars().map(|c| c.to_ascii_lowercase()).collect();
    let (mut pi, mut si) = (0, 0);
    let (mut star, mut mark) = (None, 0);
    while si < s.len() {
        if pi < p.len() && (p[pi] == '?' || p[pi] == s[si]) && p[pi] != '*' {
            pi += 1;
            si += 1;
        } else if pi < p.len() && p[pi] == '*' {
            star = Some(pi);
            pi += 1;
            mark = si;
        } else if let Some(st) = star {
            pi = st + 1;
            mark += 1;
            si = mark;
        } else {
            return false;
        }
    }
    while pi < p.len() && p[pi] == '*' {
        pi += 1;
    }
    pi == p.len()
}

pub fn has_wild(s: &str) -> bool {
    s.contains(['*', '?'])
}

/// A file glob (schema/schemas.json fileMatch): `**` any folders, `*` within one name, `?` one character.
pub fn path_glob(pattern: &str, path: &str) -> bool {
    fn seg(p: &[char], s: &[char]) -> bool {
        match p.split_first() {
            None => s.is_empty(),
            Some(('*', rest)) => (0..=s.len()).any(|i| seg(rest, &s[i..])),
            Some(('?', rest)) => !s.is_empty() && seg(rest, &s[1..]),
            Some((c, rest)) => !s.is_empty() && s[0] == *c && seg(rest, &s[1..]),
        }
    }
    fn parts(p: &[&str], s: &[&str]) -> bool {
        match p.split_first() {
            None => s.is_empty(),
            Some((&"**", rest)) => (0..=s.len()).any(|i| parts(rest, &s[i..])),
            Some((first, rest)) => {
                let pc: Vec<char> = first.chars().collect();
                !s.is_empty() && seg(&pc, &s[0].chars().collect::<Vec<_>>()) && parts(rest, &s[1..])
            }
        }
    }
    let p: Vec<&str> = pattern.split('/').filter(|x| !x.is_empty()).collect();
    let s: Vec<&str> = path.split(['/', '\\']).filter(|x| !x.is_empty()).collect();
    parts(&p, &s)
}

/// fileMatch with '!' exclusions: matches when a positive pattern matches and no exclusion does.
pub fn file_match(patterns: &[String], path: &str) -> bool {
    let mut hit = false;
    for p in patterns {
        if let Some(ex) = p.strip_prefix('!') {
            if path_glob(ex, path) {
                return false;
            }
        } else if path_glob(p, path) {
            hit = true;
        }
    }
    hit
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn suggestions() {
        assert_eq!(best("dependecies", &["dependencies", "description"]), Some("dependencies"));
        assert_eq!(best("entryassembly", &["entryAssembly", "entryType"]), Some("entryAssembly"));
        assert_eq!(best("sid", &["side", "id"]), None); // a tie
        assert_eq!(best("xyz", &["side", "name"]), None);
        assert_eq!(best("maxStackSize", &["maxStackSize"]), None);
        assert_eq!(best("Materail_T1_Wood_Logs", &["Material_T1_Wood_Logs", "Material_T1_Twigs"]), Some("Material_T1_Wood_Logs"));
    }

    #[test]
    fn globs() {
        assert!(glob_ci("Material_*", "material_t1_wood"));
        assert!(glob_ci("*", "x"));
        assert!(glob_ci("Enemy_Vukah_*", "Enemy_Vukah_Melee"));
        assert!(!glob_ci("Enemy_?", "Enemy_ab"));
        assert!(path_glob("**/content/*.json", "content/x.json"));
        assert!(path_glob("**/content/*.json", "mods/a/content/x.json"));
        assert!(!path_glob("**/content/*.json", "mods/a/content/sub/x.json"));
        assert!(path_glob("**/*.kschem", "a/b/hut.kschem"));
        let m: Vec<String> = ["**/native.json", "!**/config/native.json"].iter().map(|s| s.to_string()).collect();
        assert!(file_match(&m, "mods/x/native.json"));
        assert!(!file_match(&m, "kiln/config/native.json"));
    }
}
