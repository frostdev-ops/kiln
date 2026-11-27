// SPDX-License-Identifier: GPL-3.0-or-later
//! Strict shared-v2 dependency observations. Provenance belongs outside the transcript.
use serde_json::{Value, json};

/// A Python 3.8+ to run the comparator: the first of `python3`, `python` and (Windows) `py -3` that runs. On Windows
/// `python3` is often only the Store's alias, which exits 9009 ("Python was not found"), while `python` is the real one.
fn python() -> &'static [&'static str] {
    static PYTHON: std::sync::OnceLock<&'static [&'static str]> = std::sync::OnceLock::new();
    PYTHON.get_or_init(|| {
        let candidates: &[&'static [&'static str]] = &[&["python3"], &["python"], &["py", "-3"]];
        candidates
            .iter()
            .copied()
            .find(|python| {
                std::process::Command::new(python[0])
                    .args(&python[1..])
                    .args(["-I", "-c", "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 3)"])
                    .stdin(std::process::Stdio::null())
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .status()
                    .is_ok_and(|status| status.success())
            })
            .unwrap_or_else(|| {
                panic!("the shared-v2 comparator needs Python 3.8 or newer: none of python3, python or py -3 runs it")
            })
    })
}

/// The comparator's verdict: Ok for identical transcripts, Err(its message) when it refuses them (exit 1). Anything else
/// (no interpreter, a crash, the Store alias) fails the test: a refusal must be the comparator's own.
fn compare(old: &Value, candidate: &Value) -> Result<(), String> {
    use std::{
        io::Write,
        process::{Command, Stdio},
    };
    let python = python();
    let mut process = Command::new(python[0])
        .args(&python[1..])
        .arg("-I")
        .arg(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/m6/eml_dependency/compare.py"
        ))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap_or_else(|error| panic!("{python:?}: {error}"));
    serde_json::to_writer(
        process.stdin.as_mut().unwrap(),
        &json!({"old":old,"candidate":candidate}),
    )
    .unwrap();
    process.stdin.take().unwrap().flush().unwrap();
    let output = process.wait_with_output().unwrap();
    let stderr = String::from_utf8_lossy(&output.stderr).into_owned();
    match output.status.code() {
        Some(0) => Ok(()),
        Some(1) if !stderr.trim().is_empty() => Err(stderr),
        _ => panic!("the comparator did not run ({python:?}: {}): {stderr}", output.status),
    }
}

/// A refusal for this reason (the comparator's own message).
fn refused(result: Result<(), String>, reason: &str) -> bool {
    result.is_err_and(|message| message.contains(reason))
}

/// compare.py's refusal of two transcripts that differ.
const MISMATCH: &str = "Lua-visible differential mismatch";

fn transcript(record: Value) -> Value {
    json!({"version":2,"records":[record]})
}

#[test]
fn comparator_rejects_same_error_userdata_with_different_message() {
    let old = transcript(json!({"seq":1,"label":"error","kind":"call","ok":false,
        "error":{"t":"userdata","name":"?"},"message":"first failure"}));
    let mut candidate = old.clone();
    candidate["records"][0]["message"] = "second failure".into();
    assert!(refused(compare(&old, &candidate), MISMATCH));
    candidate["records"][0]
        .as_object_mut()
        .unwrap()
        .remove("message");
    assert!(refused(compare(&old, &candidate), MISMATCH));
}

#[test]
fn comparator_rejects_nil_arity_type_alias_binary_and_float_changes() {
    let old = transcript(json!({"seq":1,"label":"values","kind":"call","ok":true,
        "values":[{"t":"nil"},{"t":"boolean","v":false},{"t":"integer","v":"1"},
        {"t":"float","v":"8000000000000000"},{"t":"string","v":"\"a\\0b\""}]}));
    let mut mutations = vec![];
    let mut arity = old.clone();
    arity["records"][0]["values"]
        .as_array_mut()
        .unwrap()
        .remove(0);
    mutations.push(arity);
    for (index, value) in [
        (0, json!({"t":"boolean","v":false})),
        (1, json!({"t":"boolean","v":true})),
        (2, json!({"t":"float","v":"3ff0000000000000"})),
        (3, json!({"t":"float","v":"0000000000000000"})),
        (4, json!({"t":"string","v":"\"ab\""})),
    ] {
        let mut changed = old.clone();
        changed["records"][0]["values"][index] = value;
        mutations.push(changed);
    }
    for changed in mutations {
        assert!(refused(compare(&old, &changed), MISMATCH), "{changed}");
    }
    assert_eq!(compare(&old, &old), Ok(()));
}

#[test]
fn production_core_vm_hosts_gpl_fixture_table_and_userdata_in_one_type_universe() {
    use sfi_core::{
        host::{HostContext, Package, PackageSource, Phase, Side, SourceVersion},
        lua::LuaVm,
    };
    use std::{cell::Cell, rc::Rc};
    struct Source;
    impl PackageSource for Source {
        fn read_file(&self, path: &str) -> std::io::Result<Vec<u8>> {
            assert_eq!(path, "src/mod.lua");
            Ok(b"assert(rawequal(fixture.node, fixture.alias)); fixture.node.value=101; return fixture".to_vec())
        }
    }
    // This is GPL test-created userdata, not a replacement Buffer, Type or service.
    struct GplFixture(Rc<Cell<i64>>);
    impl mlua::UserData for GplFixture {
        fn add_fields<F: mlua::UserDataFields<Self>>(fields: &mut F) {
            fields.add_field_method_get("value", |_, this| Ok(this.0.get()));
            fields.add_field_method_set("value", |_, this, value: i64| {
                this.0.set(value);
                Ok(())
            });
        }
    }
    let retained = Rc::new(Cell::new(7));
    {
        let vm = LuaVm::new(HostContext::new(
            Phase::Startup,
            Side::Client,
            SourceVersion::Sf179,
        ))
        .unwrap();
        let lua: &mlua::Lua = vm.lua();
        let table = lua.create_table().unwrap();
        let node = lua.create_userdata(GplFixture(retained.clone())).unwrap();
        table.set("node", node.clone()).unwrap();
        table.set("alias", node.clone()).unwrap();
        vm.add_package(Package::new("gpl-link", Rc::new(Source)), |_, env, _| {
            env.set("fixture", table.clone())
        })
        .unwrap();
        let value: mlua::Value = vm.entrypoint("gpl-link").unwrap();
        let returned = value.as_table().unwrap();
        assert_eq!(returned, &table);
        assert_eq!(returned.get::<mlua::AnyUserData>("node").unwrap(), node);
        assert_eq!(node.borrow::<GplFixture>().unwrap().0.get(), 101);
        assert_eq!(
            vm.require("gpl-link", "mod").unwrap().as_table().unwrap(),
            &table
        );
        lua.gc_collect().unwrap();
        assert!(Rc::strong_count(&retained) > 1);
    }
    assert_eq!(retained.get(), 101);
    assert_eq!(
        Rc::strong_count(&retained),
        1,
        "VM retirement must release the GPL userdata"
    );
}

#[test]
fn production_lock_has_one_exact_lua_universe_and_core_without_runtime_plugin() {
    // Cargo emits these simple ASCII package names/versions. Read the lock instead of
    // resolving unused target packages: this test must not require an unrelated SDK cache.
    let lock =
        std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/../Cargo.lock")).unwrap();
    let versions = |name: &str| -> Vec<String> {
        lock.split("[[package]]")
            .filter(|block| {
                block
                    .lines()
                    .any(|line| line == format!("name = \"{name}\""))
            })
            .map(|block| {
                block
                    .lines()
                    .find_map(|line| {
                        line.strip_prefix("version = \"")
                            .and_then(|v| v.strip_suffix('"'))
                    })
                    .unwrap()
                    .to_owned()
            })
            .collect()
    };
    for (name, version) in [
        ("mlua", "0.11.6"),
        ("mlua-sys", "0.10.0"),
        ("lua-src", "550.0.0"),
    ] {
        assert_eq!(versions(name), [version], "one exact {name} universe");
    }
    assert_eq!(versions("sfi-core"), ["0.1.0"]);
    assert!(versions("kiln-sfi").is_empty());
}

#[test]
fn comparator_refuses_v1_and_fixture_failures() {
    for (value, reason) in [
        (json!({"version":1,"records":[]}), "shared-v2 transcript required"),
        (
            json!({"version":2,"records":[],"fixture_error":{"t":"string","v":"failed"}}),
            "fixture failed; not parity",
        ),
    ] {
        assert!(refused(compare(&value, &value), reason), "{value}");
    }
}
