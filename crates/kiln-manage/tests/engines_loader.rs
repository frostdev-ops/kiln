//! engines::register_loader: a registered loader takes its Library kind (its own test binary: the registry is
//! process-wide, and the detection tests expect no loader).

use kiln_manage::engines::{self, ImportCtx, Loader};
use kiln_manage::library::{self, DetectKind, Skipped};
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::task::NoProgress;

fn record_skip(ctx: &mut ImportCtx) -> kiln_manage::Result<()> {
    ctx.report.skipped.push(Skipped { path: ctx.detected.path.clone(), reason: "handled by the test loader".into() });
    Ok(())
}

#[test]
fn a_registered_loader_takes_its_kind() {
    let t = std::env::temp_dir().join(format!("km-engines-loader-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&t);
    let d = t.join("greeneye-mod");
    std::fs::create_dir_all(&d).unwrap();
    std::fs::write(d.join("mod.json"), br#"{"id":"find","name":"Find","version":"1.0.0","dll":"find.dll"}"#).unwrap();
    std::fs::write(d.join("find.dll"), build_test_dll(&["GreeneyeModStart"], b"")).unwrap();
    let dirs = DataDirs::at(t.join("data"));
    assert!(engines::register_loader(DetectKind::Greeneye, Loader { name: "test", import: record_skip }).is_none());
    assert!(DetectKind::Greeneye.importable());
    let rep = library::import(&dirs, std::slice::from_ref(&d), &mut NoProgress).unwrap();
    assert_eq!(rep.skipped.iter().map(|s| s.reason.as_str()).collect::<Vec<_>>(), ["handled by the test loader"]);
    let _ = std::fs::remove_dir_all(&t);
}
