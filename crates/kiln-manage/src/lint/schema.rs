//! JSON Schema (draft 2020-12) validation over [`json::Node`] trees, with the `x-kiln-*` annotations of Kiln's
//! schemas (docs/kiln/modding/lint.md, "Schema annotations"). It implements the keywords Kiln's schemas use:
//! $ref, type, enum, const, properties, patternProperties, additionalProperties, propertyNames, required,
//! min/maxProperties, dependentRequired, items, prefixItems, contains, min/maxItems, uniqueItems, min/maxLength,
//! pattern, minimum, maximum, exclusiveMinimum, exclusiveMaximum, multipleOf, allOf, anyOf, oneOf, not, if/then/else.
//! Integers are numbers written without a fraction or exponent (Kiln's readers refuse `1.0` where they want an
//! integer).

use super::json::{Member, Node, Value, ptr_seg};
use super::regex::Regex;
use super::{Fix, Severity, Span, TextEdit, codes, suggest};
use serde_json::Value as J;
use std::sync::Mutex;
use std::collections::{HashMap, HashSet};

/// One problem found by the schema.
#[derive(Debug, Clone)]
pub struct Issue {
    pub code: &'static str,
    pub severity: Severity,
    pub pointer: String,
    pub span: Span,
    pub message: String,
    pub fixes: Vec<Fix>,
    pub docs: Option<String>,
    pub depth: usize,
}

/// What a successfully applied schema says about a value (x-kiln-* annotations).
#[derive(Debug, Clone, PartialEq)]
pub enum Annot {
    /// The string names a game entry (or a mod's definition) of `section`.
    Ref { section: String, forms: Vec<String>, missing: Severity, value: String, span: Span, pointer: String },
    /// The string is a GUID this file defines in namespace `ns`.
    Def { ns: String, value: String, span: Span, pointer: String },
    /// The string is a name this file defines in namespace `ns`.
    DefName { ns: String, value: String, span: Span, pointer: String },
    /// The string is a path that must exist (`base`: mod, assets, self).
    File { base: String, kind: Option<String>, value: String, span: Span, pointer: String },
    /// An object key that is a field path of `section` (the game's reflection).
    FieldKey { section: String, key: String, span: Span, pointer: String, value: Option<Node> },
}

#[derive(Default, Debug)]
pub struct Out {
    pub issues: Vec<Issue>,
    pub annots: Vec<Annot>,
}

impl Out {
    fn ok(&self) -> bool {
        !self.issues.iter().any(|i| i.severity >= Severity::Warning && i.code != codes::UNKNOWN_KEY)
    }
    fn merge(&mut self, o: Out) {
        self.issues.extend(o.issues);
        self.annots.extend(o.annots);
    }
}

/// What the schemas applied at one object location know about its keys (for unknown-key warnings).
#[derive(Default, Debug)]
struct Known {
    props: HashSet<String>,
    patterns: Vec<String>,
    open: bool,
    policy: Option<(Severity, Option<String>)>,
    fields: Option<String>,
}

impl Known {
    fn merge(&mut self, o: Known) {
        self.props.extend(o.props);
        self.patterns.extend(o.patterns);
        self.open |= o.open;
        if self.policy.is_none() {
            self.policy = o.policy;
        }
        if self.fields.is_none() {
            self.fields = o.fields;
        }
    }
}

/// The schema documents, by `$id` (file name).
pub struct Schemas {
    docs: HashMap<String, J>,
    regex: Mutex<HashMap<String, Option<Regex>>>,
}

impl Schemas {
    pub fn new(docs: impl IntoIterator<Item = (String, J)>) -> Schemas {
        Schemas { docs: docs.into_iter().collect(), regex: Mutex::new(HashMap::new()) }
    }
    pub fn doc(&self, id: &str) -> Option<&J> {
        self.docs.get(id)
    }
    pub fn ids(&self) -> impl Iterator<Item = &String> {
        self.docs.keys()
    }

    fn regex_match(&self, pattern: &str, s: &str) -> Result<bool, String> {
        let mut cache = self.regex.lock().unwrap_or_else(|e| e.into_inner());
        let re = cache.entry(pattern.to_string()).or_insert_with(|| Regex::new(pattern).ok());
        match re {
            Some(r) => Ok(r.is_match(s)),
            None => Err(format!("the schema's pattern {pattern} is not supported by kiln lint")),
        }
    }

    /// Resolves a `$ref` from document `doc`: "#/$defs/x", "other.schema.json#/$defs/y", "other.schema.json".
    fn resolve<'s>(&'s self, doc: &str, r: &str) -> Option<(String, &'s J)> {
        let (file, frag) = match r.split_once('#') {
            Some((f, p)) => (f, p),
            None => (r, ""),
        };
        let id = if file.is_empty() { doc.to_string() } else { file.rsplit('/').next().unwrap_or(file).to_string() };
        let mut v = self.docs.get(&id)?;
        for seg in frag.split('/').filter(|s| !s.is_empty()) {
            let seg = seg.replace("~1", "/").replace("~0", "~");
            v = match v {
                J::Object(o) => o.get(&seg)?,
                J::Array(a) => a.get(seg.parse::<usize>().ok()?)?,
                _ => return None,
            };
        }
        Some((id, v))
    }

    /// Validates `node` against the schema document `id`.
    pub fn validate(&self, id: &str, node: &Node) -> Out {
        let Some(schema) = self.docs.get(id) else {
            let mut o = Out::default();
            o.issues.push(Issue {
                code: codes::SCHEMA_RULE,
                severity: Severity::Error,
                pointer: String::new(),
                span: node.span,
                message: format!("no schema {id} (kiln lint is out of date?)"),
                fixes: vec![],
                docs: None,
                depth: 0,
            });
            return o;
        };
        let v = V { s: self };
        let cx = Cx { doc: id.to_string(), ptr: String::new(), label: first_char(node.span), cap: None, docs: None, depth: 0 };
        v.inst(node, schema, &cx)
    }
}

fn first_char(s: Span) -> Span {
    Span::new(s.start, (s.start + 1).min(s.end))
}

#[derive(Clone)]
struct Cx {
    doc: String,
    ptr: String,
    /// Where object-level problems (a missing key) are underlined: the key of this value, or its first character.
    label: Span,
    cap: Option<Severity>,
    docs: Option<String>,
    depth: usize,
}

impl Cx {
    fn child(&self, seg: &str, label: Span) -> Cx {
        Cx { ptr: format!("{}/{}", self.ptr, ptr_seg(seg)), label, depth: self.depth + 1, ..self.clone() }
    }
}

struct V<'s> {
    s: &'s Schemas,
}

fn kind_name(n: &Node) -> &'static str {
    match n.kind() {
        "integer" => "an integer",
        "number" => "a number",
        "string" => "a string",
        "boolean" => "a boolean",
        "null" => "null",
        "array" => "a list",
        _ => "an object",
    }
}

fn type_matches(n: &Node, t: &str) -> bool {
    match t {
        "integer" => n.kind() == "integer",
        "number" => matches!(n.kind(), "integer" | "number"),
        k => n.kind() == k,
    }
}

fn type_label(t: &str) -> &str {
    match t {
        "integer" => "an integer",
        "number" => "a number",
        "string" => "a string",
        "boolean" => "true or false",
        "null" => "null",
        "array" => "a list",
        "object" => "an object",
        _ => t,
    }
}

/// JSON equality between a value of the document and a schema constant (numbers by value).
fn json_eq(n: &Node, j: &J) -> bool {
    match (&n.value, j) {
        (Value::Null, J::Null) => true,
        (Value::Bool(a), J::Bool(b)) => a == b,
        (Value::Number(a, _), J::Number(b)) => b.as_f64().is_some_and(|b| *a == b),
        (Value::String(a), J::String(b)) => a == b,
        (Value::Array(a), J::Array(b)) => a.len() == b.len() && a.iter().zip(b).all(|(x, y)| json_eq(x, y)),
        (Value::Object(a), J::Object(b)) => {
            let last: HashMap<&str, &Node> = a.iter().map(|m| (m.key.as_str(), &m.value)).collect();
            last.len() == b.len() && b.iter().all(|(k, v)| last.get(k.as_str()).is_some_and(|x| json_eq(x, v)))
        }
        _ => false,
    }
}

fn show(j: &J) -> String {
    match j {
        J::String(s) => format!("\"{s}\""),
        other => other.to_string(),
    }
}

fn sev_of(s: &str) -> Option<Severity> {
    match s {
        "error" => Some(Severity::Error),
        "warning" => Some(Severity::Warning),
        "info" => Some(Severity::Info),
        "hint" => Some(Severity::Hint),
        _ => None,
    }
}

fn str_list(j: Option<&J>) -> Vec<String> {
    j.and_then(J::as_array).map(|a| a.iter().filter_map(|x| x.as_str().map(str::to_string)).collect()).unwrap_or_default()
}

impl V<'_> {
    fn issue(&self, cx: &Cx, code: &'static str, span: Span, message: String) -> Issue {
        let base = codes::rule(code).map(|r| r.severity).unwrap_or(Severity::Error);
        let severity = match cx.cap {
            Some(c) if c < base => c,
            _ => base,
        };
        Issue { code, severity, pointer: cx.ptr.clone(), span, message, fixes: vec![], docs: cx.docs.clone(), depth: cx.depth }
    }

    /// Validates one instance location: the schema and its in-place applicators, then the keys nobody knows.
    fn inst(&self, node: &Node, schema: &J, cx: &Cx) -> Out {
        let mut known = Known::default();
        let mut out = self.apply(node, schema, cx, &mut known);
        if let Some(members) = node.as_object() {
            self.unknown_keys(members, &known, cx, &mut out);
        }
        out
    }

    fn is_known(&self, key: &str, k: &Known) -> bool {
        k.props.contains(key) || k.patterns.iter().any(|p| self.s.regex_match(p, key).unwrap_or(false))
    }

    fn unknown_keys(&self, members: &[Member], k: &Known, cx: &Cx, out: &mut Out) {
        if let Some(section) = &k.fields {
            for m in members {
                if !self.is_known(&m.key, k) && !m.key.starts_with('@') {
                    out.annots.push(Annot::FieldKey { section: section.clone(), key: m.key.clone(), span: m.key_span, pointer: format!("{}/{}", cx.ptr, ptr_seg(&m.key)), value: Some(m.value.clone()) });
                }
            }
        }
        if k.open {
            return;
        }
        let Some((sev, msg)) = &k.policy else { return };
        let mut candidates: Vec<&str> = k.props.iter().map(String::as_str).collect();
        candidates.sort();
        for m in members {
            if self.is_known(&m.key, k) {
                continue;
            }
            let mut c = cx.child(&m.key, m.key_span);
            c.cap = Some(*sev);
            let what = msg.clone().unwrap_or_else(|| "unknown key (Kiln ignores it)".to_string());
            let mut i = self.issue(&c, codes::UNKNOWN_KEY, m.key_span, format!("\"{}\": {what}", m.key));
            i.severity = *sev;
            if let Some(s) = suggest::best(&m.key, &candidates) {
                i.message.push_str(&format!("; did you mean \"{s}\"?"));
                i.fixes.push(Fix::new(&format!("Rename to \"{s}\""), vec![TextEdit::new("", m.key_span, &format!("\"{s}\""))]));
            }
            out.issues.push(i);
        }
    }

    fn apply(&self, node: &Node, schema: &J, cx0: &Cx, known: &mut Known) -> Out {
        let mut out = Out::default();
        let o = match schema {
            J::Bool(true) => return out,
            J::Bool(false) => {
                out.issues.push(self.issue(cx0, codes::SCHEMA_RULE, node.span, "not allowed here".into()));
                return out;
            }
            J::Object(o) => o,
            _ => return out,
        };
        let mut cx = cx0.clone();
        if let Some(s) = o.get("x-kiln-severity").and_then(J::as_str).and_then(sev_of) {
            cx.cap = Some(cx.cap.map_or(s, |c| c.min(s)));
        }
        if let Some(d) = o.get("x-kiln-docs").and_then(J::as_str) {
            cx.docs = Some(d.to_string());
        }
        let cx = &cx;

        // annotations
        if let Some(sv) = node.as_str() {
            let ptr = cx.ptr.clone();
            if let Some(sec) = o.get("x-kiln-ref").and_then(J::as_str) {
                let missing = o.get("x-kiln-ref-missing").and_then(J::as_str).and_then(sev_of).unwrap_or(Severity::Warning);
                out.annots.push(Annot::Ref { section: sec.into(), forms: str_list(o.get("x-kiln-ref-forms")), missing, value: sv.into(), span: node.span, pointer: ptr.clone() });
            }
            if let Some(ns) = o.get("x-kiln-def").and_then(J::as_str) {
                out.annots.push(Annot::Def { ns: ns.into(), value: sv.into(), span: node.span, pointer: ptr.clone() });
            }
            if let Some(ns) = o.get("x-kiln-def-name").and_then(J::as_str) {
                out.annots.push(Annot::DefName { ns: ns.into(), value: sv.into(), span: node.span, pointer: ptr.clone() });
            }
            if let Some(base) = o.get("x-kiln-file").and_then(J::as_str) {
                let kind = o.get("x-kiln-file-kind").and_then(J::as_str).map(str::to_string);
                out.annots.push(Annot::File { base: base.into(), kind, value: sv.into(), span: node.span, pointer: ptr });
            }
        }
        if let Some(sec) = o.get("x-kiln-fields").and_then(J::as_str) {
            known.fields = Some(sec.to_string());
        }
        if let Some(p) = o.get("x-kiln-unknownKeys").and_then(J::as_str).and_then(sev_of) {
            known.policy = Some((p, o.get("x-kiln-unknownKeysMessage").and_then(J::as_str).map(str::to_string)));
        }

        if let Some(r) = o.get("$ref").and_then(J::as_str) {
            match self.s.resolve(&cx.doc, r) {
                Some((doc, target)) => {
                    let mut c = cx.clone();
                    c.doc = doc;
                    out.merge(self.apply(node, target, &c, known));
                }
                None => out.issues.push(self.issue(cx, codes::SCHEMA_RULE, node.span, format!("the schema's $ref {r} does not resolve"))),
            }
        }

        // type
        if let Some(t) = o.get("type") {
            let types: Vec<&str> = match t {
                J::String(s) => vec![s.as_str()],
                J::Array(a) => a.iter().filter_map(J::as_str).collect(),
                _ => vec![],
            };
            if !types.is_empty() && !types.iter().any(|t| type_matches(node, t)) {
                let want: Vec<&str> = types.iter().map(|t| type_label(t)).collect();
                let mut msg = format!("expected {}, found {}", join_or(&want), kind_name(node));
                if node.kind() == "number" && types.contains(&"integer") {
                    msg = format!("expected an integer, found {} (write it without a fraction)", node_text(node));
                }
                out.issues.push(self.issue(cx, codes::TYPE, node.span, msg));
            }
        }

        // enum, const
        if let Some(J::Array(vals)) = o.get("enum")
            && !vals.iter().any(|v| json_eq(node, v))
        {
            out.issues.push(self.enum_issue(node, vals, o, cx));
        }
        if let Some(c) = o.get("const")
            && !json_eq(node, c)
        {
            out.issues.push(self.enum_issue(node, std::slice::from_ref(c), o, cx));
        }

        match &node.value {
            Value::Object(members) => self.object(node, members, o, cx, known, &mut out),
            Value::Array(items) => self.array(node, items, o, cx, &mut out),
            Value::String(s) => self.string(node, s, o, cx, &mut out),
            Value::Number(n, _) => self.number(node, *n, o, cx, &mut out),
            _ => {}
        }

        // combinators
        if let Some(J::Array(all)) = o.get("allOf") {
            for sub in all {
                out.merge(self.apply(node, sub, cx, known));
            }
        }
        if let Some(J::Array(any)) = o.get("anyOf") {
            self.alternatives(node, any, cx, known, &mut out, false);
        }
        if let Some(J::Array(one)) = o.get("oneOf") {
            self.alternatives(node, one, cx, known, &mut out, true);
        }
        if let Some(not) = o.get("not") {
            let mut k = Known::default();
            let r = self.apply(node, not, cx, &mut k);
            if r.ok() {
                let msg = o.get("x-kiln-message").and_then(J::as_str).map(str::to_string).unwrap_or_else(|| "this value is not allowed here".into());
                let span = if cx.label.start != node.span.start && node.as_object().is_none() { node.span } else { cx.label.union(node.span) };
                out.issues.push(self.issue(cx, codes::SCHEMA_RULE, span, msg));
            }
        }
        if let Some(cond) = o.get("if") {
            let mut k = Known::default();
            let r = self.apply(node, cond, cx, &mut k);
            let branch = if r.ok() { o.get("then") } else { o.get("else") };
            if let Some(b) = branch {
                out.merge(self.apply(node, b, cx, known));
            }
        }

        // x-kiln-message: one clear message instead of the raw failures at this location
        if let Some(msg) = o.get("x-kiln-message").and_then(J::as_str)
            && o.get("not").is_none()
        {
            let here: Vec<usize> = out.issues.iter().enumerate().filter(|(_, i)| i.depth == cx.depth && i.code != codes::UNKNOWN_KEY).map(|(n, _)| n).collect();
            if !here.is_empty() {
                let first = out.issues[here[0]].clone();
                let same = here.iter().all(|&n| out.issues[n].code == first.code);
                let severity = here.iter().map(|&n| out.issues[n].severity).max().unwrap_or(first.severity);
                let mut keep: Vec<Issue> = out.issues.iter().enumerate().filter(|(n, _)| !here.contains(n)).map(|(_, i)| i.clone()).collect();
                let fixes = if same { first.fixes.clone() } else { vec![] };
                keep.push(Issue {
                    code: if same { first.code } else { codes::SCHEMA_RULE },
                    severity,
                    message: if node.as_str().is_some() && !msg.contains('"') { format!("{}: {msg}", node_text(node)) } else { msg.to_string() },
                    fixes,
                    ..first
                });
                out.issues = keep;
            }
        }
        out
    }

    fn enum_issue(&self, node: &Node, vals: &[J], o: &serde_json::Map<String, J>, cx: &Cx) -> Issue {
        let strings: Vec<&str> = vals.iter().filter_map(J::as_str).collect();
        let is_format = cx.ptr.ends_with("/format") || cx.ptr == "/version";
        if let Some(s) = node.as_str() {
            if o.get("x-kiln-caseInsensitive").and_then(J::as_bool) == Some(true)
                && let Some(exact) = strings.iter().find(|v| v.eq_ignore_ascii_case(s))
            {
                let mut i = self.issue(cx, codes::CASE, node.span, format!("\"{s}\" matches \"{exact}\" (Kiln ignores case); write it as the game names it"));
                i.severity = Severity::Hint;
                i.fixes.push(Fix::new(&format!("Write \"{exact}\""), vec![TextEdit::new("", node.span, &format!("\"{exact}\""))]));
                return i;
            }
            let code = if is_format { codes::FORMAT_VERSION } else { codes::ENUM };
            let mut i = self.issue(cx, code, node.span, if vals.len() == 1 { format!("expected {}, found \"{s}\"", show(&vals[0])) } else { format!("\"{s}\" is not one of {}", list_vals(vals)) });
            if let Some(best) = suggest::best(s, &strings) {
                i.message.push_str(&format!("; did you mean \"{best}\"?"));
                i.fixes.push(Fix::new(&format!("Replace with \"{best}\""), vec![TextEdit::new("", node.span, &format!("\"{best}\""))]));
            }
            return i;
        }
        let code = if is_format { codes::FORMAT_VERSION } else { codes::ENUM };
        let msg = if is_format && vals.len() == 1 {
            format!("format {} is not supported (this Kiln reads {})", node_text(node), show(&vals[0]))
        } else if vals.len() == 1 {
            format!("expected {}, found {}", show(&vals[0]), node_text(node))
        } else {
            format!("{} is not one of {}", node_text(node), list_vals(vals))
        };
        self.issue(cx, code, node.span, msg)
    }

    fn object(&self, node: &Node, members: &[Member], o: &serde_json::Map<String, J>, cx: &Cx, known: &mut Known, out: &mut Out) {
        let props = o.get("properties").and_then(J::as_object);
        let pats = o.get("patternProperties").and_then(J::as_object);
        if let Some(p) = props {
            known.props.extend(p.keys().cloned());
        }
        if let Some(p) = pats {
            known.patterns.extend(p.keys().cloned());
        }
        let additional = o.get("additionalProperties");
        if matches!(additional, Some(J::Object(_))) {
            known.open = true;
        }
        for m in members {
            let c = cx.child(&m.key, m.key_span);
            let mut matched = false;
            if let Some(sub) = props.and_then(|p| p.get(&m.key)) {
                matched = true;
                out.merge(self.inst(&m.value, sub, &c));
            }
            if let Some(p) = pats {
                for (pat, sub) in p {
                    if self.s.regex_match(pat, &m.key).unwrap_or(false) {
                        matched = true;
                        out.merge(self.inst(&m.value, sub, &c));
                    }
                }
            }
            if !matched {
                match additional {
                    Some(J::Bool(false)) => {
                        let mut cands: Vec<&str> = props.map(|p| p.keys().map(String::as_str).collect()).unwrap_or_default();
                        cands.sort();
                        let mut i = self.issue(&c, codes::KEY_NOT_ALLOWED, m.key_span, format!("\"{}\" is not allowed here", m.key));
                        if !cands.is_empty() && cands.len() <= 12 {
                            i.message.push_str(&format!(" (allowed: {})", cands.join(", ")));
                        }
                        if let Some(s) = suggest::best(&m.key, &cands) {
                            i.message = format!("\"{}\" is not allowed here; did you mean \"{s}\"?", m.key);
                            i.fixes.push(Fix::new(&format!("Rename to \"{s}\""), vec![TextEdit::new("", m.key_span, &format!("\"{s}\""))]));
                        }
                        out.issues.push(i);
                    }
                    Some(sub @ J::Object(_)) => out.merge(self.inst(&m.value, sub, &c)),
                    _ => {}
                }
            }
            if let Some(pn) = o.get("propertyNames") {
                let key_node = Node { value: Value::String(m.key.clone()), span: m.key_span };
                let mut kc = c.clone();
                kc.label = m.key_span;
                let r = self.inst(&key_node, pn, &kc);
                for mut i in r.issues {
                    if !i.message.starts_with("key ") {
                        i.message = format!("key {}", i.message);
                    }
                    out.issues.push(i);
                }
                out.annots.extend(r.annots);
            }
        }
        let present: HashSet<&str> = members.iter().map(|m| m.key.as_str()).collect();
        if let Some(J::Array(req)) = o.get("required") {
            for r in req.iter().filter_map(J::as_str) {
                if !present.contains(r) {
                    out.issues.push(self.issue(cx, codes::REQUIRED, cx.label, format!("missing \"{r}\"")));
                }
            }
        }
        if let Some(J::Object(dep)) = o.get("dependentRequired") {
            for (k, need) in dep {
                if present.contains(k.as_str()) {
                    for r in need.as_array().into_iter().flatten().filter_map(J::as_str) {
                        if !present.contains(r) {
                            out.issues.push(self.issue(cx, codes::REQUIRED, cx.label, format!("\"{k}\" needs \"{r}\" too")));
                        }
                    }
                }
            }
        }
        let n = present.len();
        if let Some(min) = o.get("minProperties").and_then(J::as_u64)
            && (n as u64) < min
        {
            out.issues.push(self.issue(cx, codes::SIZE, node.span, if min == 1 { "an empty object: give at least one key".into() } else { format!("at least {min} keys") }));
        }
        if let Some(max) = o.get("maxProperties").and_then(J::as_u64)
            && (n as u64) > max
        {
            out.issues.push(self.issue(cx, codes::SIZE, node.span, format!("at most {max} keys")));
        }
    }

    fn array(&self, node: &Node, items: &[Node], o: &serde_json::Map<String, J>, cx: &Cx, out: &mut Out) {
        let prefix = o.get("prefixItems").and_then(J::as_array);
        let np = prefix.map_or(0, Vec::len);
        for (i, it) in items.iter().enumerate() {
            let c = cx.child(&i.to_string(), first_char(it.span));
            if let Some(sub) = prefix.and_then(|p| p.get(i)) {
                out.merge(self.inst(it, sub, &c));
            } else if let Some(sub) = o.get("items")
                && i >= np
            {
                if sub == &J::Bool(false) {
                    out.issues.push(self.issue(&c, codes::SIZE, it.span, format!("at most {np} entries")));
                } else {
                    out.merge(self.inst(it, sub, &c));
                }
            }
        }
        if let Some(min) = o.get("minItems").and_then(J::as_u64)
            && (items.len() as u64) < min
        {
            out.issues.push(self.issue(cx, codes::SIZE, node.span, if min == 1 { "an empty list: give at least one entry".into() } else { format!("{} entries, at least {min} needed", items.len()) }));
        }
        if let Some(max) = o.get("maxItems").and_then(J::as_u64)
            && (items.len() as u64) > max
        {
            out.issues.push(self.issue(cx, codes::SIZE, node.span, format!("{} entries, at most {max} allowed", items.len())));
        }
        if let Some(c) = o.get("contains") {
            let any = items.iter().enumerate().any(|(i, it)| {
                let mut k = Known::default();
                self.apply(it, c, &cx.child(&i.to_string(), it.span), &mut k).ok()
            });
            if !any {
                out.issues.push(self.issue(cx, codes::SCHEMA_RULE, node.span, "no entry of the required kind".into()));
            }
        }
        if o.get("uniqueItems").and_then(J::as_bool) == Some(true) {
            let vals: Vec<serde_json::Value> = items.iter().map(Node::to_json).collect();
            for (i, v) in vals.iter().enumerate() {
                if let Some(j) = vals[..i].iter().position(|w| w == v) {
                    let mut is = self.issue(&cx.child(&i.to_string(), items[i].span), codes::UNIQUE, items[i].span, format!("the same entry as #{j}"));
                    is.fixes.push(Fix::new("Remove the repeated entry", vec![TextEdit::new("", items[i].span, "")]));
                    out.issues.push(is);
                }
            }
        }
    }

    fn string(&self, node: &Node, s: &str, o: &serde_json::Map<String, J>, cx: &Cx, out: &mut Out) {
        let len = s.chars().count() as u64;
        if let Some(min) = o.get("minLength").and_then(J::as_u64)
            && len < min
        {
            out.issues.push(self.issue(cx, codes::SIZE, node.span, if min == 1 { "an empty string".into() } else { format!("at least {min} characters") }));
        }
        if let Some(max) = o.get("maxLength").and_then(J::as_u64)
            && len > max
        {
            out.issues.push(self.issue(cx, codes::SIZE, node.span, format!("{len} characters, at most {max}")));
        }
        if let Some(p) = o.get("pattern").and_then(J::as_str) {
            match self.s.regex_match(p, s) {
                Ok(true) => {}
                Ok(false) => out.issues.push(self.issue(cx, codes::PATTERN, node.span, format!("\"{s}\" does not have the expected form ({p})"))),
                Err(e) => out.issues.push(self.issue(cx, codes::SCHEMA_RULE, node.span, e)),
            }
        }
        if let Some(f) = o.get("format").and_then(J::as_str)
            && f == "date-time"
            && !is_rfc3339(s)
        {
            out.issues.push(self.issue(cx, codes::PATTERN, node.span, format!("\"{s}\" is not an RFC 3339 time (2026-11-04T12:00:00Z)")));
        }
    }

    fn number(&self, node: &Node, n: f64, o: &serde_json::Map<String, J>, cx: &Cx, out: &mut Out) {
        let f = |k: &str| o.get(k).and_then(J::as_f64);
        let t = node_text(node);
        if let Some(m) = f("minimum")
            && n < m
        {
            out.issues.push(self.issue(cx, codes::RANGE, node.span, format!("{t} is below the minimum {}", num(m))));
        }
        if let Some(m) = f("maximum")
            && n > m
        {
            out.issues.push(self.issue(cx, codes::RANGE, node.span, format!("{t} is above the maximum {}", num(m))));
        }
        if let Some(m) = f("exclusiveMinimum")
            && n <= m
        {
            out.issues.push(self.issue(cx, codes::RANGE, node.span, format!("{t} must be greater than {}", num(m))));
        }
        if let Some(m) = f("exclusiveMaximum")
            && n >= m
        {
            out.issues.push(self.issue(cx, codes::RANGE, node.span, format!("{t} must be less than {}", num(m))));
        }
        if let Some(m) = f("multipleOf")
            && m > 0.0
            && ((n / m).round() * m - n).abs() > 1e-9 * n.abs().max(1.0)
        {
            out.issues.push(self.issue(cx, codes::RANGE, node.span, format!("{t} is not a multiple of {}", num(m))));
        }
    }

    fn alternatives(&self, node: &Node, alts: &[J], cx: &Cx, known: &mut Known, out: &mut Out, one: bool) {
        let mut results: Vec<(Out, Known)> = Vec::new();
        let mut passing: Vec<usize> = Vec::new();
        for (i, a) in alts.iter().enumerate() {
            let mut k = Known::default();
            let r = self.apply(node, a, cx, &mut k);
            if r.ok() {
                passing.push(i);
            }
            results.push((r, k));
        }
        if !passing.is_empty() && !(one && passing.len() > 1) {
            let (r, k) = results.swap_remove(passing[0]);
            known.merge(k);
            out.merge(r);
            return;
        }
        if one && passing.len() > 1 {
            out.issues.push(self.issue(cx, codes::SCHEMA_RULE, node.span, format!("matches {} alternatives where exactly one is expected", passing.len())));
            let (r, k) = results.swap_remove(passing[0]);
            known.merge(k);
            out.annots.extend(r.annots);
            return;
        }
        // none passes: report the alternative that got furthest
        let depth = cx.depth;
        let score = |r: &Out| {
            let errs: Vec<&Issue> = r.issues.iter().filter(|i| i.severity >= Severity::Warning).collect();
            let root_type = errs.iter().any(|i| i.depth == depth && i.code == codes::TYPE) as u8;
            let disc = errs.iter().any(|i| i.depth == depth + 1 && matches!(i.code, codes::ENUM | codes::FORMAT_VERSION)) as u8;
            let deepest = errs.iter().map(|i| i.depth).max().unwrap_or(0);
            (root_type, disc, usize::MAX - deepest, errs.len())
        };
        let all_type = results.iter().all(|(r, _)| {
            let errs: Vec<&Issue> = r.issues.iter().filter(|i| i.severity >= Severity::Warning).collect();
            !errs.is_empty() && errs.iter().all(|i| i.depth == depth && i.code == codes::TYPE)
        });
        for (_, k) in &results {
            known.props.extend(k.props.iter().cloned());
            known.patterns.extend(k.patterns.iter().cloned());
            known.open |= k.open;
        }
        if all_type && results.len() > 1 {
            let mut want: Vec<String> = Vec::new();
            for a in alts {
                for t in schema_types(a, self.s, &cx.doc) {
                    if !want.contains(&t) {
                        want.push(t);
                    }
                }
            }
            let labels: Vec<&str> = want.iter().map(|t| type_label(t)).collect();
            let msg = if labels.is_empty() { format!("{} is not allowed here", kind_name(node)) } else { format!("expected {}, found {}", join_or(&labels), kind_name(node)) };
            out.issues.push(self.issue(cx, codes::TYPE, node.span, msg));
            return;
        }
        // every alternative is ruled out by a discriminator (a const / enum of a direct key, like "tool" or "kind"):
        // the failure of that key says it all; the alternatives' other complaints would only be noise
        let disc_all = results.len() > 1
            && results.iter().all(|(r, _)| r.issues.iter().any(|i| i.severity >= Severity::Warning && i.depth == depth + 1 && matches!(i.code, codes::ENUM | codes::FORMAT_VERSION)));
        if disc_all {
            out.issues.push(self.issue(cx, codes::SCHEMA_RULE, cx.label, "matches none of the alternatives".into()));
            return;
        }
        let best = (0..results.len()).min_by_key(|&i| score(&results[i].0)).unwrap_or(0);
        let (r, _) = results.swap_remove(best);
        out.issues.extend(r.issues);
    }
}

fn schema_types(s: &J, schemas: &Schemas, doc: &str) -> Vec<String> {
    match s {
        J::Object(o) => {
            if let Some(t) = o.get("type") {
                return match t {
                    J::String(x) => vec![x.clone()],
                    J::Array(a) => a.iter().filter_map(|x| x.as_str().map(str::to_string)).collect(),
                    _ => vec![],
                };
            }
            if let Some(c) = o.get("const").or_else(|| o.get("enum").and_then(|e| e.as_array()?.first())) {
                return vec![match c {
                    J::String(_) => "string",
                    J::Number(_) => "number",
                    J::Bool(_) => "boolean",
                    J::Null => "null",
                    J::Array(_) => "array",
                    J::Object(_) => "object",
                }
                .to_string()];
            }
            if let Some(r) = o.get("$ref").and_then(J::as_str)
                && let Some((d, t)) = schemas.resolve(doc, r)
            {
                return schema_types(t, schemas, &d);
            }
            let mut v = Vec::new();
            for k in ["allOf", "anyOf", "oneOf"] {
                for sub in o.get(k).and_then(J::as_array).into_iter().flatten() {
                    v.extend(schema_types(sub, schemas, doc));
                }
            }
            v
        }
        _ => vec![],
    }
}

fn join_or(v: &[&str]) -> String {
    match v.len() {
        0 => String::new(),
        1 => v[0].to_string(),
        n => format!("{} or {}", v[..n - 1].join(", "), v[n - 1]),
    }
}

fn list_vals(v: &[J]) -> String {
    let s: Vec<String> = v.iter().map(show).collect();
    if s.len() > 12 { format!("{}, ... ({} values)", s[..12].join(", "), s.len()) } else { s.join(", ") }
}

fn num(f: f64) -> String {
    if f.fract() == 0.0 && f.abs() < 1e15 { format!("{}", f as i64) } else { format!("{f}") }
}

fn node_text(n: &Node) -> String {
    match &n.value {
        Value::Number(_, raw) => raw.clone(),
        Value::String(s) => format!("\"{s}\""),
        Value::Bool(b) => b.to_string(),
        Value::Null => "null".into(),
        Value::Array(_) => "a list".into(),
        Value::Object(_) => "an object".into(),
    }
}

fn is_rfc3339(s: &str) -> bool {
    let b = s.as_bytes();
    if b.len() < 20 {
        return false;
    }
    let d = |r: std::ops::Range<usize>| b[r].iter().all(u8::is_ascii_digit);
    d(0..4) && b[4] == b'-' && d(5..7) && b[7] == b'-' && d(8..10) && (b[10] == b'T' || b[10] == b't') && d(11..13) && b[13] == b':' && d(14..16) && b[16] == b':' && d(17..19)
        && (s.ends_with('Z') || s.ends_with('z') || s[19..].contains(['+', '-']))
}
