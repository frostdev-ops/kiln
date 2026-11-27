// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml: the member paths each package CHANGED in a resource, for the ShroudForge dialect's change journal (format 2,
//! dialect `shroudforge`; 0.2.0-dev.32). Never a value is kept in the record.
//!
//! **How.** After each package's entrypoint, every resource it wrote (the recorder's per-resource rule, `journal.rs`) is
//! compared with what it held before that entrypoint ran (its archive value, or what the last package that changed it left):
//! [`diff`] walks the two values member by member and lists each member whose value differs, at its place in the new value
//! (`maxStackSize`, `recipes[37].craftingDuration`). A list whose length changed is aligned by its unchanged first and last
//! elements: its membership is listed (`recipes[]`) and so is every element between them (an appended recipe is
//! `recipes[2542]`); such a shift moves what earlier packages changed in that list to the elements' new places
//! ([`Paths::changed`]), so every path is a published place. A resource a package wrote back as it was is not listed.
//!
//! **What it can say.** A member written to the value it held is not a change (never named). Two packages changing one
//! member are both named. An element in the middle of a list whose length changed is listed whole (insertions and removals
//! inside a list are seen from its two ends only), so a list edited at both ends and in its middle names more elements than
//! were changed: the record can over-name, never miss. Past [`super::journal::RESOURCE_PATHS`] paths of one package in one
//! resource, that package is named on the whole resource.
//!
//! **Cost.** Per package and resource it wrote: the resource converted and encoded once (as the commit does), compared as
//! bytes with what it held before; only when they differ are the two decoded and walked.
use kfc::resource::value::Value;

/// One step of a member path.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum Seg {
    /// A struct (or a variant's) member.
    Field(Box<str>),
    /// A list element by its 0-based index.
    Index(usize),
    /// A list's membership (an insertion, removal or clear): always the last step.
    Items,
}

/// A list whose length changed between two values, at `at`: its first `prefix` and last `suffix` elements unchanged,
/// `old` elements before, `new` after.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Shift {
    pub at: Vec<Seg>,
    pub prefix: usize,
    pub suffix: usize,
    pub old: usize,
    pub new: usize,
}

/// What changed between two values of one resource.
#[derive(Debug, Default, PartialEq)]
pub struct Diff {
    /// The changed members, at their places in the new value; `[[]]` (one empty path): the whole value.
    pub changed: Vec<Vec<Seg>>,
    pub shifts: Vec<Shift>,
    /// More than the limit's paths: the whole value.
    pub over: bool,
}

/// Two values the same, floats by their bits (a NaN the same as itself; -0.0 not the same as 0.0).
pub fn same(a: &Value, b: &Value) -> bool {
    match (a, b) {
        (Value::Float(x), Value::Float(y)) => x.to_bits() == y.to_bits(),
        (Value::Struct(x), Value::Struct(y)) => x.len() == y.len() && x.iter().all(|(k, v)| y.get(k).is_some_and(|w| same(v, w))),
        (Value::Variant(x), Value::Variant(y)) => {
            x.type_index == y.type_index && x.value.len() == y.value.len() && x.value.iter().all(|(k, v)| y.value.get(k).is_some_and(|w| same(v, w)))
        }
        (Value::Array(x), Value::Array(y)) => x.len() == y.len() && x.iter().zip(y).all(|(v, w)| same(v, w)),
        _ => a == b,
    }
}

/// The members of `after` that differ from `before`, at most `limit` paths (more: `over`).
pub fn diff(before: &Value, after: &Value, limit: usize) -> Diff {
    let mut out = Diff::default();
    let mut path = Vec::new();
    walk(before, after, &mut path, limit, &mut out);
    if out.over {
        out.changed = vec![Vec::new()];
    }
    out
}

fn note(path: &[Seg], limit: usize, out: &mut Diff) {
    if out.changed.len() >= limit {
        out.over = true;
        return;
    }
    out.changed.push(path.to_vec());
}

// A struct's (or a variant's) members, in the new value's order, then the ones only the old value has (kfc's struct map,
// whose type kfc does not name outside it).
macro_rules! members {
    ($a:expr, $b:expr, $path:expr, $limit:expr, $out:expr) => {{
        for (k, w) in $b.iter() {
            $path.push(Seg::Field(k.as_str().into()));
            match $a.get(k) {
                Some(v) => walk(v, w, $path, $limit, $out),
                None => note($path, $limit, $out),
            }
            $path.pop();
            if $out.over {
                return;
            }
        }
        for k in $a.keys().filter(|k| !$b.contains_key(*k)) {
            $path.push(Seg::Field(k.as_str().into()));
            note($path, $limit, $out);
            $path.pop();
        }
    }};
}

fn walk(a: &Value, b: &Value, path: &mut Vec<Seg>, limit: usize, out: &mut Diff) {
    if out.over {
        return;
    }
    match (a, b) {
        (Value::Struct(x), Value::Struct(y)) => members!(x, y, path, limit, out),
        (Value::Variant(x), Value::Variant(y)) if x.type_index == y.type_index => members!(x.value, y.value, path, limit, out),
        (Value::Array(x), Value::Array(y)) if x.len() == y.len() => {
            for (i, (v, w)) in x.iter().zip(y).enumerate() {
                path.push(Seg::Index(i));
                walk(v, w, path, limit, out);
                path.pop();
                if out.over {
                    return;
                }
            }
        }
        (Value::Array(x), Value::Array(y)) => {
            let shorter = x.len().min(y.len());
            let prefix = x.iter().zip(y).take_while(|(v, w)| same(v, w)).count();
            let suffix = x.iter().rev().zip(y.iter().rev()).take(shorter - prefix).take_while(|(v, w)| same(v, w)).count();
            path.push(Seg::Items);
            note(path, limit, out);
            path.pop();
            for i in prefix..y.len() - suffix {
                path.push(Seg::Index(i));
                note(path, limit, out);
                path.pop();
                if out.over {
                    return;
                }
            }
            out.shifts.push(Shift { at: path.clone(), prefix, suffix, old: x.len(), new: y.len() });
        }
        _ => {
            if !same(a, b) {
                note(path, limit, out);
            }
        }
    }
}

/// The changed paths of one resource, per writer (a bucket number of the recorder), kept at their published places.
#[derive(Debug, Default)]
pub struct Paths {
    entries: Vec<(u32, Vec<Seg>)>,
    whole: Vec<u32>,
}

impl Paths {
    /// `writer`'s change: the earlier writers' paths move with this diff's shifts first (an element removed from a list
    /// takes them along; one in the middle of a list whose length changed keeps its index while the list still has it).
    pub fn changed(&mut self, writer: u32, d: Diff) {
        for s in &d.shifts {
            let n = s.at.len();
            self.entries.retain_mut(|(_, p)| {
                if p.len() <= n || p[..n] != s.at[..] {
                    return true;
                }
                let Seg::Index(i) = p[n] else { return true };
                if i < s.prefix {
                    true
                } else if i >= s.old - s.suffix {
                    p[n] = Seg::Index(i + s.new - s.old);
                    true
                } else {
                    i < s.new - s.suffix
                }
            });
        }
        if d.over || d.changed.iter().any(Vec::is_empty) {
            if !self.whole.contains(&writer) {
                self.whole.push(writer);
            }
            return;
        }
        for p in d.changed {
            if !self.entries.iter().any(|(w, q)| *w == writer && *q == p) {
                self.entries.push((writer, p));
            }
        }
    }

    /// `writer` changed this resource in a way no path names (it could not be compared): the whole resource.
    pub fn whole(&mut self, writer: u32) {
        if !self.whole.contains(&writer) {
            self.whole.push(writer);
        }
    }

    /// `writer`'s paths as the record writes them (`[""]`: the whole resource), sorted, each once; empty: it changed nothing.
    pub fn of(&self, writer: u32) -> Vec<String> {
        if self.whole.contains(&writer) {
            return vec![String::new()];
        }
        let mut out: Vec<String> = self.entries.iter().filter(|(w, _)| *w == writer).map(|(_, p)| text(p)).collect();
        out.sort();
        out.dedup();
        out
    }
}

/// A path as the record writes it: `a.b[3].c`, `list[]`, "" for the whole resource.
pub fn text(path: &[Seg]) -> String {
    let mut s = String::new();
    for seg in path {
        match seg {
            Seg::Field(k) => {
                if !s.is_empty() {
                    s.push('.');
                }
                s.push_str(k);
            }
            Seg::Index(i) => s.push_str(&format!("[{i}]")),
            Seg::Items => s.push_str("[]"),
        }
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    fn st(fields: &[(&str, Value)]) -> Value {
        Value::Struct(Box::new(fields.iter().map(|(k, v)| (k.to_string(), v.clone())).collect()))
    }
    fn recipe(duration: f64, count: u64) -> Value {
        st(&[("craftingDuration", Value::Float(duration)), ("input", Value::Array(vec![st(&[("count", Value::UInt(count))])]))])
    }
    fn list(n: usize) -> Value {
        st(&[("recipes", Value::Array((0..n).map(|i| recipe(i as f64, 1)).collect()))])
    }
    fn paths(d: &Diff) -> Vec<String> {
        let mut v: Vec<String> = d.changed.iter().map(|p| text(p)).collect();
        v.sort();
        v
    }

    // A member written back as it was is no change; a changed one is named where it is; a float by its bits.
    #[test]
    fn only_members_that_differ_are_named() {
        let a = st(&[("maxStackSize", Value::UInt(10)), ("weight", Value::Float(1.5)), ("name", Value::String("x".into()))]);
        assert!(diff(&a, &a.clone(), 100).changed.is_empty());
        let b = st(&[("maxStackSize", Value::UInt(100)), ("weight", Value::Float(1.5)), ("name", Value::String("x".into()))]);
        assert_eq!(paths(&diff(&a, &b, 100)), ["maxStackSize"]);
        let n1 = st(&[("f", Value::Float(f64::NAN))]);
        assert!(diff(&n1, &n1.clone(), 100).changed.is_empty(), "a NaN that stays is no change");
        let z = st(&[("f", Value::Float(0.0))]);
        let mz = st(&[("f", Value::Float(-0.0))]);
        assert_eq!(paths(&diff(&z, &mz, 100)), ["f"]);
    }

    // Equal-length lists are walked element by element; a nested member is named by its full path.
    #[test]
    fn a_member_inside_a_list_is_named_by_its_element() {
        let a = list(3);
        let mut b = list(3);
        if let Value::Struct(s) = &mut b
            && let Some(Value::Array(r)) = s.get_mut("recipes")
        {
            r[1] = recipe(1.0, 7);
        }
        assert_eq!(paths(&diff(&a, &b, 100)), ["recipes[1].input[0].count"]);
    }

    // An appended element: the list's membership and the new element; a removed one: the membership only (no element is new);
    // one inserted in front: aligned from the end, so only the front element is listed.
    #[test]
    fn a_list_of_another_length_is_aligned_by_its_ends() {
        let d = diff(&list(3), &list(4), 100);
        assert_eq!(paths(&d), ["recipes[3]", "recipes[]"]);
        assert_eq!(d.shifts, [Shift { at: vec![Seg::Field("recipes".into())], prefix: 3, suffix: 0, old: 3, new: 4 }]);
        assert_eq!(paths(&diff(&list(4), &list(3), 100)), ["recipes[]"]);
        let a = list(3);
        let b = if let Value::Struct(s) = &a {
            let mut s = s.clone();
            if let Some(Value::Array(r)) = s.get_mut("recipes") {
                r.insert(0, recipe(9.0, 9));
            }
            Value::Struct(s)
        } else {
            unreachable!()
        };
        let d = diff(&a, &b, 100);
        assert_eq!(paths(&d), ["recipes[0]", "recipes[]"]);
        assert_eq!(d.shifts[0].prefix, 0);
        assert_eq!(d.shifts[0].suffix, 3);
    }

    // An earlier writer's path moves with a later insertion in front of it, and leaves with a removed element.
    #[test]
    fn earlier_paths_move_to_their_published_places() {
        let mut p = Paths::default();
        let mut one = list(3);
        if let Value::Struct(s) = &mut one
            && let Some(Value::Array(r)) = s.get_mut("recipes")
        {
            r[2] = recipe(5.0, 1);
        }
        p.changed(1, diff(&list(3), &one, 100));
        assert_eq!(p.of(1), ["recipes[2].craftingDuration"]);
        // writer 2 inserts one in front: writer 1's recipe is now the fourth
        let two = if let Value::Struct(s) = &one {
            let mut s = s.clone();
            if let Some(Value::Array(r)) = s.get_mut("recipes") {
                r.insert(0, recipe(9.0, 9));
            }
            Value::Struct(s)
        } else {
            unreachable!()
        };
        p.changed(2, diff(&one, &two, 100));
        assert_eq!(p.of(1), ["recipes[3].craftingDuration"]);
        assert_eq!(p.of(2), ["recipes[0]", "recipes[]"]);
        // writer 3 removes that fourth recipe: writer 1 changed nothing that is still there
        let three = if let Value::Struct(s) = &two {
            let mut s = s.clone();
            if let Some(Value::Array(r)) = s.get_mut("recipes") {
                r.remove(3);
            }
            Value::Struct(s)
        } else {
            unreachable!()
        };
        p.changed(3, diff(&two, &three, 100));
        assert!(p.of(1).is_empty(), "{:?}", p.of(1));
        assert_eq!(p.of(3), ["recipes[]"]);
        assert!(p.of(4).is_empty(), "a writer that changed nothing has no paths");
    }

    // Past the limit, and a value of another kind at the root: the whole resource.
    #[test]
    fn too_many_paths_or_another_kind_is_the_whole_resource() {
        let a = st(&(0..10).map(|i| (Box::leak(format!("f{i}").into_boxed_str()) as &str, Value::UInt(i))).collect::<Vec<_>>());
        let b = st(&(0..10).map(|i| (Box::leak(format!("f{i}").into_boxed_str()) as &str, Value::UInt(i + 1))).collect::<Vec<_>>());
        let d = diff(&a, &b, 4);
        assert!(d.over);
        let mut p = Paths::default();
        p.changed(1, d);
        assert_eq!(p.of(1), [""]);
        let mut q = Paths::default();
        q.changed(2, diff(&Value::UInt(1), &Value::String("x".into()), 100));
        assert_eq!(q.of(2), [""]);
        q.whole(3);
        assert_eq!(q.of(3), [""]);
    }
}
