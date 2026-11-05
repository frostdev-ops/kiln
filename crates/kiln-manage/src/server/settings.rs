//! `enshrouded_server.json` (the game's server settings): read with the editor's schema, validated writes. The
//! value ranges are the ones the game reported. Unknown keys are kept as they are. The game
//! rewrites the file while it runs, so writes are refused then. Written with tabs, like the game does.

use super::instance::ServerInstance;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use std::path::Path;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerSettings {
    pub path: String,
    pub values: serde_json::Value,
    /// The editor's schema: `{gameSettings: [{key, group, label, kind, min?, max?, options?, hint}], presets,
    /// permissions: [{key, field, label}], fields}` (kind: factor | bool | enum | minutes | int; minutes are stored as
    /// nanoseconds).
    #[serde(default)]
    pub schema: serde_json::Value,
    /// The game rewrites the file while it runs: edits are refused then.
    #[serde(default)]
    pub running: bool,
}

fn f(key: &str, group: &str, label: &str, lo: f64, hi: f64, hint: &str) -> Value {
    json!({"key": key, "group": group, "label": label, "kind": "factor", "min": lo, "max": hi, "hint": hint})
}
fn b(key: &str, group: &str, label: &str, hint: &str) -> Value {
    json!({"key": key, "group": group, "label": label, "kind": "bool", "hint": hint})
}
fn e(key: &str, group: &str, label: &str, options: &[&str], hint: &str) -> Value {
    json!({"key": key, "group": group, "label": label, "kind": "enum", "options": options, "hint": hint})
}
fn m(key: &str, group: &str, label: &str, lo: f64, hi: f64, hint: &str) -> Value {
    json!({"key": key, "group": group, "label": label, "kind": "minutes", "min": lo, "max": hi, "hint": hint})
}

/// The game settings the editor offers.
pub fn game_settings() -> Vec<Value> {
    vec![
        f("playerHealthFactor", "Player", "Health", 0.25, 4.0, ""),
        f("playerManaFactor", "Player", "Mana", 0.25, 4.0, ""),
        f("playerStaminaFactor", "Player", "Stamina", 0.25, 4.0, ""),
        f("playerBodyHeatFactor", "Player", "Body heat", 0.5, 2.0, "How long you stay warm in the cold"),
        f("playerDivingTimeFactor", "Player", "Diving time", 0.5, 2.0, ""),
        f("shroudTimeFactor", "Player", "Time in the Shroud", 0.5, 2.0, ""),
        b("enableDurability", "Survival", "Item durability", ""),
        b("enableStarvingDebuff", "Survival", "Starving debuff", ""),
        f("foodBuffDurationFactor", "Survival", "Food buff duration", 0.5, 2.0, ""),
        m("fromHungerToStarving", "Survival", "Hunger to starving", 5.0, 20.0, "Minutes"),
        e("tombstoneMode", "Survival", "On death", &["AddBackpackMaterials", "Everything", "NoTombstone"], "What goes into your tombstone"),
        b("enableGliderTurbulences", "Survival", "Glider turbulence", ""),
        e("weatherFrequency", "World", "Weather", &["Disabled", "Rare", "Normal", "Often"], ""),
        e("randomSpawnerAmount", "World", "Random enemy spawns", &["Few", "Normal", "Many", "Extreme", "Automatic"], ""),
        json!({"key": "flameLevelFogRemoval", "group": "World", "label": "Fog removal flame level", "kind": "int", "min": 0, "max": 20,
               "hint": "Flame level at which Shroud fog lifts"}),
        m("dayTimeDuration", "Time", "Day length", 2.0, 60.0, "Minutes"),
        m("nightTimeDuration", "Time", "Night length", 2.0, 60.0, "Minutes"),
        f("miningDamageFactor", "Rates", "Mining damage", 0.5, 2.0, ""),
        f("plantGrowthSpeedFactor", "Rates", "Plant growth", 0.25, 2.0, ""),
        f("resourceDropStackAmountFactor", "Rates", "Resource drops", 0.25, 2.0, ""),
        f("factoryProductionSpeedFactor", "Rates", "Crafting station speed", 0.25, 2.0, ""),
        f("perkUpgradeRecyclingFactor", "Rates", "Perk recycling refund", 0.0, 1.0, ""),
        f("perkCostFactor", "Rates", "Perk cost", 0.25, 2.0, ""),
        f("experienceCombatFactor", "Rates", "Combat XP", 0.25, 2.0, ""),
        f("experienceMiningFactor", "Rates", "Mining XP", 0.0, 2.0, ""),
        f("experienceExplorationQuestsFactor", "Rates", "Exploration & quest XP", 0.25, 2.0, ""),
        e("aggroPoolAmount", "Enemies", "Simultaneous attackers", &["Few", "Normal", "Many", "Extreme"], ""),
        f("enemyDamageFactor", "Enemies", "Enemy damage", 0.25, 5.0, ""),
        f("enemyHealthFactor", "Enemies", "Enemy health", 0.25, 4.0, ""),
        f("enemyStaminaFactor", "Enemies", "Enemy stamina", 0.5, 2.0, ""),
        f("enemyPerceptionRangeFactor", "Enemies", "Enemy perception", 0.5, 2.0, ""),
        f("bossDamageFactor", "Enemies", "Boss damage", 0.2, 5.0, ""),
        f("bossHealthFactor", "Enemies", "Boss health", 0.2, 5.0, ""),
        f("threatBonus", "Enemies", "Threat bonus", 0.25, 4.0, ""),
        b("pacifyAllEnemies", "Enemies", "Pacify all enemies", "Enemies only fight back"),
        e("curseModifier", "Enemies", "Curse strength", &["Easy", "Normal", "Hard"], ""),
        e("tamingStartleRepercussion", "Other", "Startling a tame", &["KeepProgress", "LoseSomeProgress", "LoseAllProgress"], ""),
        e("fishingDifficulty", "Other", "Fishing difficulty", &["VeryEasy", "Easy", "Normal", "Hard", "VeryHard"], ""),
    ]
}

pub const PRESETS: &[&str] = &["Default", "Relaxed", "Hard", "Survival", "Custom"];
/// (field in the editor, key in the file, label)
pub const PERMISSIONS: &[(&str, &str, &str)] = &[
    ("canKickBan", "CanKickBan", "Kick & ban"),
    ("canAccessInventories", "CanAccessInventories", "Open chests"),
    ("canEditWorld", "CanEditWorld", "Edit the world"),
    ("canEditBase", "CanEditBase", "Build in bases"),
    ("canExtendBase", "CanExtendBase", "Extend bases"),
];

pub fn schema() -> Value {
    json!({
        "gameSettings": game_settings(),
        "presets": PRESETS,
        "permissions": PERMISSIONS.iter().map(|(k, f, l)| json!({"key": k, "field": f, "label": l})).collect::<Vec<_>>(),
        "fields": {
            "name": {"kind": "string", "max": 64}, "slotCount": {"kind": "int", "min": 1, "max": 16},
            "queryPort": {"kind": "int", "min": 1024, "max": 65535}, "enableVoiceChat": {"kind": "bool"},
            "enableTextChat": {"kind": "bool"}, "voiceChatMode": {"kind": "enum", "options": ["Proximity", "Global"]},
            "gameSettingsPreset": {"kind": "enum", "options": PRESETS},
            "userGroups": {"kind": "groups", "max": 16}, "bannedAccounts": {"kind": "steamIds"}
        }
    })
}

fn need(cond: bool, msg: impl Into<String>) -> Result<()> {
    if cond { Ok(()) } else { Err(Error::invalid(msg)) }
}

fn steam_id(s: &str) -> bool {
    s.len() == 17 && s.bytes().all(|b| b.is_ascii_digit())
}

/// Merges an edit onto the current config (validated). Unknown keys are kept.
pub fn validate(new: &Value, old: &Value) -> Result<Value> {
    let new = new.as_object().ok_or_else(|| Error::invalid("Config must be an object."))?;
    let mut out = old.clone();
    if !out.is_object() {
        out = json!({});
    }
    let o = out.as_object_mut().unwrap();
    if let Some(v) = new.get("name") {
        let s = v.as_str().map(str::trim).unwrap_or("");
        need(!s.is_empty() && s.chars().count() <= 64, "Server name: 1 to 64 characters.")?;
        o.insert("name".into(), json!(s));
    }
    if let Some(v) = new.get("slotCount") {
        need(v.as_i64().is_some_and(|n| (1..=16).contains(&n)) && v.is_i64(), "Player slots: 1 to 16.")?;
        o.insert("slotCount".into(), v.clone());
    }
    if let Some(v) = new.get("queryPort") {
        need(v.as_i64().is_some_and(|n| (1024..=65535).contains(&n)) && v.is_i64(), "Query port: 1024 to 65535.")?;
        o.insert("queryPort".into(), v.clone());
    }
    for key in ["enableVoiceChat", "enableTextChat"] {
        if let Some(v) = new.get(key) {
            need(v.is_boolean(), format!("{key} must be true or false."))?;
            o.insert(key.into(), v.clone());
        }
    }
    if let Some(v) = new.get("voiceChatMode") {
        need(matches!(v.as_str(), Some("Proximity" | "Global")), "Voice chat mode: Proximity or Global.")?;
        o.insert("voiceChatMode".into(), v.clone());
    }
    if let Some(v) = new.get("gameSettingsPreset") {
        need(v.as_str().is_some_and(|p| PRESETS.contains(&p)), "Unknown difficulty preset.")?;
        o.insert("gameSettingsPreset".into(), v.clone());
    }
    if let Some(gs) = new.get("gameSettings") {
        let gs = gs.as_object().ok_or_else(|| Error::invalid("gameSettings must be an object."))?;
        let cur = o.entry("gameSettings").or_insert_with(|| json!({}));
        if !cur.is_object() {
            *cur = json!({});
        }
        let cur = cur.as_object_mut().unwrap();
        for spec in game_settings() {
            let k = spec["key"].as_str().unwrap();
            let Some(v) = gs.get(k) else { continue };
            let label = spec["label"].as_str().unwrap();
            let (lo, hi) = (spec["min"].as_f64().unwrap_or(0.0), spec["max"].as_f64().unwrap_or(0.0));
            let v = match spec["kind"].as_str().unwrap() {
                "bool" => {
                    need(v.is_boolean(), format!("{label}: on or off."))?;
                    v.clone()
                }
                "enum" => {
                    let ok = spec["options"].as_array().unwrap().iter().any(|x| x == v) || cur.get(k) == Some(v);
                    need(ok, format!("{label}: unknown option."))?;
                    v.clone()
                }
                "minutes" => {
                    let ok = v.is_i64() || v.is_u64();
                    let n = v.as_f64().unwrap_or(-1.0);
                    need(ok && lo * 60e9 <= n && n <= hi * 60e9, format!("{label}: {lo} to {hi} minutes."))?;
                    v.clone()
                }
                "int" => {
                    need(v.is_i64() && v.as_f64().is_some_and(|n| lo <= n && n <= hi), format!("{label}: {lo} to {hi}."))?;
                    v.clone()
                }
                _ => {
                    let n = v.as_f64().filter(|_| v.is_number()).unwrap_or(f64::NAN);
                    need(lo <= n && n <= hi, format!("{label}: {lo} to {hi}."))?;
                    let r = (n * 1000.0).round() / 1000.0;
                    if r.fract() == 0.0 { json!(r as i64) } else { json!(r) }
                }
            };
            cur.insert(k.into(), v);
        }
    }
    if let Some(groups) = new.get("userGroups") {
        let groups = groups.as_array().filter(|g| (1..=16).contains(&g.len())).ok_or_else(|| Error::invalid("1 to 16 user groups."))?;
        let slots = o.get("slotCount").and_then(|v| v.as_i64()).unwrap_or(16);
        let old_groups: Map<String, Value> = o
            .get("userGroups")
            .and_then(|g| g.as_array())
            .map(|a| a.iter().filter_map(|g| Some((g.get("name")?.as_str()?.to_string(), g.clone()))).collect())
            .unwrap_or_default();
        let mut names = std::collections::HashSet::new();
        let mut result = Vec::new();
        for g in groups {
            let g = g.as_object().ok_or_else(|| Error::invalid("Bad user group."))?;
            let name = g.get("name").and_then(|n| n.as_str()).map(str::trim).unwrap_or("");
            need(!name.is_empty() && name.chars().count() <= 32, "Group names: 1 to 32 characters.")?;
            need(names.insert(name.to_lowercase()), format!("Duplicate group name {name}."))?;
            let pw = g.get("password").and_then(|p| p.as_str()).unwrap_or("");
            need(pw.chars().count() <= 64 && !pw.chars().any(|c| "\"\\\n\r\t".contains(c)), format!("{name}: password up to 64 characters, no quotes, backslashes or tabs."))?;
            let original = g.get("originalName").and_then(|n| n.as_str()).unwrap_or(name);
            let mut base = old_groups.get(original).and_then(|v| v.as_object()).cloned().unwrap_or_default();
            base.insert("name".into(), json!(name));
            base.insert("password".into(), json!(pw));
            for (key, file_key, label) in PERMISSIONS {
                let v = g.get(*key).or_else(|| g.get(*file_key)).cloned().unwrap_or(json!(false));
                need(v.is_boolean(), format!("{name}: {label} must be on or off."))?;
                base.remove(*key);
                base.insert((*file_key).into(), v);
            }
            let rs = g.get("reservedSlots").cloned().unwrap_or(json!(0));
            need(rs.as_i64().is_some_and(|n| (0..=slots).contains(&n)), format!("{name}: reserved slots out of range."))?;
            base.insert("reservedSlots".into(), rs);
            result.push(Value::Object(base));
        }
        let pws: Vec<&str> = result.iter().filter_map(|g| g["password"].as_str()).filter(|p| !p.is_empty()).collect();
        let unique: std::collections::HashSet<&&str> = pws.iter().collect();
        need(pws.len() == unique.len(), "Each group needs its own password: the server picks the group from the password a player types.")?;
        o.insert("userGroups".into(), Value::Array(result));
    }
    if let Some(bans) = new.get("bannedAccounts") {
        let bans = bans.as_array().ok_or_else(|| Error::invalid("Bans must be 17-digit SteamID64 numbers."))?;
        let mut v: Vec<String> = Vec::new();
        for b in bans {
            let s = b.as_str().map(str::trim).unwrap_or("");
            need(steam_id(s), "Bans must be 17-digit SteamID64 numbers.")?;
            v.push(s.to_string());
        }
        v.sort();
        v.dedup();
        o.insert("bannedAccounts".into(), json!(v));
    }
    Ok(out)
}

/// JSON with tab indentation (the game's own style), trailing newline.
pub fn render(v: &Value) -> Result<String> {
    let mut buf = Vec::new();
    let fmt = serde_json::ser::PrettyFormatter::with_indent(b"\t");
    let mut ser = serde_json::Serializer::with_formatter(&mut buf, fmt);
    v.serialize(&mut ser)?;
    buf.push(b'\n');
    String::from_utf8(buf).map_err(|e| Error::new(ErrorCode::Internal, e.to_string()))
}

fn read_value(path: &Path) -> Result<Value> {
    let text = std::fs::read_to_string(path).map_err(crate::util::io_at(path, "reading"))?;
    crate::admin::parse_jsonc(&text).map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {}", path.display(), e.message)))
}

pub fn read(instance: &ServerInstance) -> Result<ServerSettings> {
    let path = instance.local_dir()?.join(super::game::CONFIG);
    let values = read_value(&path)?;
    let running = super::run::status(instance).map(|s| s.pid.is_some()).unwrap_or(false);
    Ok(ServerSettings { path: path.display().to_string(), values, schema: schema(), running })
}

/// Validates and writes an edit (merged onto the current file). Refuses while the server runs.
pub fn write(instance: &ServerInstance, values: &serde_json::Value) -> Result<ServerSettings> {
    if super::run::status(instance)?.pid.is_some() {
        return Err(Error::new(ErrorCode::Conflict, "The server is running and rewrites this file: stop it first."));
    }
    let path = instance.local_dir()?.join(super::game::CONFIG);
    let merged = validate(values, &read_value(&path)?)?;
    crate::util::write_atomic(&path, render(&merged)?.as_bytes())?;
    read(instance)
}

/// Sets `name` and `queryPort` of a config file (server creation).
pub fn set_name_port(path: &Path, name: &str, port: u16) -> Result<()> {
    let merged = validate(&json!({"name": name, "queryPort": port}), &read_value(path)?)?;
    crate::util::write_atomic(path, render(&merged)?.as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validation_rules() {
        let old = json!({"name": "A", "slotCount": 4, "unknown": 1, "gameSettings": {"playerHealthFactor": 1, "weatherFrequency": "Normal"},
                         "userGroups": [{"name": "Admin", "password": "x", "CanKickBan": true, "reservedSlots": 0, "extra": 5}]});
        let new = json!({"name": " B ", "gameSettings": {"playerHealthFactor": 1.23456, "dayTimeDuration": 1_800_000_000_000i64, "weatherFrequency": "Rare"},
                         "userGroups": [{"name": "Admins", "originalName": "Admin", "password": "y", "canKickBan": true}], "bannedAccounts": ["76561198000000002", "76561198000000001"]});
        let out = validate(&new, &old).unwrap();
        assert_eq!(out["name"], "B");
        assert_eq!(out["unknown"], 1);
        assert_eq!(out["gameSettings"]["playerHealthFactor"], 1.235);
        assert_eq!(out["gameSettings"]["weatherFrequency"], "Rare");
        assert_eq!(out["userGroups"][0]["extra"], 5, "kept from the renamed group");
        assert_eq!(out["userGroups"][0]["CanKickBan"], true);
        assert_eq!(out["bannedAccounts"], json!(["76561198000000001", "76561198000000002"]));
        assert!(validate(&json!({"slotCount": 17}), &old).is_err());
        assert!(validate(&json!({"gameSettings": {"enemyDamageFactor": 9}}), &old).is_err());
        assert!(validate(&json!({"gameSettings": {"dayTimeDuration": 60}}), &old).is_err(), "minutes are nanoseconds");
        assert!(validate(&json!({"userGroups": [{"name": "a", "password": "p"}, {"name": "b", "password": "p"}]}), &old).is_err());
        let text = render(&out).unwrap();
        assert!(text.contains("\n\t\"name\": \"B\""), "{text}");
    }
}
