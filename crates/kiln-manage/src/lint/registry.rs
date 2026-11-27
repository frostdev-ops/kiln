//! Client requirements of a registry-only data pack. Read the shipped Kiln field metadata, not game paths.
use super::LMod;
use std::collections::HashMap;
use std::sync::OnceLock;

type Fields = HashMap<(String, String), Option<u8>>;
fn fields() -> &'static Fields {
    static FIELDS: OnceLock<Fields> = OnceLock::new();
    FIELDS.get_or_init(|| {
        fn visit(out: &mut Fields, registry: &str, prefix: &str, fields: &serde_json::Value, read_only: bool) {
            let Some(fields) = fields.as_object() else { return };
            for (name, spec) in fields {
                let path = format!("{prefix}{name}");
                let ro = spec["readOnly"].as_bool().unwrap_or(read_only);
                if spec["type"] == "struct" {
                    visit(out, registry, &format!("{path}."), &spec["fields"], ro);
                    continue;
                }
                let requirement = if ro { None } else {
                    match spec["joinedClient"].as_str() {
                        Some("serverOnly") => Some(0),
                        Some("now" | "worldLoad") => Some(1),
                        Some("restart" | "localOnly") => Some(2),
                        _ => None, // unknown timing/raw data is not a safety claim
                    }
                };
                out.insert((registry.into(), path.clone()), requirement);
                if spec["type"] == "list" && spec["of"]["type"] == "struct" {
                    visit(out, registry, &format!("{path}[]."), &spec["of"]["fields"], ro);
                }
            }
        }
        let mut fields = Fields::new();
        for text in [
            include_str!("../../../../schema/registry/items.json"),
            include_str!("../../../../schema/registry/recipes.json"),
            include_str!("../../../../schema/registry/loot.json"),
            include_str!("../../../../schema/registry/drops.json"),
            include_str!("../../../../schema/registry/balance.json"),
            // dev.32 (experimental)
            include_str!("../../../../schema/registry/creatures.json"),
            include_str!("../../../../schema/registry/arsenals.json"),
            include_str!("../../../../schema/registry/perks.json"),
            include_str!("../../../../schema/registry/skills.json"),
            include_str!("../../../../schema/registry/stations.json"),
            include_str!("../../../../schema/registry/buffs.json"),
            include_str!("../../../../schema/registry/abilities.json"),
            include_str!("../../../../schema/registry/world.json"),
            include_str!("../../../../schema/registry/difficulty.json"),
            include_str!("../../../../schema/registry/limits.json"),
        ] {
            let doc: serde_json::Value = serde_json::from_str(text).expect("shipped registry schema");
            visit(&mut fields, doc["registry"].as_str().expect("registry name"), "", &doc["fields"], false);
        }
        fields
    })
}

/// The registries the field table holds (tests: every shipped schema is one of them).
#[cfg(test)]
pub(super) fn registries() -> std::collections::BTreeSet<String> {
    fields().keys().map(|(registry, _)| registry.clone()).collect()
}

fn generic_field(path: &str) -> Option<String> {
    let mut result = String::new();
    let mut rest = path;
    while let Some(i) = rest.find('[') {
        result.push_str(&rest[..i]);
        let end = rest[i..].find(']')? + i;
        let index = &rest[i + 1..end];
        if !index.is_empty() && index != "*" && !index.bytes().all(|c| c.is_ascii_digit()) { return None }
        result.push_str("[]");
        rest = &rest[end + 1..];
    }
    result.push_str(rest);
    Some(result)
}

fn field_requirement(registry: &str, path: &str) -> Option<u8> {
    let field = generic_field(path)?;
    let direct = fields().get(&(registry.into(), field.clone()));
    direct.or_else(|| field.strip_suffix("[]").and_then(|parent| fields().get(&(registry.into(), parent.into())))).copied().flatten()
}

pub(super) fn clients(m: &LMod) -> Option<&'static str> {
    let manifest = m.manifest()?.root.as_ref()?;
    if m.native || manifest.str_of("entryAssembly").is_some_and(|s| !s.is_empty()) || manifest.get("native").is_some() { return None }
    let mut requirement = 0;
    let mut edits = 0;
    for file in &m.files {
        if file.kind == "manifest" { continue }
        if file.kind != "content" { return None }
        let root = file.root.as_ref()?;
        if root.as_object()?.iter().any(|member| !matches!(member.key.as_str(), "$schema" | "format" | "registry")) { return None }
        let section = root.get("registry")?;
        for phase in ["add", "alter", "final"] {
            let Some(batch) = section.get(phase) else { continue };
            for edit in batch.as_array()? {
                if edit.get("raw").is_some() { return None }
                let registry = edit.str_of("registry")?;
                let need = if let Some(operation) = edit.str_of("entry") {
                    match (registry, operation) {
                        ("loot", "remove") => 0,
                        ("items" | "recipes", "add" | "remove") => 1,
                        _ => return None,
                    }
                } else {
                    field_requirement(registry, edit.str_of("field")?)?
                };
                requirement = requirement.max(need);
                if let Some(overrides) = edit.get("fields") {
                    for field in overrides.as_object()? {
                        requirement = requirement.max(field_requirement(registry, &field.key)?);
                    }
                }
                edits += 1;
            }
        }
    }
    (edits > 0).then_some(match requirement { 0 => "none", 1 => "kiln", _ => "mod" })
}
