//! Generated artifacts from the schemas: schema/idrefs.json (where mod files name game ids, for tools/vscode
//! completion) and the schema reference pages docs/kiln/modding/schemas/*.md. Tests keep both in sync with schema/.

use super::embedded;
use serde_json::{Value as J, json};
use std::collections::BTreeSet;

fn resolve<'a>(doc: &'a J, root_id: &str, r: &str) -> Option<(String, &'a J)> {
    let (file, frag) = r.split_once('#').unwrap_or((r, ""));
    let id = if file.is_empty() { root_id.to_string() } else { file.to_string() };
    let base = if file.is_empty() { doc } else { embedded().schemas.doc(&id)? };
    let mut v = base;
    for seg in frag.split('/').filter(|s| !s.is_empty()) {
        v = v.get(seg)?;
    }
    Some((id, v))
}

/// Every value or key that names a game entry: [{schema, files, pointer, section, forms}] (pointer: `*` any key or
/// index, a trailing `~` the key itself).
pub fn idrefs() -> J {
    let e = embedded();
    let mut rows: BTreeSet<(String, String, String, String)> = BTreeSet::new();
    fn walk(id: &str, s: &J, ptr: &str, key: bool, seen: &mut Vec<String>, rows: &mut BTreeSet<(String, String, String, String)>, schema: &str) {
        let Some(o) = s.as_object() else { return };
        if let Some(sec) = o.get("x-kiln-ref").and_then(J::as_str) {
            let forms: Vec<String> = o.get("x-kiln-ref-forms").and_then(J::as_array).into_iter().flatten().filter_map(|x| x.as_str().map(str::to_string)).collect();
            let p = if key { format!("{ptr}~") } else { ptr.to_string() };
            rows.insert((schema.to_string(), if p.is_empty() { "/".into() } else { p }, sec.to_string(), forms.join(",")));
        }
        if let Some(r) = o.get("$ref").and_then(J::as_str) {
            // a $ref already on this path is a cycle (kiln-quests: a condition's all / any / not): tag without the pointer, which grows
            let tag = format!("{id}{r}");
            if !seen.contains(&tag) && seen.len() < 64 {
                if let Some((rid, t)) = resolve(embedded().schemas.doc(id).unwrap_or(&J::Null), id, r) {
                    seen.push(tag);
                    walk(&rid, t, ptr, key, seen, rows, schema);
                    seen.pop();
                }
            }
        }
        for k in ["allOf", "anyOf", "oneOf"] {
            for sub in o.get(k).and_then(J::as_array).into_iter().flatten() {
                walk(id, sub, ptr, key, seen, rows, schema);
            }
        }
        for k in ["then", "else", "not"] {
            if k != "not"
                && let Some(sub) = o.get(k)
            {
                walk(id, sub, ptr, key, seen, rows, schema);
            }
        }
        if key {
            return;
        }
        if let Some(p) = o.get("properties").and_then(J::as_object) {
            for (k, sub) in p {
                walk(id, sub, &format!("{ptr}/{}", k.replace('~', "~0").replace('/', "~1")), false, seen, rows, schema);
            }
        }
        for k in ["additionalProperties", "items"] {
            if let Some(sub) = o.get(k).filter(|x| x.is_object()) {
                walk(id, sub, &format!("{ptr}/*"), false, seen, rows, schema);
            }
        }
        for sub in o.get("patternProperties").and_then(J::as_object).into_iter().flat_map(|m| m.values()) {
            walk(id, sub, &format!("{ptr}/*"), false, seen, rows, schema);
        }
        for sub in o.get("prefixItems").and_then(J::as_array).into_iter().flatten() {
            walk(id, sub, &format!("{ptr}/*"), false, seen, rows, schema);
        }
        if let Some(pn) = o.get("propertyNames") {
            walk(id, pn, &format!("{ptr}/*"), true, seen, rows, schema);
        }
    }
    for row in &e.index {
        if let Some(d) = e.schemas.doc(&row.schema) {
            walk(&row.schema, d, "", false, &mut vec![], &mut rows, &row.schema);
        }
    }
    let refs: Vec<J> = rows
        .into_iter()
        .map(|(schema, pointer, section, forms)| {
            let files = e.index.iter().find(|r| r.schema == schema).map(|r| r.file_match.clone()).unwrap_or_default();
            json!({"schema": schema, "files": files, "pointer": pointer, "section": section, "forms": forms.split(',').filter(|x| !x.is_empty()).collect::<Vec<_>>()})
        })
        .collect();
    json!({
        "format": 1,
        "description": "Where mod files name game entries (generated from the x-kiln-ref annotations of schema/*.schema.json by kiln_manage::lint::docs::idrefs; do not edit: the lint tests regenerate it with KILN_LINT_WRITE=1). pointer: JSON pointer, * = any key or index, a trailing ~ = the key itself. section: the game-data catalog section (or a mod-defined namespace: variants, skillNodes, talentTrees, natives). forms: name, id (0x / decimal), guid, glob, item:, like:, template:, buff_, skill_, guid#k, lang, displayName.",
        "refs": refs,
    })
}

fn type_of(s: &J, id: &str) -> String {
    let Some(o) = s.as_object() else { return if s == &J::Bool(true) { "any".into() } else { String::new() } };
    if let Some(c) = o.get("const") {
        return format!("`{c}`");
    }
    if let Some(J::Array(v)) = o.get("enum") {
        return v.iter().map(|x| format!("`{x}`")).collect::<Vec<_>>().join(" \\| ");
    }
    if let Some(t) = o.get("type") {
        let base = match t {
            J::String(x) => x.clone(),
            J::Array(a) => a.iter().filter_map(J::as_str).collect::<Vec<_>>().join(" \\| "),
            _ => String::new(),
        };
        if base == "array"
            && let Some(it) = o.get("items")
        {
            let inner = type_of(it, id);
            if !inner.is_empty() {
                return format!("array of {inner}");
            }
        }
        return base;
    }
    if let Some(r) = o.get("$ref").and_then(J::as_str) {
        let name = r.rsplit('/').next().unwrap_or(r);
        if r.starts_with("#/$defs/") {
            return format!("[{name}](#{})", name.to_lowercase());
        }
        return format!("`{}`", r.replace("#/$defs/", " "));
    }
    for k in ["oneOf", "anyOf"] {
        if let Some(J::Array(alts)) = o.get(k) {
            let v: Vec<String> = alts.iter().map(|a| type_of(a, id)).filter(|x| !x.is_empty()).collect();
            if !v.is_empty() {
                return v.join(" or ");
            }
        }
    }
    String::new()
}

fn notes(s: &J) -> String {
    let Some(o) = s.as_object() else { return String::new() };
    let mut n: Vec<String> = Vec::new();
    if let Some(d) = o.get("description").and_then(J::as_str) {
        n.push(d.replace('|', "\\|").replace('\n', " "));
    }
    if let Some(m) = o.get("x-kiln-message").and_then(J::as_str)
        && o.get("description").is_none()
    {
        n.push(m.replace('|', "\\|"));
    }
    if let Some(d) = o.get("default") {
        n.push(format!("Default `{d}`."));
    }
    for (k, label) in [("minimum", "min"), ("maximum", "max"), ("minItems", "min items"), ("maxItems", "max items"), ("minLength", "min length"), ("maxLength", "max length")] {
        if let Some(v) = o.get(k) {
            n.push(format!("{label} {v}"));
        }
    }
    if let Some(p) = o.get("pattern").and_then(J::as_str)
        && o.get("x-kiln-message").is_none()
        && p.len() < 60
    {
        n.push(format!("Pattern `{}`.", p.replace('|', "\\|")));
    }
    if let Some(r) = o.get("x-kiln-ref").and_then(J::as_str) {
        n.push(format!("Names a **{r}** entry (checked against the catalog)."));
    }
    if o.get("x-kiln-severity").and_then(J::as_str) == Some("warning") {
        n.push("Kiln warns (not refused).".into());
    }
    if o.get("x-kiln-file").is_some() {
        n.push("A file that must exist.".into());
    }
    n.join(" ")
}

fn table(o: &serde_json::Map<String, J>, id: &str, out: &mut String) {
    let Some(props) = o.get("properties").and_then(J::as_object) else { return };
    let req: Vec<&str> = o.get("required").and_then(J::as_array).into_iter().flatten().filter_map(J::as_str).collect();
    let rows: Vec<(&String, &J)> = props.iter().filter(|(_, v)| v.as_object().is_some_and(|m| !m.is_empty())).collect();
    if rows.is_empty() {
        return;
    }
    out.push_str("| Key | Type | Required | Notes |\n|---|---|---|---|\n");
    for (k, v) in rows {
        out.push_str(&format!("| `{k}` | {} | {} | {} |\n", type_of(v, id), if req.contains(&k.as_str()) { "yes" } else { "" }, notes(v)));
    }
    out.push('\n');
}

/// A reference page for one schema of schema/schemas.json (Markdown).
pub fn schema_markdown(id: &str) -> Option<String> {
    let e = embedded();
    let doc = e.schemas.doc(id)?;
    let row = e.index.iter().find(|r| r.schema == id);
    let mut out = String::new();
    out.push_str(&format!("# {}\n\n", doc["title"].as_str().unwrap_or(id)));
    out.push_str(&format!("<!-- Generated from schema/{id} by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->\n\n"));
    if let Some(d) = doc["description"].as_str() {
        out.push_str(d);
        out.push_str("\n\n");
    }
    if let Some(r) = row {
        out.push_str(&format!(
            "- Files: {}\n- JSON: comments {}, trailing commas {}{}\n- Schema: [`schema/{id}`](../../../../schema/{id}); rules: [lint.md](../lint.md)\n\n",
            r.file_match.iter().map(|f| format!("`{f}`")).collect::<Vec<_>>().join(", "),
            if r.dialect.comments { "allowed" } else { "**not allowed**" },
            if r.dialect.trailing_commas { "allowed" } else { "**not allowed**" },
            r.container.as_ref().map(|c| format!(" (inside a binary .{c} file)")).unwrap_or_default()
        ));
    }
    out.push_str("## Top level\n\n");
    if let Some(o) = doc.as_object() {
        table(o, id, &mut out);
    }
    if let Some(defs) = doc["$defs"].as_object() {
        for (name, d) in defs {
            let Some(o) = d.as_object() else { continue };
            let has_props = o.get("properties").and_then(J::as_object).is_some_and(|p| p.values().any(|v| v.as_object().is_some_and(|m| !m.is_empty())));
            let n = notes(d);
            let t = type_of(d, id);
            if !has_props && n.is_empty() {
                continue;
            }
            out.push_str(&format!("## {name}\n\n"));
            if !n.is_empty() {
                out.push_str(&n);
                out.push_str("\n\n");
            }
            if !has_props && !t.is_empty() {
                out.push_str(&format!("Type: {t}\n\n"));
            }
            table(o, id, &mut out);
        }
    }
    Some(out)
}

/// The file name of a schema's reference page (content-pack.schema.json -> content-pack.md).
pub fn page_name(id: &str) -> String {
    format!("{}.md", id.trim_end_matches(".schema.json").trim_end_matches(".json"))
}
