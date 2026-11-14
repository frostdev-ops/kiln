//! `kiln-release.json`: the file that says which mod, version and zip a release holds. `kiln publish` writes it next
//! to the zip it uploads; the update check reads it from a release to know what a candidate is before anything is
//! downloaded. Schema: `schema/kiln-release.schema.json`.
//!
//! ```json
//! {"format":1,"mods":[{"id":"example","version":"1.2.3","side":"both","apiVersion":"0.2",
//!   "artifact":{"name":"example-1.2.3.zip","sha256":"<64 lower-case hex>","bytes":12345}}]}
//! ```
//!
//! The reader is strict because the file comes from the network: at most 256 KiB, plain JSON (no comments, no
//! trailing commas, no BOM), no key given twice in any object, `format` 1, 1 to 64 records, every field checked with
//! the manifest's own parsers. Keys it does not know are ignored. A file that breaks a rule is refused as a whole;
//! nothing of it is trusted.

use crate::jsonc::{self, JValue, Node};
use crate::manifest::{is_mod_id, Side};
use crate::semver::{SemVersion, VersionRange};
use crate::{Error, ErrorCode, Result};

/// The fixed name of the file as a release asset.
pub const FILE_NAME: &str = "kiln-release.json";
/// The largest file the reader accepts.
pub const MAX_BYTES: usize = 256 * 1024;
/// The most records one file may hold.
pub const MAX_MODS: usize = 64;
/// The largest `bytes` value: integers above 2^53 - 1 do not survive every JSON reader.
pub const MAX_ARTIFACT_BYTES: u64 = (1 << 53) - 1;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseBinding {
    pub mods: Vec<BoundMod>,
}

/// One mod of a release.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoundMod {
    pub id: String,
    /// As written (the spelling that is in the tag and the asset name); compare with [`BoundMod::semver`].
    pub version: String,
    pub side: Side,
    /// The manifest's `apiVersion` range, as written.
    pub api_version: String,
    pub artifact: BoundArtifact,
}

/// The zip a record stands for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoundArtifact {
    /// The asset's base name.
    pub name: String,
    /// Lower-case hex.
    pub sha256: String,
    pub bytes: u64,
}

impl BoundMod {
    pub fn semver(&self) -> SemVersion {
        // checked when the record was read or written
        SemVersion::try_parse(&self.version).unwrap_or_else(|| SemVersion::new(0, 0, 0))
    }
}

fn bad(what: impl std::fmt::Display) -> Error {
    Error::new(ErrorCode::Schema, format!("{FILE_NAME}: {what}"))
}

/// An asset base name: 1 to 200 characters of `A-Z a-z 0-9 . _ -`, not `.` or `..`, no `..` inside.
pub fn is_asset_name(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 200
        && s.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
        && s != "."
        && !s.contains("..")
}

pub fn is_sha256_hex(s: &str) -> bool {
    s.len() == 64 && s.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn side_name(s: Side) -> &'static str {
    match s {
        Side::Server => "server",
        Side::Client => "client",
        Side::Both => "both",
    }
}

fn check(m: &BoundMod) -> Result<()> {
    if !is_mod_id(&m.id) || m.id.ends_with('\n') {
        return Err(bad("a record's id is not a mod id"));
    }
    if m.version.len() > 128 || m.version.trim() != m.version || SemVersion::try_parse(&m.version).is_none() {
        return Err(bad(format!("{}: version is not a semantic version", m.id)));
    }
    if m.api_version.is_empty() || m.api_version.len() > 128 || m.api_version.bytes().any(|b| !(0x20..=0x7e).contains(&b)) {
        return Err(bad(format!("{}: apiVersion is not a version range", m.id)));
    }
    VersionRange::try_parse(&m.api_version).map_err(|_| bad(format!("{}: apiVersion is not a version range", m.id)))?;
    if !is_asset_name(&m.artifact.name) {
        return Err(bad(format!("{}: artifact.name is not a plain file name", m.id)));
    }
    if !is_sha256_hex(&m.artifact.sha256) {
        return Err(bad(format!("{}: artifact.sha256 is not 64 lower-case hex digits", m.id)));
    }
    if m.artifact.bytes == 0 || m.artifact.bytes > MAX_ARTIFACT_BYTES {
        return Err(bad(format!("{}: artifact.bytes is out of range", m.id)));
    }
    Ok(())
}

fn no_duplicate_keys(n: &Node) -> Result<()> {
    match &n.value {
        JValue::Object(members) => {
            for (i, m) in members.iter().enumerate() {
                if members[..i].iter().any(|o| o.key == m.key) {
                    return Err(bad("a key is given twice in one object"));
                }
                no_duplicate_keys(&m.value)?;
            }
        }
        JValue::Array(items) => {
            for e in items {
                no_duplicate_keys(&e.node)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn string(o: &Node, key: &str, at: &str) -> Result<String> {
    o.get(key).and_then(|n| n.as_str()).map(str::to_string).ok_or_else(|| bad(format!("{at}{key} must be a string")))
}

/// A JSON number that is a plain non-negative integer (`12345`; not `1e3`, `1.0`, `-1`, `01`).
fn integer(o: &Node, key: &str, at: &str) -> Result<u64> {
    let text = o.get(key).and_then(|n| n.as_number()).ok_or_else(|| bad(format!("{at}{key} must be a whole number")))?;
    let plain = !text.is_empty() && text.bytes().all(|b| b.is_ascii_digit()) && (text == "0" || !text.starts_with('0'));
    if !plain {
        return Err(bad(format!("{at}{key} must be a whole number")));
    }
    text.parse::<u64>().map_err(|_| bad(format!("{at}{key} is out of range")))
}

impl ReleaseBinding {
    /// A binding of one mod.
    pub fn single(m: BoundMod) -> ReleaseBinding {
        ReleaseBinding { mods: vec![m] }
    }

    /// Reads the bytes of a `kiln-release.json`.
    pub fn parse(bytes: &[u8]) -> Result<ReleaseBinding> {
        if bytes.len() > MAX_BYTES {
            return Err(Error::new(ErrorCode::Refused, format!("{FILE_NAME} is larger than {} KiB", MAX_BYTES / 1024)));
        }
        let text = std::str::from_utf8(bytes).map_err(|_| Error::new(ErrorCode::Parse, format!("{FILE_NAME} is not UTF-8")))?;
        // Plain JSON first (serde_json refuses comments, trailing commas and a BOM); then the tree, which keeps a
        // repeated key where serde_json would silently take the last one.
        serde_json::from_str::<serde::de::IgnoredAny>(text).map_err(|e| Error::new(ErrorCode::Parse, format!("{FILE_NAME}: {e}")))?;
        let root = jsonc::parse_tree(text).map_err(|e| Error::new(ErrorCode::Parse, format!("{FILE_NAME}: {e}")))?;
        no_duplicate_keys(&root)?;
        if !root.is_object() {
            return Err(bad("the file must be an object"));
        }
        if integer(&root, "format", "")? != 1 {
            return Err(bad("format must be 1 (a newer Kiln wrote this file)"));
        }
        let list = root.get("mods").filter(|n| n.is_array()).ok_or_else(|| bad("mods must be a list"))?.elements();
        if list.is_empty() || list.len() > MAX_MODS {
            return Err(bad(format!("mods must hold 1 to {MAX_MODS} records")));
        }
        let mut mods = Vec::with_capacity(list.len());
        for (i, e) in list.iter().enumerate() {
            let at = format!("mods[{i}].");
            let o = &e.node;
            if !o.is_object() {
                return Err(bad(format!("mods[{i}] must be an object")));
            }
            let side = match string(o, "side", &at)?.as_str() {
                "server" => Side::Server,
                "client" => Side::Client,
                "both" => Side::Both,
                _ => return Err(bad(format!("{at}side must be server, client or both"))),
            };
            let a = o.get("artifact").filter(|n| n.is_object()).ok_or_else(|| bad(format!("{at}artifact must be an object")))?;
            let at_a = format!("{at}artifact.");
            let m = BoundMod {
                id: string(o, "id", &at)?,
                version: string(o, "version", &at)?,
                side,
                api_version: string(o, "apiVersion", &at)?,
                artifact: BoundArtifact { name: string(a, "name", &at_a)?, sha256: string(a, "sha256", &at_a)?, bytes: integer(a, "bytes", &at_a)? },
            };
            check(&m)?;
            mods.push(m);
        }
        Ok(ReleaseBinding { mods })
    }

    /// The one record of a mod. No record, or more than one, is an error: a file that names a mod twice is not
    /// trusted for it.
    pub fn record_for(&self, id: &str) -> Result<&BoundMod> {
        let mut found = self.mods.iter().filter(|m| m.id == id);
        match (found.next(), found.next()) {
            (Some(m), None) => Ok(m),
            (None, _) => Err(bad(format!("no record for {id}"))),
            (Some(_), Some(_)) => Err(bad(format!("more than one record for {id}"))),
        }
    }

    /// The file's text: fixed key order, two-space indent, a final newline, so the same binding always gives the
    /// same bytes (they are hashed when a release is planned). Refuses a binding the reader would refuse.
    pub fn to_json(&self) -> Result<String> {
        if self.mods.is_empty() || self.mods.len() > MAX_MODS {
            return Err(bad(format!("mods must hold 1 to {MAX_MODS} records")));
        }
        let mut out = String::from("{\n  \"format\": 1,\n  \"mods\": [\n");
        for (i, m) in self.mods.iter().enumerate() {
            check(m)?;
            if self.mods[..i].iter().any(|o| o.id == m.id) {
                return Err(bad(format!("more than one record for {}", m.id)));
            }
            out.push_str(&format!(
                "    {{\n      \"id\": {},\n      \"version\": {},\n      \"side\": \"{}\",\n      \"apiVersion\": {},\n      \"artifact\": {{\n        \"name\": {},\n        \"sha256\": \"{}\",\n        \"bytes\": {}\n      }}\n    }}{}\n",
                jsonc::quote(&m.id),
                jsonc::quote(&m.version),
                side_name(m.side),
                jsonc::quote(&m.api_version),
                jsonc::quote(&m.artifact.name),
                m.artifact.sha256,
                m.artifact.bytes,
                if i + 1 < self.mods.len() { "," } else { "" },
            ));
        }
        out.push_str("  ]\n}\n");
        if out.len() > MAX_BYTES {
            return Err(Error::new(ErrorCode::Refused, format!("{FILE_NAME} would be larger than {} KiB", MAX_BYTES / 1024)));
        }
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SHA: &str = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    fn sample() -> BoundMod {
        BoundMod {
            id: "example".into(),
            version: "1.2.3".into(),
            side: Side::Both,
            api_version: "0.2".into(),
            artifact: BoundArtifact { name: "example-1.2.3.zip".into(), sha256: SHA.into(), bytes: 12345 },
        }
    }

    fn doc(record: &str) -> String {
        format!(r#"{{"format":1,"mods":[{record}]}}"#)
    }

    /// The sample record with another `bytes` token (the digits also occur in the sample hash, so the key is named).
    fn with_bytes(token: &str) -> String {
        let r = record().replacen("\"bytes\":12345", &format!("\"bytes\":{token}"), 1);
        assert_ne!(r, record());
        r
    }

    fn record() -> String {
        format!(r#"{{"id":"example","version":"1.2.3","side":"both","apiVersion":"0.2","artifact":{{"name":"example-1.2.3.zip","sha256":"{SHA}","bytes":12345}}}}"#)
    }

    #[test]
    fn the_chapter_example_reads() {
        let b = ReleaseBinding::parse(doc(&record()).as_bytes()).unwrap();
        assert_eq!(b, ReleaseBinding::single(sample()));
        assert_eq!(b.record_for("example").unwrap().semver(), SemVersion::new(1, 2, 3));
    }

    #[test]
    fn written_bytes_read_back_and_are_stable() {
        let mut second = sample();
        second.id = "other.mod".into();
        second.side = Side::Client;
        second.version = "v2.0.0-rc.1+build5".into();
        second.artifact.name = "other.mod-v2.0.0-rc.1.zip".into();
        let b = ReleaseBinding { mods: vec![sample(), second] };
        let text = b.to_json().unwrap();
        assert_eq!(ReleaseBinding::parse(text.as_bytes()).unwrap(), b);
        assert_eq!(text, b.to_json().unwrap());
        assert!(text.ends_with("}\n") && text.starts_with("{\n  \"format\": 1,"));
        // key order is fixed, not whatever a map gives
        let (id, ver, side, api, art) = (text.find("\"id\"").unwrap(), text.find("\"version\"").unwrap(), text.find("\"side\"").unwrap(), text.find("\"apiVersion\"").unwrap(), text.find("\"artifact\"").unwrap());
        assert!(id < ver && ver < side && side < api && api < art);
        // and it is valid against serde_json too
        let v: serde_json::Value = serde_json::from_str(&text).unwrap();
        assert_eq!(v["mods"][1]["artifact"]["bytes"], 12345);
    }

    #[test]
    fn unknown_keys_are_ignored() {
        let r = record().replacen("{", r#"{"homepage":"https://example.com","extra":{"a":[1,2]},"#, 1);
        let text = format!(r#"{{"format":1,"generator":"kiln 0.2.0","mods":[{r}]}}"#);
        assert_eq!(ReleaseBinding::parse(text.as_bytes()).unwrap(), ReleaseBinding::single(sample()));
    }

    #[test]
    fn a_key_given_twice_is_refused_at_every_level() {
        let top = format!(r#"{{"format":1,"format":1,"mods":[{}]}}"#, record());
        let rec = record().replacen(r#""id":"example","#, r#""id":"evil","id":"example","#, 1);
        let art = record().replacen(r#""bytes":12345"#, r#""bytes":1,"bytes":12345"#, 1);
        let unknown = record().replacen("{", r#"{"x":1,"x":2,"#, 1);
        let nested_unknown = record().replacen("{", r#"{"x":{"y":1,"y":2},"#, 1);
        for text in [top, doc(&rec), doc(&art), doc(&unknown), doc(&nested_unknown)] {
            let e = ReleaseBinding::parse(text.as_bytes()).unwrap_err();
            assert_eq!(e.code, ErrorCode::Schema, "{text}");
            assert!(e.message.contains("twice"), "{}", e.message);
        }
    }

    #[test]
    fn only_plain_json_is_read() {
        let good = doc(&record());
        let with_comment = good.replacen("{", "{ // note\n", 1);
        let trailing = good.replacen("]}", "],}", 1);
        let bom = format!("\u{feff}{good}");
        for text in [with_comment, trailing, bom, String::new(), "[]".into(), "null".into(), format!("{good}{good}")] {
            assert!(ReleaseBinding::parse(text.as_bytes()).is_err(), "{text}");
        }
        assert!(ReleaseBinding::parse(&[0xff, 0xfe, b'{', b'}']).is_err());
    }

    #[test]
    fn the_size_and_count_limits_hold() {
        let pad = " ".repeat(MAX_BYTES);
        let big = format!("{}{pad}", doc(&record()));
        assert_eq!(ReleaseBinding::parse(big.as_bytes()).unwrap_err().code, ErrorCode::Refused);
        let records = |n: usize| (0..n).map(|i| record().replacen("\"example\"", &format!("\"m{i}\""), 1)).collect::<Vec<_>>().join(",");
        assert_eq!(ReleaseBinding::parse(doc(&records(64)).as_bytes()).unwrap().mods.len(), 64);
        assert!(ReleaseBinding::parse(doc(&records(65)).as_bytes()).is_err());
        assert!(ReleaseBinding::parse(br#"{"format":1,"mods":[]}"#).is_err());
        let many = ReleaseBinding { mods: (0..65).map(|i| BoundMod { id: format!("m{i}"), ..sample() }).collect() };
        assert!(many.to_json().is_err());
    }

    #[test]
    fn each_field_is_checked() {
        let cases: Vec<(&str, String)> = vec![
            ("format 2", doc(&record()).replacen("\"format\":1", "\"format\":2", 1)),
            ("format as text", doc(&record()).replacen("\"format\":1", "\"format\":\"1\"", 1)),
            ("format 1.0", doc(&record()).replacen("\"format\":1", "\"format\":1.0", 1)),
            ("no format", format!(r#"{{"mods":[{}]}}"#, record())),
            ("mods not a list", r#"{"format":1,"mods":{}}"#.to_string()),
            ("record not an object", doc("\"example\"")),
            ("id upper case", doc(&record().replacen("\"example\"", "\"Example\"", 1))),
            ("id with a slash", doc(&record().replacen("\"example\"", "\"a/b\"", 1))),
            ("id with a newline", doc(&record().replacen("\"example\"", "\"example\\n\"", 1))),
            ("version not semver", doc(&record().replacen("\"1.2.3\"", "\"latest\"", 1))),
            ("version wildcard", doc(&record().replacen("\"1.2.3\"", "\"1.x\"", 1))),
            ("version with spaces", doc(&record().replacen("\"1.2.3\"", "\" 1.2.3\"", 1))),
            ("side", doc(&record().replacen("\"both\"", "\"everywhere\"", 1))),
            ("no side", doc(&record().replacen("\"side\":\"both\",", "", 1))),
            ("apiVersion", doc(&record().replacen("\"0.2\"", "\">=\"", 1))),
            ("apiVersion empty", doc(&record().replacen("\"0.2\"", "\"\"", 1))),
            ("no artifact", doc(r#"{"id":"example","version":"1.2.3","side":"both","apiVersion":"0.2"}"#)),
            ("name with a folder", doc(&record().replacen("example-1.2.3.zip", "a/example-1.2.3.zip", 1))),
            ("name with a backslash", doc(&record().replacen("example-1.2.3.zip", "a\\\\b.zip", 1))),
            ("name dot-dot", doc(&record().replacen("example-1.2.3.zip", "..", 1))),
            ("name with dot-dot inside", doc(&record().replacen("example-1.2.3.zip", "a..zip", 1))),
            ("name empty", doc(&record().replacen("example-1.2.3.zip", "", 1))),
            ("name too long", doc(&record().replacen("example-1.2.3.zip", &"a".repeat(201), 1))),
            ("sha upper case", doc(&record().replacen(SHA, &SHA.to_uppercase(), 1))),
            ("sha short", doc(&record().replacen(SHA, &SHA[1..], 1))),
            ("bytes zero", doc(&with_bytes("0"))),
            ("bytes negative", doc(&with_bytes("-5"))),
            ("bytes fraction", doc(&with_bytes("12345.0"))),
            ("bytes exponent", doc(&with_bytes("1e3"))),
            ("bytes as text", doc(&with_bytes("\"12345\""))),
            ("bytes over 2^53", doc(&with_bytes("9007199254740992"))),
            ("bytes over u64", doc(&with_bytes("99999999999999999999999"))),
        ];
        for (what, text) in cases {
            assert!(ReleaseBinding::parse(text.as_bytes()).is_err(), "accepted: {what}: {text}");
        }
        // the largest allowed size reads
        let ok = doc(&with_bytes("9007199254740991"));
        assert_eq!(ReleaseBinding::parse(ok.as_bytes()).unwrap().mods[0].artifact.bytes, MAX_ARTIFACT_BYTES);
    }

    #[test]
    fn a_mod_named_twice_has_no_record() {
        let text = doc(&format!("{},{}", record(), with_bytes("999")));
        let b = ReleaseBinding::parse(text.as_bytes()).unwrap();
        assert!(b.record_for("example").unwrap_err().message.contains("more than one"));
        assert!(b.record_for("absent").unwrap_err().message.contains("no record"));
        // and the writer does not produce such a file
        assert!(b.to_json().is_err());
    }

    #[test]
    fn the_writer_refuses_what_the_reader_refuses() {
        for edit in [
            (|m: &mut BoundMod| m.id = "Bad Id".into()) as fn(&mut BoundMod),
            |m| m.version = "nope".into(),
            |m| m.api_version = "".into(),
            |m| m.artifact.name = "../x.zip".into(),
            |m| m.artifact.sha256 = "zz".into(),
            |m| m.artifact.bytes = 0,
        ] {
            let mut m = sample();
            edit(&mut m);
            assert!(ReleaseBinding::single(m).to_json().is_err());
        }
    }

    /// The schema file says what the reader enforces (limits and patterns), and its example reads.
    #[test]
    fn the_schema_file_agrees_with_the_reader() {
        let schema: serde_json::Value = serde_json::from_str(include_str!("../../../schema/kiln-release.schema.json")).unwrap();
        assert_eq!(schema["properties"]["format"]["const"], 1);
        assert_eq!(schema["properties"]["mods"]["maxItems"], MAX_MODS);
        assert_eq!(schema["properties"]["mods"]["minItems"], 1);
        let rec = &schema["properties"]["mods"]["items"];
        let mut req: Vec<&str> = rec["required"].as_array().unwrap().iter().map(|v| v.as_str().unwrap()).collect();
        req.sort();
        assert_eq!(req, ["apiVersion", "artifact", "id", "side", "version"]);
        let art = &rec["properties"]["artifact"];
        assert_eq!(art["properties"]["bytes"]["maximum"], MAX_ARTIFACT_BYTES);
        assert_eq!(art["properties"]["bytes"]["minimum"], 1);
        assert_eq!(art["properties"]["name"]["maxLength"], 200);
        let example = std::fs::read(std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/examples/kiln-release.json")).unwrap();
        ReleaseBinding::parse(&example).unwrap();
    }
}
