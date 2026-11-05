//! The reader, search and status on the committed synthetic fixture (testdata/, invented names),
//! the fixture against catalog.schema.json's required shape, and (feature `gameart`) a whole build from a synthetic
//! KFC4 + PE pair. `real_client` / `real_server` (ignored) build from an installed game:
//! `KILN_CATALOG_GAME=<game folder> KILN_CATALOG_OUT=<scratch dir> cargo test -p kiln-manage --features gameart
//! --release --lib -- --ignored catalog::tests::real_ --nocapture`.

use super::*;

fn fixture() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("src/catalog/testdata")
}

#[test]
fn fixture_loads_and_has_every_section() {
    let c = Catalog::load(&fixture().join("client.json")).unwrap();
    assert_eq!(c.kind, KIND);
    assert_eq!(c.format, FORMAT);
    assert_eq!(c.game.side, "client");
    for s in SECTIONS {
        assert!(!c.section(s).is_empty(), "fixture section {s} is empty");
    }
    assert!(c.languages.contains(&"en-us".to_string()));
    assert!(c.fields.get("items").is_some_and(|f| !f.is_empty()));
    // ids and GUIDs are lower-case in the agreed form
    for (s, list) in &c.sections {
        for e in list {
            if let Some(id) = &e.id {
                assert!(id.len() == 10 && id.starts_with("0x") && id[2..].chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()), "{s}: bad id {id}");
            }
            if let Some(g) = &e.guid {
                assert!(g.len() == 36 && !g.chars().any(|c| c.is_ascii_uppercase()), "{s}: bad guid {g}");
            }
        }
    }
    assert_eq!(c.counts(), c.counts, "fixture counts match its sections");
}

#[test]
fn lookup_by_name_id_and_guid() {
    let c = Catalog::load(&fixture().join("client.json")).unwrap();
    let it = c.section("items")[0].clone();
    assert_eq!(c.lookup("items", it.name.as_deref().unwrap()), Some(&it));
    assert_eq!(c.lookup("items", &it.id.clone().unwrap().to_uppercase().replace("0X", "0x")), Some(&it));
    assert_eq!(c.lookup("items", &it.guid.clone().unwrap().to_uppercase()), Some(&it));
    assert_eq!(c.lookup("items", "No_Such_Item"), None);
    // names are case-sensitive
    assert_eq!(c.lookup("items", &it.name.unwrap().to_lowercase()), None);
}

#[test]
fn search_ranks_exact_then_prefix() {
    let c = Catalog::load(&fixture().join("client.json")).unwrap();
    let hits = search(&c, "frost", None, 0);
    assert!(!hits.is_empty());
    assert!(hits.iter().all(|h| h.section != "loca"), "loca is only searched on request");
    let only = search(&c, "frost", Some("items"), 1);
    assert_eq!(only.len(), 1);
    assert_eq!(only[0].section, "items");
    let exact = search(&c, "Test_Frost_Blade", None, 5);
    assert_eq!(exact[0].name.as_deref(), Some("Test_Frost_Blade"));
    let loca = search(&c, "frost", Some("loca"), 5);
    assert!(!loca.is_empty() && loca[0].display_name.is_some());
    let words = search(&c, "blade frost", Some("items"), 5);
    assert!(words.iter().any(|h| h.name.as_deref() == Some("Test_Frost_Blade")));
}

#[test]
fn lenient_reader() {
    // languages inside sections (older catalogs), unknown keys, non-object entries, missing everything
    let c = Catalog::from_slice(br#"{"kind":"kiln.catalog","format":1,"sections":{"languages":["en-us"],"items":[{"name":"A","x":1},"junk"],"odd":5},"new":true}"#).unwrap();
    assert_eq!(c.languages, vec!["en-us"]);
    assert_eq!(c.section("items").len(), 1);
    assert_eq!(c.section("items")[0].get("x"), Some(&serde_json::json!(1)));
    assert!(c.section("odd").is_empty());
    assert!(c.extra.contains_key("new"));
    assert!(Catalog::from_slice(b"{}").is_ok());
    assert!(Catalog::from_slice(br#"{"kind":"something.else"}"#).is_err());
    assert!(Catalog::from_slice(br#"{"kind":"kiln.catalog","format":99}"#).is_err());
}

#[test]
fn round_trip_through_the_writer() {
    let c = Catalog::load(&fixture().join("client.json")).unwrap();
    let text = to_text(&c).unwrap();
    let back = Catalog::from_slice(text.as_bytes()).unwrap();
    assert_eq!(back, c);
    // one entry per line
    let items_lines = text.lines().filter(|l| l.trim_start().starts_with("{\"name\":\"Test_")).count();
    assert!(items_lines >= c.section("items").len());
}

#[test]
fn find_and_status() {
    let tmp = std::env::temp_dir().join(format!("kiln-catalog-find-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&tmp);
    std::fs::create_dir_all(&tmp).unwrap();
    std::fs::copy(fixture().join("client.json"), tmp.join("server.json")).unwrap();
    // explicit dir: client missing -> server
    assert_eq!(find(None, Some(&tmp)), Some(tmp.join("server.json")));
    assert_eq!(find(Some("client"), Some(&tmp)), None);
    assert_eq!(find(Some("server"), Some(&tmp.join("server.json"))), Some(tmp.join("server.json")));
    let st = status(&tmp, None).unwrap();
    assert_eq!(st.len(), 2);
    assert!(!st[0].exists && st[1].exists);
    assert_eq!(st[1].current, None, "no game to compare with");
    assert!(st[1].counts.get("items").copied().unwrap_or(0) > 0);
    // a fake game folder whose .kfc version differs -> not current; equal -> current
    let game = tmp.join("game");
    std::fs::create_dir_all(&game).unwrap();
    let c = Catalog::load(&tmp.join("server.json")).unwrap();
    let kfc = |v: &str| {
        let mut b = b"KFC4".to_vec();
        b.resize(0x18, 0);
        b[0x10..0x14].copy_from_slice(&8u32.to_le_bytes());
        b[0x14..0x18].copy_from_slice(&(v.len() as u32).to_le_bytes());
        b.extend_from_slice(v.as_bytes());
        b
    };
    std::fs::write(game.join("enshrouded_server.kfc"), kfc("999|other")).unwrap();
    let st = status(&tmp, Some(&game)).unwrap();
    assert_eq!(st[1].current, Some(false));
    assert_eq!(st[1].game_build.as_deref(), Some("999"));
    std::fs::write(game.join("enshrouded_server.kfc"), kfc(c.game.kfc_version.as_deref().unwrap())).unwrap();
    assert_eq!(status(&tmp, Some(&game)).unwrap()[1].current, Some(c.recipe == Some(RECIPE)));
    assert_eq!(status(&tmp, Some(&game)).unwrap()[0].current, None, "the client side is not compared with a server");
    let _ = std::fs::remove_dir_all(&tmp);
}

/// The committed schema requires what the reader relies on, and the fixture satisfies its `required` lists and
/// the id/GUID patterns (a small check of the schema's own keywords; no JSON-Schema crate in the default build).
#[test]
fn fixture_matches_the_schema() {
    let schema: Value = serde_json::from_str(&std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("src/catalog/catalog.schema.json")).unwrap()).unwrap();
    let fix: Value = serde_json::from_str(&std::fs::read_to_string(fixture().join("client.json")).unwrap()).unwrap();
    assert_eq!(schema["$schema"], "https://json-schema.org/draft/2020-12/schema");
    for k in schema["required"].as_array().unwrap() {
        assert!(fix.get(k.as_str().unwrap()).is_some(), "fixture lacks required {k}");
    }
    assert_eq!(schema["properties"]["kind"]["const"], KIND);
    let id_rx = schema["$defs"]["id"]["pattern"].as_str().unwrap();
    assert_eq!(id_rx, "^0x[0-9a-f]{8}$");
    let secs = schema["properties"]["sections"]["properties"].as_object().unwrap();
    for s in SECTIONS {
        assert!(secs.contains_key(s), "schema lacks section {s}");
    }
    for (name, list) in fix["sections"].as_object().unwrap() {
        let def = &secs[name];
        let req: Vec<&str> = def["items"]["required"].as_array().map(|a| a.iter().filter_map(Value::as_str).collect()).unwrap_or_default();
        for e in list.as_array().unwrap() {
            for r in &req {
                assert!(e.get(*r).is_some(), "{name} entry lacks {r}: {e}");
            }
        }
    }
}

#[cfg(feature = "gameart")]
mod build {
    //! A whole build from a synthetic game: KFC4 + zstd stream + .dat (the same builder as gameart's tests, copied
    //! here because those helpers are private to gameart's test module) and a PE32+ with reflection records.
    use super::super::*;
    use crate::gameart::kfc::{ContentHash, fnv1a32};
    use std::collections::HashMap;

    pub struct Res {
        pub guid: [u8; 16],
        pub type_hash: u32,
        pub part: u32,
        pub data: Vec<u8>,
    }

    pub fn guid(n: u8) -> [u8; 16] {
        let mut g = [0u8; 16];
        g[0] = n;
        g[15] = 0xA5;
        g
    }

    /// Writes `<dir>/<stem>.kfc`, `.kfc_resources`, `<stem>_000.dat`, `_001.dat` (layout in gameart/kfc.rs).
    pub fn build_kfc(dir: &Path, stem: &str, version: &str, res: &[Res], contents: &[(ContentHash, Vec<u8>)], layouts: &[(u32, u32)]) {
        let chunk_size = 64;
        let mut stream = Vec::new();
        let mut rvals = Vec::new();
        for r in res {
            while !stream.len().is_multiple_of(16) {
                stream.push(0);
            }
            rvals.push((stream.len() as u32, r.data.len() as u32));
            stream.extend_from_slice(&r.data);
        }
        let mut packed = Vec::new();
        let mut chunks = Vec::new();
        for off in (0..stream.len()).step_by(chunk_size) {
            let raw = &stream[off..(off + chunk_size).min(stream.len())];
            let z = zstd::bulk::compress(raw, 3).unwrap();
            let foff = packed.len();
            packed.extend_from_slice(&z);
            let stored = z.len().div_ceil(4096) * 4096;
            packed.resize(foff + stored, 0);
            chunks.push([foff as u32, stored as u32, z.len() as u32, off as u32, raw.len() as u32]);
        }
        std::fs::write(dir.join(format!("{stem}.kfc_resources")), &packed).unwrap();
        let mut dats = [Vec::new(), Vec::new()];
        let mut cvals = Vec::new();
        for (i, (_, c)) in contents.iter().enumerate() {
            let dat = &mut dats[i % 2];
            let off = dat.len();
            dat.extend_from_slice(c);
            dat.resize(dat.len().div_ceil(4096) * 4096, 0);
            cvals.push((off as u32, (i % 2) as u16));
        }
        for (i, d) in dats.iter().enumerate() {
            std::fs::write(dir.join(format!("{stem}_{i:03}.dat")), d).unwrap();
        }
        let mut types: Vec<u32> = res.iter().map(|r| r.type_hash).collect();
        types.sort();
        types.dedup();
        let mut order = Vec::new();
        let mut bundles = Vec::new();
        for th in &types {
            let first = order.len();
            order.extend(res.iter().enumerate().filter(|(_, r)| r.type_hash == *th).map(|(i, _)| i as u32));
            let layout = layouts.iter().find(|(t, _)| t == th).map_or(0xABC0_0000 | (th & 0xFFFF), |x| x.1);
            bundles.push((*th, layout, first as u32, (order.len() - first) as u32));
        }
        let le = |v: &[u32]| v.iter().flat_map(|x| x.to_le_bytes()).collect::<Vec<u8>>();
        let tables: Vec<(Vec<u8>, usize)> = vec![
            (version.as_bytes().to_vec(), version.len()),
            (dats.iter().flat_map(|d| [(d.len() as u64).to_le_bytes(), 1u64.to_le_bytes()].concat()).collect(), 2),
            (vec![], 0),
            (vec![], 0),
            (le(&[stream.len() as u32, packed.len() as u32, res.len() as u32, 0, chunks.len() as u32]), 1),
            (le(&order), order.len()),
            (le(&[0, contents.len() as u32]), 1),
            (contents.iter().flat_map(|(h, _)| h.to_bytes()).collect(), contents.len()),
            (cvals.iter().flat_map(|(o, c)| [o.to_le_bytes().to_vec(), 0u16.to_le_bytes().to_vec(), c.to_le_bytes().to_vec(), vec![0; 8]].concat()).collect(), contents.len()),
            (le(&[0, res.len() as u32]), 1),
            (res.iter().flat_map(|r| [r.guid.to_vec(), le(&[r.type_hash, r.part, 0, 0])].concat()).collect(), res.len()),
            (rvals.iter().flat_map(|(o, s)| le(&[*o, *s])).collect(), res.len()),
            (le(&[0, bundles.len() as u32]), 1),
            (le(&bundles.iter().map(|b| b.0).collect::<Vec<_>>()), bundles.len()),
            (bundles.iter().flat_map(|b| le(&[b.1, b.2, b.3])).collect(), bundles.len()),
            (chunks.iter().flat_map(|c| le(c)).collect(), chunks.len()),
        ];
        let mut head = b"KFC4".to_vec();
        head.resize(16 + 8 * tables.len(), 0);
        let mut body = Vec::new();
        let hl = head.len();
        for (i, (data, count)) in tables.iter().enumerate() {
            let at = 0x10 + 8 * i;
            let start = hl + body.len();
            let rel = if data.is_empty() { 0 } else { (start - at) as u32 };
            head[at..at + 4].copy_from_slice(&rel.to_le_bytes());
            head[at + 4..at + 8].copy_from_slice(&(*count as u32).to_le_bytes());
            body.extend_from_slice(data);
            while !body.len().is_multiple_of(4) {
                body.push(0);
            }
        }
        let mut out = [head, body].concat();
        let n = out.len() as u32;
        out[4..8].copy_from_slice(&n.to_le_bytes());
        out[8..12].copy_from_slice(&12u32.to_le_bytes());
        std::fs::write(dir.join(format!("{stem}.kfc")), out).unwrap();
    }

    pub struct Rec {
        pub name: &'static str,
        pub size: u32,
        pub kind: u8,
        pub inner: Option<&'static str>,
        pub fields: Vec<(&'static str, &'static str, u64)>,
        pub values: Vec<(&'static str, u64)>,
        pub layout: u32,
    }

    pub fn rec(name: &'static str, size: u32, kind: u8) -> Rec {
        Rec { name, size, kind, inner: None, fields: vec![], values: vec![], layout: 0 }
    }

    /// A PE32+ whose .data holds reflection records (layout in gameart/refl.rs).
    pub fn build_pe(records: &[Rec]) -> Vec<u8> {
        let base: u64 = 0x1_4000_0000;
        let (rdata_rva, data_rva) = (0x2000u64, 0x10000u64);
        let mut rdata: Vec<u8> = Vec::new();
        let mut strs: HashMap<String, u64> = HashMap::new();
        let mut s = |t: &str, rdata: &mut Vec<u8>| -> u64 {
            if let Some(v) = strs.get(t) {
                return *v;
            }
            let va = base + rdata_rva + rdata.len() as u64;
            rdata.extend_from_slice(t.as_bytes());
            rdata.push(0);
            strs.insert(t.to_string(), va);
            va
        };
        let rec_va: HashMap<&str, u64> = records.iter().enumerate().map(|(i, r)| (r.name, base + data_rva + 0x80 * i as u64)).collect();
        let extra = 0x80 * records.len() + 0x100;
        let mut data = vec![0u8; extra];
        let mut tables: Vec<u8> = Vec::new();
        let put = |b: &mut Vec<u8>, o: usize, v: &[u8]| b[o..o + v.len()].copy_from_slice(v);
        for (i, r) in records.iter().enumerate() {
            let o = 0x80 * i;
            let qn = s(r.name, &mut rdata);
            put(&mut data, o + 0x20, &qn.to_le_bytes());
            put(&mut data, o + 0x28, &(r.name.len() as u64).to_le_bytes());
            put(&mut data, o + 0x38, &r.inner.map_or(0u64, |n| rec_va[n]).to_le_bytes());
            put(&mut data, o + 0x40, &r.size.to_le_bytes());
            put(&mut data, o + 0x48, &((r.fields.len().max(r.values.len())) as u32).to_le_bytes());
            put(&mut data, o + 0x4C, &(r.kind as u32).to_le_bytes());
            put(&mut data, o + 0x50, &fnv1a32(r.name.as_bytes()).to_le_bytes());
            put(&mut data, o + 0x54, &r.layout.to_le_bytes());
            if !r.fields.is_empty() {
                put(&mut data, o + 0x58, &(base + data_rva + (extra + tables.len()) as u64).to_le_bytes());
                for (fname, ftype, foff) in &r.fields {
                    let np = s(fname, &mut rdata);
                    for v in [np, fname.len() as u64, rec_va[ftype], *foff, 0, 0] {
                        tables.extend_from_slice(&v.to_le_bytes());
                    }
                }
            }
            if !r.values.is_empty() {
                put(&mut data, o + 0x60, &(base + data_rva + (extra + tables.len()) as u64).to_le_bytes());
                for (vname, v) in &r.values {
                    let np = s(vname, &mut rdata);
                    for x in [np, vname.len() as u64, *v, 0, 0] {
                        tables.extend_from_slice(&x.to_le_bytes());
                    }
                }
            }
        }
        data.extend_from_slice(&tables);
        let secs = [(".rdata", rdata_rva as u32, rdata), (".data", data_rva as u32, data)];
        let pe = 0x80usize;
        let mut raw = vec![0u8; 0x400];
        raw[0..2].copy_from_slice(b"MZ");
        raw[0x3C..0x40].copy_from_slice(&(pe as u32).to_le_bytes());
        raw[pe..pe + 4].copy_from_slice(b"PE\0\0");
        raw[pe + 4..pe + 6].copy_from_slice(&0x8664u16.to_le_bytes());
        raw[pe + 6..pe + 8].copy_from_slice(&(secs.len() as u16).to_le_bytes());
        raw[pe + 20..pe + 22].copy_from_slice(&240u16.to_le_bytes());
        raw[pe + 24..pe + 26].copy_from_slice(&0x20Bu16.to_le_bytes());
        raw[pe + 48..pe + 56].copy_from_slice(&base.to_le_bytes());
        let mut so = pe + 24 + 240;
        for (name, rva, blob) in &secs {
            let at = raw.len();
            let mut n8 = [0u8; 8];
            n8[..name.len()].copy_from_slice(name.as_bytes());
            raw[so..so + 8].copy_from_slice(&n8);
            for (k, v) in [blob.len() as u32, *rva, blob.len() as u32, at as u32].iter().enumerate() {
                raw[so + 8 + 4 * k..so + 12 + 4 * k].copy_from_slice(&v.to_le_bytes());
            }
            so += 40;
            raw.extend_from_slice(blob);
            raw.resize(raw.len().div_ceil(0x200) * 0x200, 0);
        }
        raw
    }

    /// A blob: a fixed part, then out-of-line data appended with self-relative offsets.
    pub struct Blob(pub Vec<u8>);
    impl Blob {
        pub fn new(size: usize) -> Blob {
            Blob(vec![0; size])
        }
        pub fn u8(&mut self, o: usize, v: u8) -> &mut Self {
            self.0[o] = v;
            self
        }
        pub fn u16(&mut self, o: usize, v: u16) -> &mut Self {
            self.0[o..o + 2].copy_from_slice(&v.to_le_bytes());
            self
        }
        pub fn u32(&mut self, o: usize, v: u32) -> &mut Self {
            self.0[o..o + 4].copy_from_slice(&v.to_le_bytes());
            self
        }
        pub fn guid(&mut self, o: usize, g: [u8; 16]) -> &mut Self {
            self.0[o..o + 16].copy_from_slice(&g);
            self
        }
        /// Appends `data` (4-aligned) and writes `{i32 rel, u32 count}` at `o`.
        pub fn array(&mut self, o: usize, data: &[u8], count: u32) -> &mut Self {
            while !self.0.len().is_multiple_of(4) {
                self.0.push(0);
            }
            let at = self.0.len();
            self.0.extend_from_slice(data);
            self.u32(o, (at - o) as u32).u32(o + 4, count)
        }
        pub fn string(&mut self, o: usize, s: &str) -> &mut Self {
            self.array(o, s.as_bytes(), s.len() as u32)
        }
    }

    /// Items, perks, templates, a skill tree, buffs, recipes, loca; reflection for exactly those fields.
    pub fn fake_game(dir: &Path) {
        use super::super::decode::*;
        let mut t = vec![
            rec("keen::uint8", 1, K_U8),
            rec("keen::uint16", 2, K_U16),
            rec("keen::uint32", 4, K_U32),
            rec("keen::uint64", 8, K_U64),
            rec("keen::string", 8, K_BLOB_STRING),
            rec("keen::Guid", 16, K_GUID),
        ];
        let mut idt = rec("keen::HashKey32", 4, K_STRUCT);
        idt.fields = vec![("value", "keen::uint32", 0)];
        t.push(idt);
        let mut ch = rec("keen::ContentHash", 16, K_STRUCT);
        ch.fields = vec![("size", "keen::uint32", 0), ("hash0", "keen::uint32", 4), ("hash1", "keen::uint32", 8), ("hash2", "keen::uint32", 12)];
        t.push(ch);
        let mut rcr = rec("keen::ResourceContentReference", 32, K_STRUCT);
        rcr.fields = vec![("hash", "keen::ContentHash", 0)];
        t.push(rcr);
        let mut lid = rec("keen::LanguageId", 4, K_ENUM);
        lid.inner = Some("keen::uint32");
        lid.values = vec![("En_Us", 0), ("De_De", 1)];
        t.push(lid);
        let mut lang = rec("keen::LocaTagCollectionResourceLanguage", 40, K_STRUCT);
        lang.fields = vec![("language", "keen::LanguageId", 0), ("data", "keen::ResourceContentReference", 8)];
        t.push(lang);
        let mut la = rec("keen::BlobArray<keen::LocaTagCollectionResourceLanguage>", 8, K_BLOB_ARRAY);
        la.inner = Some("keen::LocaTagCollectionResourceLanguage");
        t.push(la);
        let mut lc = rec("keen::LocaTagCollectionResource", 40, K_STRUCT);
        lc.fields = vec![("languages", "keen::BlobArray<keen::LocaTagCollectionResourceLanguage>", 0x20)];
        t.push(lc);
        let mut lt = rec("keen::LocaTagResource", 24, K_STRUCT);
        lt.fields = vec![("id", "keen::uint32", 0), ("text", "keen::string", 4)];
        t.push(lt);
        let mut cat = rec("keen::ItemCategory", 1, K_ENUM);
        cat.inner = Some("keen::uint8");
        cat.values = vec![("Weapons", 2), ("Materials", 9)];
        t.push(cat);
        let mut item = rec("keen::ItemInfo", 0x40, K_STRUCT);
        item.layout = 0x0700_9d37;
        item.fields = vec![
            ("itemId", "keen::HashKey32", 0),
            ("objectId", "keen::Guid", 4),
            ("maxStackSize", "keen::uint16", 0x14),
            ("category", "keen::ItemCategory", 0x16),
            ("name", "keen::Guid", 0x18),
            ("debugName", "keen::string", 0x28),
        ];
        t.push(item);
        let mut perk = rec("keen::Perk", 0x20, K_STRUCT);
        perk.fields = vec![("name", "keen::Guid", 0), ("debugName", "keen::string", 0x10)];
        t.push(perk);
        let mut comps = rec("keen::BlobArray<keen::uint32>", 8, K_BLOB_ARRAY);
        comps.inner = Some("keen::uint32");
        t.push(comps);
        let mut tmpl = rec("keen::ecs::TemplateResource", 24, K_STRUCT);
        tmpl.fields = vec![("name", "keen::string", 0), ("components", "keen::BlobArray<keen::uint32>", 8)];
        t.push(tmpl);
        let mut node = rec("keen::SkillTreeNode", 16, K_STRUCT);
        node.fields = vec![("id", "keen::HashKey32", 0), ("name", "keen::HashKey32", 4), ("activeSkillUnlock", "keen::HashKey32", 8)];
        t.push(node);
        let mut na = rec("keen::BlobArray<keen::SkillTreeNode>", 8, K_BLOB_ARRAY);
        na.inner = Some("keen::SkillTreeNode");
        t.push(na);
        let mut tree = rec("keen::SkillTreeResource", 16, K_STRUCT);
        tree.fields = vec![("nodes", "keen::BlobArray<keen::SkillTreeNode>", 0)];
        t.push(tree);
        let mut buff = rec("keen::BuffType", 24, K_STRUCT);
        buff.fields = vec![("buffTypeId", "keen::HashKey32", 0), ("name", "keen::Guid", 8)];
        t.push(buff);
        let mut ba = rec("keen::BlobArray<keen::BuffType>", 8, K_BLOB_ARRAY);
        ba.inner = Some("keen::BuffType");
        t.push(ba);
        let mut bc = rec("keen::BuffTypeCollectionResource", 32, K_STRUCT);
        bc.fields = vec![("buffs", "keen::BlobArray<keen::BuffType>", 0x18)];
        t.push(bc);
        let mut comp = rec("keen::ecs::Health", 8, K_STRUCT);
        comp.fields = vec![("health", "keen::uint32", 0), ("healthMax", "keen::uint32", 4)];
        t.push(comp);
        // a component with a time, an enum (numbers not 0, 1, 2, ...), a flag set, a reference, a list of variants, configs
        let mut time = rec("keen::Time", 8, K_STRUCT);
        time.fields = vec![("value", "keen::uint64", 0)];
        t.push(time);
        let mut force = rec("keen::impact::PushForce", 1, K_ENUM);
        force.inner = Some("keen::uint8");
        force.values = vec![("None", 0), ("Big", 5)];
        t.push(force);
        let mut mask = rec("keen::ForceMask", 1, K_BM8);
        mask.inner = Some("keen::impact::PushForce");
        t.push(mask);
        t.push(rec("keen::ObjectReference<keen::ecs::Template>", 16, K_OBJECT_REF));
        let mut tref = rec("keen::ecs::TemplateReference", 16, K_TYPEDEF);
        tref.inner = Some("keen::ObjectReference<keen::ecs::Template>");
        t.push(tref);
        let mut variant = rec("keen::BlobVariant<keen::impact::ImpactConfig>", 12, K_BLOB_VARIANT);
        variant.inner = Some("keen::uint32");
        t.push(variant);
        let mut clist = rec("keen::impact::ConfigurationArray", 8, K_BLOB_ARRAY);
        clist.inner = Some("keen::BlobVariant<keen::impact::ImpactConfig>");
        t.push(clist);
        let mut cfgs = rec("keen::impact::Configurations", 16, K_STRUCT);
        cfgs.fields = vec![("simple", "keen::impact::ConfigurationArray", 0), ("scaled", "keen::impact::ConfigurationArray", 8)];
        t.push(cfgs);
        let mut life = rec("keen::ecs::LifeTime", 64, K_STRUCT);
        life.fields = vec![
            ("lifeTime", "keen::Time", 0),
            ("force", "keen::impact::PushForce", 8),
            ("forces", "keen::ForceMask", 9),
            ("spawn", "keen::ecs::TemplateReference", 16),
            ("configValues", "keen::impact::Configurations", 32),
            ("guid", "keen::Guid", 48),
        ];
        t.push(life);
        t.push(rec("keen::ecs::DoNotSave", 1, K_STRUCT));
        // an Impact program's variables, named and typed
        let mut var = rec("keen::ImpactVariable", 24, K_STRUCT);
        var.fields = vec![("configId", "keen::HashKey32", 4), ("type", "keen::HashKey32", 8), ("dbgName", "keen::string", 16)];
        t.push(var);
        let mut vars = rec("keen::BlobArray<keen::ImpactVariable>", 8, K_BLOB_ARRAY);
        vars.inner = Some("keen::ImpactVariable");
        t.push(vars);
        let mut prog = rec("keen::ImpactProgram", 64, K_STRUCT);
        prog.fields = vec![("dataLayout", "keen::BlobArray<keen::ImpactVariable>", 40)];
        t.push(prog);
        t.push(rec("keen::float32", 4, K_F32));
        // a VFX: what the catalog reads of it, and the components and events that name it
        t.push(rec("keen::ObjectReference<keen::VfxDefinition>", 16, K_OBJECT_REF));
        let mut vref = rec("keen::VfxReference", 16, K_TYPEDEF);
        vref.inner = Some("keen::ObjectReference<keen::VfxDefinition>");
        t.push(vref);
        let mut vc = rec("keen::ecs::VfxComponentResource", 16, K_STRUCT);
        vc.fields = vec![("vfx", "keen::VfxReference", 0)];
        t.push(vc);
        let mut dl = rec("keen::ecs::DestroyOnLoot", 16, K_STRUCT);
        dl.fields = vec![("vfx", "keen::VfxReference", 0)];
        t.push(dl);
        let mut r8 = rec("keen::Range8", 2, K_STRUCT);
        r8.fields = vec![("start", "keen::uint8", 0), ("count", "keen::uint8", 1)];
        t.push(r8);
        let mut pd = rec("keen::VfxUserEffectParameterDescription", 12, K_STRUCT);
        pd.fields = vec![("nameHash", "keen::HashKey32", 0), ("typeId", "keen::uint32", 4), ("dataRange", "keen::Range8", 8)];
        t.push(pd);
        for (arr, inner) in [
            ("keen::BlobArray<keen::VfxUserEffectParameterDescription>", "keen::VfxUserEffectParameterDescription"),
            ("keen::BlobArray<keen::VfxParticleStateBufferInfo>", "keen::VfxParticleStateBufferInfo"),
            ("keen::BlobArray<keen::VfxBufferMember>", "keen::VfxBufferMember"),
        ] {
            let mut a = rec(arr, 8, K_BLOB_ARRAY);
            a.inner = Some(inner);
            t.push(a);
        }
        let mut member = rec("keen::VfxBufferMember", 16, K_STRUCT);
        member.fields = vec![("name", "keen::string", 0)];
        t.push(member);
        let mut state = rec("keen::VfxParticleStateBufferInfo", 12, K_STRUCT);
        state.fields = vec![("members", "keen::BlobArray<keen::VfxBufferMember>", 0)];
        t.push(state);
        let mut vr = rec("keen::VfxResource", 216, K_STRUCT);
        vr.fields = vec![
            ("cullingDistanceMin", "keen::float32", 0),
            ("cullingDistanceMax", "keen::float32", 4),
            ("killTimeInSeconds", "keen::uint32", 8),
            ("emitterCount", "keen::uint32", 0xc),
            ("particles", "keen::BlobArray<keen::uint32>", 0x14),
            ("stateInfos", "keen::BlobArray<keen::VfxParticleStateBufferInfo>", 0x1c),
            ("meshes", "keen::BlobArray<keen::uint32>", 0x34),
            ("images", "keen::BlobArray<keen::uint32>", 0x4c),
            ("parameters", "keen::BlobArray<keen::VfxUserEffectParameterDescription>", 0x6c),
            ("defaultParameterData", "keen::uint32", 0x74),
        ];
        t.push(vr);
        let mut ve = rec("keen::VfxNotifierEvent", 0x20, K_STRUCT);
        ve.fields = vec![("vfx", "keen::VfxReference", 0x10)];
        t.push(ve);
        let mut sub = rec("keen::actor::ActorSubSequenceResource", 72, K_STRUCT);
        sub.fields = vec![("events", "keen::BlobArray<keen::uint32>", 0), ("name", "keen::string", 0x14)];
        t.push(sub);
        let mut suba = rec("keen::BlobArray<keen::actor::ActorSubSequenceResource>", 8, K_BLOB_ARRAY);
        suba.inner = Some("keen::actor::ActorSubSequenceResource");
        t.push(suba);
        let mut seq = rec("keen::actor::ActorSequenceResource", 24, K_STRUCT);
        seq.fields = vec![("subSequences", "keen::BlobArray<keen::actor::ActorSubSequenceResource>", 0x10)];
        t.push(seq);
        // the exe's strings hold one parameter's name; a particle's state member gives another
        let mut pe = build_pe(&t);
        pe.extend_from_slice(b"\0radius\0");
        std::fs::write(dir.join("enshrouded.exe"), pe).unwrap();

        let th = |n: &str| fnv1a32(n.as_bytes());
        let tag = |n: u8| fnv1a32(&guid(n));
        // loca: {BlobArray<LocaTagResource>} with ids = FNV of the tag GUIDs 0x50.. and skill name id 0x77
        let texts = [(tag(0x50), "Frost <b>Blade</b>"), (tag(0x51), "Ice Shard"), (tag(0x52), "Keen Edge"), (tag(0x53), "Chill"), (0x77u32, "Double Jump")];
        let mut lb = Blob::new(8);
        let mut recs = vec![0u8; 24 * texts.len()];
        for (i, (id, _)) in texts.iter().enumerate() {
            recs[24 * i..24 * i + 4].copy_from_slice(&id.to_le_bytes());
        }
        lb.array(0, &recs, texts.len() as u32);
        let base = lb.0.len() - recs.len();
        for (i, (_, s)) in texts.iter().enumerate() {
            lb.string(base + 24 * i + 4, s);
        }
        let loca_hash = ContentHash { size: lb.0.len() as u32, h: [1, 2, 3] };
        let mut coll = Blob::new(40);
        let mut lang = vec![0u8; 40];
        lang[8..24].copy_from_slice(&loca_hash.to_bytes());
        coll.array(0x20, &lang, 1);

        let item = |n: u8, id: u32, name_tag: u8, dbg: &str, cat: u8| {
            let mut b = Blob::new(0x40);
            b.u32(0, id).guid(4, guid(n)).u16(0x14, 99).u8(0x16, cat).guid(0x18, guid(name_tag)).string(0x28, dbg);
            b.0
        };
        let perk = |tagn: u8, dbg: &str| {
            let mut b = Blob::new(0x20);
            b.guid(0, guid(tagn)).string(0x10, dbg);
            b.0
        };
        // a template whose components carry VFX: {type hash, offset from +4, size}, the bodies after the list
        let tmpl_vfx = |name: &str, comps: &[(&str, u8)]| {
            let mut b = Blob::new(24);
            b.string(0, name);
            let mut list = vec![0u8; (12 + 16) * comps.len()];
            for (i, (c, vfx)) in comps.iter().enumerate() {
                let at = 12 * i;
                list[at..at + 4].copy_from_slice(&fnv1a32(c.as_bytes()).to_le_bytes());
                let body = 12 * comps.len() + 16 * i;
                list[at + 4..at + 8].copy_from_slice(&((body - (at + 4)) as i32).to_le_bytes());
                list[at + 8..at + 12].copy_from_slice(&16u32.to_le_bytes());
                list[body..body + 16].copy_from_slice(&guid(*vfx));
            }
            b.array(8, &list, comps.len() as u32);
            b.0
        };
        // an actor sequence with one named sub-sequence: its events are [VfxNotifierEvent for each VFX]
        let sequence = |name: &str, vfx: &[u8]| {
            let mut b = Blob::new(24);
            let mut sub = vec![0u8; 72];
            let mut events = vec![0u8; (12 + 0x20) * vfx.len()];
            for (i, v) in vfx.iter().enumerate() {
                let at = 12 * i;
                events[at..at + 4].copy_from_slice(&fnv1a32(b"keen::VfxNotifierEvent").to_le_bytes());
                let body = 12 * vfx.len() + 0x20 * i;
                events[at + 4..at + 8].copy_from_slice(&((body - (at + 4)) as i32).to_le_bytes());
                events[at + 8..at + 12].copy_from_slice(&0x20u32.to_le_bytes());
                events[body + 0x10..body + 0x20].copy_from_slice(&guid(*v));
            }
            sub[0..4].copy_from_slice(&72i32.to_le_bytes());
            sub[4..8].copy_from_slice(&(vfx.len() as u32).to_le_bytes());
            let mut all = sub;
            all.extend_from_slice(&events);
            b.array(0x10, &all, 1);
            let base = b.0.len() - all.len();
            b.string(base + 0x14, name);
            b.0
        };
        // a VFX: parameters {hash, type id, range}, the 64-byte block, a state member's name
        let vfx_res = |kill: u32, params: &[(&str, &str, u8, u8)], block: &[(usize, &[u8])]| {
            let mut b = Blob::new(216);
            b.0[0..4].copy_from_slice(&50f32.to_le_bytes());
            b.0[4..8].copy_from_slice(&75f32.to_le_bytes());
            b.u32(8, kill).u32(0xc, 1);
            for (at, v) in block {
                b.0[0x74 + at..0x74 + at + v.len()].copy_from_slice(v);
            }
            let mut p = vec![0u8; 12 * params.len()];
            for (i, (name, ty, start, count)) in params.iter().enumerate() {
                p[12 * i..12 * i + 4].copy_from_slice(&fnv1a32(name.as_bytes()).to_le_bytes());
                p[12 * i + 4..12 * i + 8].copy_from_slice(&fnv1a32(format!("VfxEffectParameter{ty}").as_bytes()).to_le_bytes());
                p[12 * i + 8] = *start;
                p[12 * i + 9] = *count;
            }
            if !params.is_empty() {
                b.array(0x6c, &p, params.len() as u32);
            }
            // one state info with one member, "lifetime", so that name is known without the exe
            let mut state = vec![0u8; 12 + 16];
            state[0..4].copy_from_slice(&12i32.to_le_bytes());
            state[4..8].copy_from_slice(&1u32.to_le_bytes());
            b.array(0x1c, &state, 1);
            let base = b.0.len() - state.len();
            b.string(base + 12, "lifetime");
            b.0
        };
        let tmpl = |name: &str, comps: &[&str]| {
            let mut b = Blob::new(24);
            b.string(0, name);
            let mut list = vec![0u8; 12 * comps.len()];
            for (i, c) in comps.iter().enumerate() {
                list[12 * i..12 * i + 4].copy_from_slice(&fnv1a32(c.as_bytes()).to_le_bytes());
            }
            if !comps.is_empty() {
                b.array(8, &list, comps.len() as u32);
            }
            b.0
        };
        // three variables of two programs: the same config under two names, a shared name, a C++ config given a type
        let program = |vars: &[(u32, &str, &str)]| {
            let mut list = vec![0u8; 24 * vars.len()];
            for (i, (id, _, ty)) in vars.iter().enumerate() {
                list[24 * i + 4..24 * i + 8].copy_from_slice(&id.to_le_bytes());
                list[24 * i + 8..24 * i + 12].copy_from_slice(&fnv1a32(ty.as_bytes()).to_le_bytes());
            }
            let mut b = Blob::new(64);
            b.array(40, &list, vars.len() as u32);
            let base = b.0.len() - list.len();
            for (i, (_, name, _)) in vars.iter().enumerate() {
                b.string(base + 24 * i + 16, name);
            }
            b.0
        };
        let mut tree = Blob::new(16);
        let mut nodes = vec![0u8; 32];
        nodes[0..4].copy_from_slice(&0x81d8b64fu32.to_le_bytes());
        nodes[4..8].copy_from_slice(&0x77u32.to_le_bytes());
        nodes[8..12].copy_from_slice(&0x1111u32.to_le_bytes());
        nodes[16..20].copy_from_slice(&0x22u32.to_le_bytes());
        tree.array(0, &nodes, 2);
        let mut buffs = Blob::new(32);
        let mut bl = vec![0u8; 24];
        bl[0..4].copy_from_slice(&0xcdbe9c80u32.to_le_bytes());
        bl[8..24].copy_from_slice(&guid(0x53));
        buffs.array(0x18, &bl, 1);
        let res = vec![
            Res { guid: guid(1), type_hash: th("keen::ItemInfo"), part: 0, data: item(1, 0x1111, 0x50, "Test_Frost_Blade", 2) },
            Res { guid: guid(2), type_hash: th("keen::ItemInfo"), part: 0, data: item(2, 0x2222, 0x51, "Test_Ice_Shard", 9) },
            Res { guid: guid(3), type_hash: th("keen::Perk"), part: 0, data: perk(0x52, "Perk_Test_Keen") },
            Res { guid: guid(4), type_hash: th("keen::ecs::TemplateResource"), part: 0, data: tmpl("Enemy_Test_Frostling", &["keen::ecs::Health", "keen::ecs::LifeTime", "keen::ecs::DoNotSave"]) },
            Res { guid: guid(5), type_hash: th("keen::ecs::TemplateResource"), part: 0, data: tmpl("", &[]) },
            Res { guid: guid(6), type_hash: th("keen::SkillTreeResource"), part: 0, data: tree.0.clone() },
            Res { guid: guid(7), type_hash: th("keen::BuffTypeCollectionResource"), part: 0, data: buffs.0.clone() },
            Res { guid: guid(8), type_hash: th("keen::LocaTagCollectionResource"), part: 0, data: coll.0.clone() },
            // 0x61 carried by a template; 0x62 played first by a sequence, named by a label too; 0x63 only a label; 0x64 nothing names
            Res { guid: guid(0x61), type_hash: th("keen::VfxResource"), part: 0, data: vfx_res(10, &[("radius", "Float", 0, 4), ("lifetime", "Float", 4, 4), ("duration", "Float", 8, 4), ("tint", "Color", 12, 4)], &[(0, &2f32.to_le_bytes()), (4, &30f32.to_le_bytes()), (8, &12f32.to_le_bytes()), (12, &[0x7f, 0xd4, 0xff, 0xff])]) },
            Res { guid: guid(0x62), type_hash: th("keen::VfxResource"), part: 0, data: vfx_res(3, &[], &[]) },
            Res { guid: guid(0x63), type_hash: th("keen::VfxResource"), part: 0, data: vfx_res(10, &[], &[]) },
            Res { guid: guid(0x64), type_hash: th("keen::VfxResource"), part: 0, data: vfx_res(0, &[], &[]) },
            Res { guid: guid(0x65), type_hash: th("keen::ecs::TemplateResource"), part: 0, data: tmpl_vfx("Explosion_Test", &[("keen::ecs::VfxComponentResource", 0x61)]) },
            Res { guid: guid(0x66), type_hash: th("keen::ecs::TemplateResource"), part: 0, data: tmpl_vfx("Base_Explosion_Long_Name", &[("keen::ecs::VfxComponentResource", 0x61)]) },
            Res { guid: guid(0x67), type_hash: th("keen::ecs::TemplateResource"), part: 0, data: tmpl_vfx("Prop_Test", &[("keen::ecs::DestroyOnLoot", 0x63)]) },
            Res { guid: guid(0x68), type_hash: th("keen::actor::ActorSequenceResource"), part: 0, data: sequence("Cast_Test", &[0x62, 0x63]) },
            Res {
                guid: guid(9),
                type_hash: th("keen::ImpactProgram"),
                part: 0,
                data: program(&[(0x0d0d_0001, "Blast", "float32"), (0x0d0d_0001, "Blast", "float32"), (0x0d0d_0001, "BlastAlias::1", "float32"), (0x0d0d_0002, "Shove", "impact.PushForce"), (0x0d0d_0003, "Blast", "ecs.TemplateReference")]),
            },
        ];
        // layout hashes as the exe's records have them (0, ItemInfo 0x07009d37): exe and data of one build
        let layouts: Vec<(u32, u32)> = res.iter().map(|r| (r.type_hash, if r.type_hash == th("keen::ItemInfo") { 0x0700_9d37 } else { 0 })).collect();
        build_kfc(dir, "enshrouded", "4242|^/test|2026-11-04T00:00:00Z", &res, &[(loca_hash, lb.0.clone())], &layouts);
    }

    struct Tmp(PathBuf);
    impl Tmp {
        fn new(tag: &str) -> Tmp {
            let p = std::env::temp_dir().join(format!("kiln-catalog-{tag}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&p);
            std::fs::create_dir_all(&p).unwrap();
            Tmp(p)
        }
    }
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn builds_from_a_synthetic_client() {
        let game = Tmp::new("game");
        let out = Tmp::new("out");
        fake_game(&game.0);
        let opts = BuildOptions { icons_dir: Some(out.0.join("no-icons")), types_dump: true, ..Default::default() };
        let info = build(&game.0, &out.0, &opts, &mut crate::task::NoProgress).unwrap();
        assert!(info.built);
        assert_eq!(info.side, "client");
        let c = Catalog::load(&info.path).unwrap();
        eprintln!("problems: {:?}", c.problems);
        assert_eq!(c.game.svn.as_deref(), Some("4242"));
        assert_eq!(c.languages, vec!["en-us"]);
        let blade = c.lookup("items", "Test_Frost_Blade").unwrap();
        assert_eq!(blade.id.as_deref(), Some("0x00001111"));
        assert_eq!(blade.guid.as_deref(), Some(crate::gameart::kfc::guid_text(&guid(1)).as_str()));
        assert_eq!(blade.display_name(), Some("Frost Blade"), "markup removed");
        assert_eq!(blade.get("kind"), Some(&serde_json::json!("Weapons")));
        assert_eq!(blade.get("maxStack"), Some(&serde_json::json!(99)));
        let perk = c.lookup("perks", "Perk_Test_Keen").unwrap();
        assert_eq!(perk.id.as_deref(), Some(hex_id(fnv1a32(&guid(3))).as_str()));
        assert_eq!(perk.display_name(), Some("Keen Edge"));
        assert!(c.lookup("templates", "Enemy_Test_Frostling").is_some());
        let unnamed = crate::gameart::kfc::guid_text(&guid(5));
        assert!(c.lookup("templates", &unnamed).is_some(), "an unnamed template is named by its GUID");
        let node = c.lookup("skills", "skill_81d8b64f").unwrap();
        assert_eq!(node.display_name(), Some("Double Jump"));
        assert_eq!(node.get("item"), Some(&serde_json::json!("Test_Frost_Blade")));
        let buff = c.lookup("buffs", "0xcdbe9c80").unwrap();
        assert_eq!(buff.name.as_deref(), Some("buff_cdbe9c80"));
        assert_eq!(buff.display_name(), Some("Chill"));
        assert!(c.section("loca").iter().any(|e| e.get("text") == Some(&serde_json::json!("Ice Shard"))));
        assert!(c.fields["items"].iter().any(|f| f.path == "maxStackSize" && f.type_name == "uint16"));
        assert!(c.fields["items"].iter().any(|f| f.path == "itemId" && f.writable == Some(false)));
        assert!(c.fields["items"].iter().any(|f| f.path == "category" && f.enum_values.as_ref().is_some_and(|v| v.contains(&"Weapons".to_string()))));
        // the template field list: the component's name first; kinds, enums and identity fields as data packs see them
        let tf = |p: &str| c.fields["templates"].iter().find(|f| f.path == p).unwrap_or_else(|| panic!("no template field {p}"));
        assert_eq!(tf("Health.healthMax").kind.as_deref(), Some("uint32"));
        assert_eq!(tf("LifeTime.lifeTime").kind.as_deref(), Some("time"));
        assert_eq!(tf("LifeTime.spawn").kind.as_deref(), Some("ref"));
        assert_eq!(tf("LifeTime.guid").writable, Some(false));
        assert_eq!(tf("LifeTime.force").kind.as_deref(), Some("enum"));
        assert_eq!(tf("LifeTime.force").enum_numbers, Some(vec![0, 5]));
        assert_eq!(tf("LifeTime.forces").kind.as_deref(), Some("flags"));
        assert_eq!(tf("LifeTime.forces").enum_values.as_deref(), Some(&["None".to_string(), "Big".to_string()][..]));
        assert_eq!(tf("LifeTime.configValues").kind.as_deref(), Some("configs"));
        assert_eq!(tf("LifeTime.configValues.simple[*]").kind.as_deref(), Some("variant"));
        assert_eq!(tf("DoNotSave").kind.as_deref(), Some("struct"), "a component without fields is still listed");
        assert_eq!(c.fields["items"].iter().find(|f| f.path == "debugName").unwrap().writable, Some(false));
        // the config vocabulary: names by use, a shared name, the value type from the programs' variables
        let cfg = |id: &str| c.section("configs").iter().find(|e| e.id.as_deref() == Some(id)).unwrap_or_else(|| panic!("no config {id}"));
        assert_eq!(cfg("0x0d0d0001").name.as_deref(), Some("Blast"));
        assert_eq!(cfg("0x0d0d0001").get("names"), Some(&serde_json::json!(["Blast", "BlastAlias"])));
        assert_eq!(cfg("0x0d0d0001").get("kind"), Some(&serde_json::json!("float32")));
        assert_eq!(cfg("0x0d0d0001").get("shared"), Some(&serde_json::json!(true)));
        assert_eq!(cfg("0x0d0d0001").get("programs"), Some(&serde_json::json!(1)));
        assert_eq!(cfg("0x0d0d0002").get("kind"), Some(&serde_json::json!("enum")));
        assert_eq!(cfg("0x0d0d0002").get("enumNumbers"), Some(&serde_json::json!([0, 5])));
        assert_eq!(cfg("0x0d0d0003").get("kind"), Some(&serde_json::json!("ref")));
        assert_eq!(cfg("0xaf236ece").name.as_deref(), Some("ManaCost"));
        assert_eq!(cfg("0xaf236ece").get("readBy"), Some(&serde_json::json!("C++")));
        // VFX: named from what uses them, the name a run-time lookup takes first
        let by_guid = |n: u8| {
            let g = crate::gameart::kfc::guid_text(&guid(n));
            c.section("vfx").iter().find(|e| e.guid.as_deref() == Some(g.as_str())).unwrap_or_else(|| panic!("no VFX {g}"))
        };
        assert_eq!(c.section("vfx").len(), 4);
        let carried = by_guid(0x61);
        assert_eq!(carried.name.as_deref(), Some("Explosion_Test"), "not the Base_ template");
        assert_eq!(carried.get("namedFrom"), Some(&serde_json::json!("template")));
        assert_eq!(carried.get("byName"), Some(&serde_json::json!(true)));
        assert_eq!(carried.get("names"), Some(&serde_json::json!(["Explosion_Test", "Base_Explosion_Long_Name"])), "every name a run-time lookup takes");
        assert_eq!(carried.get("usedBy"), None);
        let params = carried.get("parameters").unwrap();
        assert_eq!(params[0], serde_json::json!({"name": "radius", "type": "float", "default": 2.0}), "the name from the exe's strings");
        assert_eq!(params[1], serde_json::json!({"name": "lifetime", "type": "float", "default": 30.0}), "the name from a state member");
        assert_eq!(params[2]["hash"], serde_json::json!(hex_id(fnv1a32(b"duration"))), "a name nothing holds stays a hash");
        assert_eq!(params[3]["type"], serde_json::json!("color"));
        assert_eq!(params[3]["default"], serde_json::json!("#7fd4ffff"));
        assert_eq!(carried.get("length"), Some(&serde_json::json!(30.0)), "lifetime beats the kill time; duration's name was not recovered");
        assert_eq!(carried.get("cullDistance"), Some(&serde_json::json!([50.0, 75.0])));
        let played = by_guid(0x62);
        assert_eq!(played.name.as_deref(), Some("Cast_Test"));
        assert_eq!(played.get("namedFrom"), Some(&serde_json::json!("sequence")));
        assert_eq!(played.get("byName"), Some(&serde_json::json!(true)));
        assert_eq!(played.get("length"), Some(&serde_json::json!(3.0)));
        let label = by_guid(0x63);
        assert_eq!(label.name.as_deref(), Some("Prop_Test_DestroyOnLoot"), "a label, and not the shorter Cast_Test, which a run-time lookup answers with another VFX");
        assert_eq!(label.get("namedFrom"), Some(&serde_json::json!("use")));
        assert_eq!(label.get("byName"), None);
        assert_eq!(label.get("usedBy"), Some(&serde_json::json!(["Cast_Test"])));
        let alone = by_guid(0x64);
        assert_eq!(alone.name.as_deref(), Some(format!("Vfx_{}", &crate::gameart::kfc::guid_text(&guid(0x64))[..8]).as_str()));
        assert_eq!(alone.get("namedFrom"), None);
        assert_eq!(alone.get("length"), Some(&serde_json::json!(10.0)), "says nothing: 10 s");
        assert_eq!(search(&c, "Base_Explosion_Long_Name", Some("vfx"), 5).first().and_then(|h| h.name.as_deref()), Some("Explosion_Test"), "search finds a VFX by what else uses it");
        // sections the synthetic game lacks are problems, not failures
        assert!(c.problems.iter().any(|p| p.starts_with("recipes:")));
        // Kiln.Game local types: the dump and Generated.cs
        let dump = info.types_dump.clone().unwrap();
        assert!(dump.starts_with(out.0.join("types")));
        let cs = std::fs::read_to_string(out.0.join("Kiln.Game/Generated.cs")).unwrap();
        assert!(cs.contains("public unsafe struct Health") && cs.contains("[FieldOffset(4)] public uint healthMax;"));
        // current now: a second build is skipped; force rebuilds
        let again = build(&game.0, &out.0, &opts, &mut crate::task::NoProgress).unwrap();
        assert!(!again.built);
        assert_eq!(status(&out.0, Some(&game.0)).unwrap()[0].current, Some(true));
        let forced = build(&game.0, &out.0, &BuildOptions { force: true, ..opts.clone() }, &mut crate::task::NoProgress).unwrap();
        assert!(forced.built);
    }

    #[test]
    fn refuses_the_game_folder_and_a_layout_conflict() {
        let game = Tmp::new("conflict");
        fake_game(&game.0);
        let e = build(&game.0, &game.0.join("catalog"), &BuildOptions::default(), &mut crate::task::NoProgress).unwrap_err();
        assert_eq!(e.code, crate::ErrorCode::Refused);
        // the data's ItemInfo layout differs from the exe's: mid-update
        let th = fnv1a32(b"keen::ItemInfo");
        build_kfc(&game.0, "enshrouded", "4243|x", &[Res { guid: guid(1), type_hash: th, part: 0, data: vec![0; 0x40] }], &[], &[(th, 0xdead_beef)]);
        let out = Tmp::new("conflict-out");
        let e = build(&game.0, &out.0, &BuildOptions::default(), &mut crate::task::NoProgress).unwrap_err();
        assert_eq!(e.code, crate::ErrorCode::Conflict, "{}", e.message);
    }

    #[test]
    fn typegen_port_matches_the_committed_generated_cs() {
        // the committed dump (game-derived, in sigdb/) through the Rust port == the committed Generated.cs
        let repo = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let dump_path = repo.join("sigdb/types/server/c5c9fe50c6398d2adedd98923c2d04bf9d9b4af64c1447ac167cdf8f874ff84f.json");
        let Ok(text) = std::fs::read_to_string(&dump_path) else { return };
        let d: super::super::types::Dump = serde_json::from_str(&text).unwrap();
        let committed = std::fs::read_to_string(repo.join("managed/Kiln.Game/Generated.cs")).unwrap();
        let ours = super::super::types::generate_cs(&d, "sigdb/types/server/c5c9fe50c6398d2adedd98923c2d04bf9d9b4af64c1447ac167cdf8f874ff84f.json");
        assert_eq!(ours, committed);
    }

    /// KILN_CATALOG_GAME=<client or server folder> KILN_CATALOG_OUT=<scratch dir>
    #[test]
    #[ignore]
    fn real_game() {
        let game = PathBuf::from(std::env::var("KILN_CATALOG_GAME").expect("KILN_CATALOG_GAME"));
        let out = PathBuf::from(std::env::var("KILN_CATALOG_OUT").expect("KILN_CATALOG_OUT"));
        let t0 = std::time::Instant::now();
        let opts = BuildOptions { force: true, types_dump: true, ..Default::default() };
        let info = build(&game, &out, &opts, &mut |f: Option<f32>, m: &str| eprintln!("  {:>5.1}% {m}", f.unwrap_or(0.0) * 100.0)).unwrap();
        eprintln!("{}", serde_json::to_string_pretty(&info).unwrap());
        eprintln!("build: {:.2} s, {} bytes", t0.elapsed().as_secs_f64(), info.bytes);
        let t1 = std::time::Instant::now();
        let c = Catalog::load(&info.path).unwrap();
        eprintln!("load: {:.3} s", t1.elapsed().as_secs_f64());
        for q in ["Bronze Sword", "Fireball", "Vukah"] {
            eprintln!("search {q}: {:?}", search(&c, q, None, 5).iter().map(|h| (&h.section, &h.name, &h.display_name)).collect::<Vec<_>>());
        }
    }
}

/// Kiln.Game types from an exe outside a game folder (e.g. the build the committed Generated.cs came from):
/// `KILN_CATALOG_EXE=<exe> KILN_CATALOG_OUT=<scratch> cargo test -p kiln-manage --features gameart --lib --
/// --ignored catalog::tests::real_exe_types --nocapture`; compares with managed/Kiln.Game/Generated.cs.
#[cfg(feature = "gameart")]
#[test]
#[ignore]
fn real_exe_types() {
    let exe = PathBuf::from(std::env::var("KILN_CATALOG_EXE").expect("KILN_CATALOG_EXE"));
    let out = PathBuf::from(std::env::var("KILN_CATALOG_OUT").expect("KILN_CATALOG_OUT"));
    let side = if exe.file_name().is_some_and(|n| n.to_string_lossy().contains("server")) || std::env::var("KILN_CATALOG_SIDE").as_deref() == Ok("server") { "server" } else { "client" };
    let dump = types::from_exe(&exe, side, &out, &types::DEFAULT_COMPONENTS).unwrap();
    eprintln!("dump: {}", dump.display());
    let ours = std::fs::read_to_string(out.join("Kiln.Game/Generated.cs")).unwrap();
    let committed = std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../managed/Kiln.Game/Generated.cs")).unwrap();
    let strip = |s: &str| s.lines().filter(|l| !l.starts_with("// Source:")).collect::<Vec<_>>().join("\n");
    eprintln!("same as the committed Generated.cs (apart from the Source line): {}", strip(&ours) == strip(&committed));
}
