//! Regression cases for engine server ownership and untrusted modpack inputs.
#![cfg(all(unix, feature = "server", feature = "zip"))]
use kiln_manage::{ErrorCode, hash, library, modpack};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::server::{control, instance::ServerInstance};
use kiln_manage::task::NoProgress;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp { fn drop(&mut self) { let _ = std::fs::remove_dir_all(&self.0); } }
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("engine-server-review-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}
fn put(root: &Path, rel: &str, bytes: &[u8]) {
    let p = root.join(rel); std::fs::create_dir_all(p.parent().unwrap()).unwrap(); std::fs::write(p, bytes).unwrap();
}
fn instance(path: &Path) -> ServerInstance {
    std::fs::create_dir_all(path.join("kiln")).unwrap();
    serde_json::from_value(serde_json::json!({"id":"t","name":"t","kind":"local","dir":path,"port":27140,"queryPort":27140,
        "wine":null,"policy":"any","source":"launcher","remote":null})).unwrap()
}
fn import(dirs: &DataDirs, src: &Path) -> library::LibraryRef {
    let r = library::import(dirs, &[src.to_path_buf()], &mut NoProgress).unwrap();
    r.imported.into_iter().chain(r.existing).next().unwrap_or_else(|| panic!("{:?}",r.skipped))
}
fn set_mod(root: &Path) { put(root,"init.lua",b"return {}"); }
fn sf_mod(root: &Path, version: &str) {
    put(root,"mod.json",format!(r#"{{"id":"settings","name":"Settings","version":"{version}"}}"#).as_bytes());
    put(root,"extended.mod.json",br#"{"launcher":"SF","enabled":false,"targets":["client","server"],"settings":{"speed":{"control":"slider","min":1,"max":10,"value":3},"simple":true}}"#);
    put(root,"src/mod.lua",b"return {}");
}

#[test]
fn spoofed_source_marker_never_authorizes_engine_removal_or_replacement() {
    let t=tmp("spoof"); let dirs=DataDirs::at(t.0.join("data")); let src=t.0.join("src/owner"); set_mod(&src);
    let r=import(&dirs,&src); let server=t.0.join("server"); let i=instance(&server);
    let own=server.join("shrouded_engine_tweaks/mods/owner"); set_mod(&own); put(&own,"save.txt",b"player data");
    put(&own,".kiln-source.json",serde_json::to_vec(&serde_json::json!({"format":1,"library":r,"synced":"now","by":"kiln-launcher"})).unwrap().as_slice());
    control::sync(&dirs,&i,&[],&mut NoProgress).unwrap();
    assert_eq!(std::fs::read(own.join("save.txt")).unwrap(),b"player data");
    assert_eq!(control::sync(&dirs,&i,&[r],&mut NoProgress).unwrap_err().code,ErrorCode::Conflict);
    assert_eq!(std::fs::read(own.join("save.txt")).unwrap(),b"player data");
}

#[test]
fn player_additions_and_symlinked_roots_are_preserved() {
    let t=tmp("roots"); let dirs=DataDirs::at(t.0.join("data")); let src=t.0.join("src/additions"); set_mod(&src);
    let r=import(&dirs,&src); let server=t.0.join("server"); let i=instance(&server);
    control::sync(&dirs,&i,&[r.clone()],&mut NoProgress).unwrap();
    let copy=server.join("shrouded_engine_tweaks/mods/additions"); put(&copy,".player-save",b"keep");
    control::sync(&dirs,&i,&[],&mut NoProgress).unwrap();
    assert_eq!(std::fs::read(copy.join(".player-save")).unwrap(),b"keep");
    control::set_enabled(&i,"additions",false).unwrap();
    // A modified disabled copy must never cause sync to create a second, enabled copy.
    assert_eq!(control::sync(&dirs,&i,&[r.clone()],&mut NoProgress).unwrap_err().code,ErrorCode::Conflict);
    assert!(!copy.exists());
    assert_eq!(std::fs::read(server.join("kiln/disabled/set/additions/.player-save")).unwrap(),b"keep");
    let server2=t.0.join("server2"); let i2=instance(&server2); let outside=t.0.join("outside"); std::fs::create_dir_all(&outside).unwrap();
    std::os::unix::fs::symlink(&outside,server2.join("shrouded_engine_tweaks")).unwrap();
    assert_eq!(control::sync(&dirs,&i2,&[r],&mut NoProgress).unwrap_err().code,ErrorCode::Refused);
    assert_eq!(std::fs::read_dir(outside).unwrap().count(),0);
}

#[test]
fn engine_kind_collisions_and_ambiguous_toggles_are_refused() {
    let t=tmp("collision"); let dirs=DataDirs::at(t.0.join("data"));
    let sf=t.0.join("src/sf"); sf_mod(&sf,"1.0.0");
    let flame=t.0.join("src/flame"); put(&flame,"mod.json",br#"{"id":"settings","name":"Settings","version":"1.0.0"}"#); put(&flame,"flame.json",br#"{"dll":"x.dll"}"#);
    let a=import(&dirs,&sf); let b=import(&dirs,&flame); assert_eq!(b.kind,ModKind::Flame);
    let server=t.0.join("server"); let i=instance(&server); control::sync(&dirs,&i,&[a.clone()],&mut NoProgress).unwrap();
    assert_eq!(control::sync(&dirs,&i,&[a,b],&mut NoProgress).unwrap_err().code,ErrorCode::Conflict);
    assert!(server.join("mods/settings/extended.mod.json").is_file());
    let set=server.join("shrouded_engine_tweaks/mods/settings"); set_mod(&set);
    assert_eq!(control::set_enabled(&i,"settings",false).unwrap_err().code,ErrorCode::Conflict);
    assert!(set.is_dir() && server.join("mods/settings").is_dir());
    std::fs::remove_dir_all(&set).unwrap();
    put(&server,"kiln/mods/settings/kiln.mod.json",b"{}");
    assert_eq!(control::set_enabled(&i,"settings",false).unwrap_err().code,ErrorCode::Conflict);
    assert!(!server.join("kiln/mods/settings/.disabled").exists());
}

#[test]
fn disabled_updates_stay_disabled_and_shroudforge_keeps_values() {
    let t=tmp("updates"); let dirs=DataDirs::at(t.0.join("data")); let src=t.0.join("src/settings"); sf_mod(&src,"1.0.0");
    let r=import(&dirs,&src); let server=t.0.join("server"); let i=instance(&server);
    // A predictable receipt .tmp link must never be followed by the receipt writer.
    put(&t.0,"sentinel",b"keep"); std::fs::create_dir_all(server.join("kiln/state")).unwrap();
    std::os::unix::fs::symlink(t.0.join("sentinel"),server.join("kiln/state/server-library.json.tmp")).unwrap();
    control::sync(&dirs,&i,&[r],&mut NoProgress).unwrap(); let copy=server.join("mods/settings");
    assert_eq!(std::fs::read(t.0.join("sentinel")).unwrap(),b"keep");
    assert!(kiln_manage::shroudforge::read_mod(&copy).unwrap().enabled,"sync enables the authored package");
    kiln_manage::shroudforge::set_setting(&copy,"speed",&serde_json::json!(7)).unwrap();
    kiln_manage::shroudforge::set_setting(&copy,"simple",&serde_json::json!(false)).unwrap();
    control::set_enabled(&i,"settings",false).unwrap();
    sf_mod(&src,"2.0.0"); let newer=import(&dirs,&src); control::sync(&dirs,&i,&[newer],&mut NoProgress).unwrap();
    assert!(!copy.exists());
    control::set_enabled(&i,"settings",true).unwrap(); let m=kiln_manage::shroudforge::read_mod(&copy).unwrap();
    assert_eq!(m.version,"2.0.0"); assert_eq!(m.settings.iter().find(|s|s.key=="speed").unwrap().value,7);
    assert_eq!(m.settings.iter().find(|s|s.key=="simple").unwrap().value,false);
}

fn pack(path: &Path, entry: &str) -> modpack::ModpackManifest {
    use std::io::Write;
    let hash=hash::dirhash_records([hash::Record{path:"init.lua".into(),size:1,sha256:hash::sha256_hex(b"x")}]).unwrap();
    let manifest:modpack::ModpackManifest=serde_json::from_value(serde_json::json!({"format":1,"name":"P","version":"1","server":{"id":"s"},
        "mods":[{"kind":"set","id":"m","version":"1.0.0","path":"set/m","hash":hash}]})).unwrap();
    let mut w=zip::ZipWriter::new(std::fs::File::create(path).unwrap()); let opts=zip::write::SimpleFileOptions::default();
    w.start_file(modpack::MANIFEST,opts).unwrap();w.write_all(&serde_json::to_vec(&manifest).unwrap()).unwrap();
    w.start_file(entry,opts).unwrap();w.write_all(b"x").unwrap();w.finish().unwrap(); manifest
}

#[test]
fn modpack_extract_revalidates_manifest_and_refuses_windows_streams_and_links() {
    let t=tmp("pack");let zip=t.0.join("pack.zip");let m=pack(&zip,"set/m/init.lua");
    let mut forged=m.clone();forged.mods[0].path="../../escaped".into();
    assert_eq!(modpack::extract(&zip,&forged,&t.0.join("staging")).unwrap_err().code,ErrorCode::HashMismatch);
    let outside=t.0.join("outside");std::fs::create_dir_all(&outside).unwrap();std::os::unix::fs::symlink(&outside,t.0.join("staging")).unwrap();
    assert_eq!(modpack::extract(&zip,&m,&t.0.join("staging")).unwrap_err().code,ErrorCode::Refused);
    assert!(std::fs::read_dir(outside).unwrap().next().is_none());
    pack(&zip,"set/m/init.lua:stream");assert_eq!(modpack::verify(&zip,None).unwrap_err().code,ErrorCode::Refused);
}

#[test]
fn unknown_required_kind_cannot_match_a_kiln_mod() {
    let t=tmp("unknown");let zip=t.0.join("pack.zip");let mut m=pack(&zip,"set/m/init.lua");m.mods[0].kind=ModKind::Kiln;
    let required: Vec<kiln_manage::state::RequiredMod>=serde_json::from_value(serde_json::json!([{"id":"m","version":"1.0.0","hash":m.mods[0].hash,"kind":"future-engine"}])).unwrap();
    assert_eq!(modpack::check_against(&m,&required).unwrap_err().code,ErrorCode::HashMismatch);
}

#[test]
fn modpack_limits_and_mod_versions_are_checked_before_install() {
    use std::io::Write;
    let t=tmp("pack-limits"); let zip=t.0.join("pack.zip"); let mut m=pack(&zip,"set/m/init.lua");
    let mut bytes=std::fs::read(&zip).unwrap();
    let entries:Vec<usize>=bytes.windows(4).enumerate().filter(|(_,x)|*x==b"PK\x01\x02").map(|(i,_)|i).collect();
    let off=entries[1]+24; bytes[off..off+4].copy_from_slice(&((kiln_manage::quick::MAX_IMPORT_FILE+1)as u32).to_le_bytes());
    std::fs::write(&zip,bytes).unwrap();
    assert_eq!(modpack::verify(&zip,None).unwrap_err().code,ErrorCode::Refused);
    m.mods[0].version="../../escape".into();
    let mut w=zip::ZipWriter::new(std::fs::File::create(&zip).unwrap());let o=zip::write::SimpleFileOptions::default();
    w.start_file(modpack::MANIFEST,o).unwrap();w.write_all(&serde_json::to_vec(&m).unwrap()).unwrap();
    w.start_file("set/m/init.lua",o).unwrap();w.write_all(b"x").unwrap();w.finish().unwrap();
    assert_eq!(modpack::verify(&zip,None).unwrap_err().code,ErrorCode::Schema);
}

/// Fedora Atomic / Bazzite: `/home` is a link to `/var/home`. The data dir, the server folder and a modpack's staging
/// folder are reached through it; links inside the managed folders are still refused.
#[test]
fn symlinked_system_ancestors_work_but_links_inside_the_server_folder_do_not() {
    let t=tmp("ancestors"); std::fs::create_dir_all(t.0.join("var/home/user")).unwrap();
    std::os::unix::fs::symlink("var/home",t.0.join("home")).unwrap();
    let dirs=DataDirs::at(t.0.join("home/user/data")); let src=t.0.join("home/user/src/owner"); set_mod(&src);
    let r=import(&dirs,&src); let server=t.0.join("home/user/server"); let i=instance(&server);
    control::sync(&dirs,&i,&[r.clone()],&mut NoProgress).unwrap();
    let copy=server.join("shrouded_engine_tweaks/mods/owner"); assert!(copy.join("init.lua").is_file());
    control::set_enabled(&i,"owner",false).unwrap();
    assert!(server.join("kiln/disabled/set/owner/init.lua").is_file());
    control::set_enabled(&i,"owner",true).unwrap();
    assert!(control::mods(&i).unwrap().iter().any(|m| m.folder=="owner" && m.source.is_some()),"the synced copy is still Kiln's own");
    control::sync(&dirs,&i,&[],&mut NoProgress).unwrap();
    assert!(!copy.exists());
    // a link inside the server folder is refused, whatever lies above it
    let outside=t.0.join("home/user/outside"); std::fs::create_dir_all(&outside).unwrap();
    std::fs::remove_dir_all(server.join("shrouded_engine_tweaks")).unwrap();
    std::os::unix::fs::symlink(&outside,server.join("shrouded_engine_tweaks")).unwrap();
    assert_eq!(control::sync(&dirs,&i,&[r],&mut NoProgress).unwrap_err().code,ErrorCode::Refused);
    assert_eq!(std::fs::read_dir(&outside).unwrap().count(),0);
    // a modpack unpacks into a staging folder behind the link; a link inside the staging folder is refused
    let zip=t.0.join("home/user/pack.zip"); let m=pack(&zip,"set/m/init.lua");
    let staging=t.0.join("home/user/staging");
    assert_eq!(modpack::extract(&zip,&m,&staging).unwrap().len(),1);
    assert!(staging.join("set/m/init.lua").is_file());
    let staging2=t.0.join("home/user/staging2"); std::fs::create_dir_all(&staging2).unwrap();
    std::os::unix::fs::symlink(&outside,staging2.join("set")).unwrap();
    assert_eq!(modpack::extract(&zip,&m,&staging2).unwrap_err().code,ErrorCode::Refused);
    assert_eq!(std::fs::read_dir(&outside).unwrap().count(),0);
}
