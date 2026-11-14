//! `kiln publish`, the parts that send nothing: the plan step end to end through the binary, and the refusals that
//! come before any token is read or any transport exists. No test here passes a valid plan with --confirm: that is
//! the path that talks to the provider, and it is only ever run by the mod's author.
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

fn crc32(data: &[u8]) -> u32 {
    let mut c = !0u32;
    for b in data {
        c ^= *b as u32;
        for _ in 0..8 {
            c = if c & 1 != 0 { (c >> 1) ^ 0xEDB8_8320 } else { c >> 1 };
        }
    }
    !c
}

/// A zip of stored (uncompressed) files.
fn write_zip(path: &Path, files: &[(&str, &[u8])]) {
    let (mut out, mut central) = (Vec::new(), Vec::new());
    for (name, data) in files {
        let offset = out.len() as u32;
        let (crc, len, n) = (crc32(data), data.len() as u32, name.len() as u16);
        let mut common = Vec::new();
        common.extend_from_slice(&20u16.to_le_bytes()); // version needed
        common.extend_from_slice(&0x0800u16.to_le_bytes()); // flags: UTF-8 names
        common.extend_from_slice(&0u16.to_le_bytes()); // stored
        common.extend_from_slice(&[0, 0, 0x21, 0]); // time, date (1980-01-01)
        common.extend_from_slice(&crc.to_le_bytes());
        common.extend_from_slice(&len.to_le_bytes());
        common.extend_from_slice(&len.to_le_bytes());
        common.extend_from_slice(&n.to_le_bytes());
        common.extend_from_slice(&0u16.to_le_bytes()); // extra
        out.extend_from_slice(b"PK\x03\x04");
        out.extend_from_slice(&common);
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(data);
        central.extend_from_slice(b"PK\x01\x02");
        central.extend_from_slice(&20u16.to_le_bytes()); // made by
        central.extend_from_slice(&common);
        central.extend_from_slice(&[0u8; 2 + 2 + 2 + 4]); // comment, disk, internal attrs, external attrs
        central.extend_from_slice(&offset.to_le_bytes());
        central.extend_from_slice(name.as_bytes());
    }
    let (start, size, count) = (out.len() as u32, central.len() as u32, files.len() as u16);
    out.extend_from_slice(&central);
    out.extend_from_slice(b"PK\x05\x06\0\0\0\0");
    out.extend_from_slice(&count.to_le_bytes());
    out.extend_from_slice(&count.to_le_bytes());
    out.extend_from_slice(&size.to_le_bytes());
    out.extend_from_slice(&start.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    std::fs::write(path, out).unwrap();
}

struct Fx {
    root: PathBuf,
}
impl Drop for Fx {
    fn drop(&mut self) {
        // frozen files are read-only: make them writable so the folder can go
        fn thaw(p: &Path) {
            if let Ok(rd) = std::fs::read_dir(p) {
                for e in rd.flatten() {
                    let path = e.path();
                    if path.is_dir() {
                        thaw(&path);
                    } else if let Ok(m) = std::fs::metadata(&path) {
                        let mut perm = m.permissions();
                        #[allow(clippy::permissions_set_readonly_false)]
                        perm.set_readonly(false);
                        let _ = std::fs::set_permissions(&path, perm);
                    }
                }
            }
        }
        thaw(&self.root);
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn kiln(f: &Fx, args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_kiln"))
        .current_dir(f.root.join("project"))
        .env("KILN_DATA_DIR", f.root.join("data"))
        .env_remove("KILN_PUBLISH_GITHUB_TOKEN")
        .args(args)
        .output()
        .unwrap()
}

fn json(o: &Output) -> serde_json::Value {
    serde_json::from_slice(&o.stdout).unwrap_or_else(|e| panic!("{e}: {} / {}", String::from_utf8_lossy(&o.stdout), String::from_utf8_lossy(&o.stderr)))
}

#[test]
fn the_plan_step_and_every_refusal_before_the_network() {
    let f = Fx { root: std::env::temp_dir().join(format!("kiln-cli-publish-{}", std::process::id())) };
    let project = f.root.join("project");
    std::fs::create_dir_all(&project).unwrap();
    std::fs::create_dir_all(f.root.join("data")).unwrap();
    let manifest = br#"{"id": "dry-run", "version": "0.3.1", "apiVersion": "0.2", "side": "both"}"#;
    write_zip(&project.join("dry-run-0.3.1.zip"), &[("dry-run/kiln.mod.json", manifest), ("dry-run/blocks/items.json", b"[]")]);
    write_zip(&project.join("leaky.zip"), &[("dry-run/kiln.mod.json", manifest), ("dry-run/.env", b"A=1")]);
    std::fs::write(project.join("notes.md"), "Notes.\n").unwrap();
    std::fs::write(project.join("kiln.publish.json"), r#"{"format": 1, "provider": "github", "github": {"repository": "example/dry-run", "tag": "v{version}", "commit": "0123456789abcdef0123456789abcdef01234567"}, "notes": "notes.md"}"#).unwrap();

    // plan: offline, a file to review
    let o = kiln(&f, &["--json", "publish", "--artifact", "dry-run-0.3.1.zip", "--config", "kiln.publish.json", "--to", "github", "--plan", "plan.json"]);
    assert!(o.status.success(), "{}", String::from_utf8_lossy(&o.stderr));
    let v = json(&o);
    assert_eq!((v["state"].as_str(), v["mod"]["version"].as_str(), v["destination"]["tag"].as_str()), (Some("planned"), Some("0.3.1"), Some("v0.3.1")));
    assert_eq!(v["operations"].as_array().unwrap().len(), 4);
    let plan_text = std::fs::read_to_string(project.join("plan.json")).unwrap();
    let tx = v["txId"].as_str().unwrap().to_string();
    assert!(plan_text.contains(&tx) && !plan_text.contains(f.root.to_str().unwrap()), "the plan holds a path of this PC");
    let dir = f.root.join("data/publish/transactions").join(&tx);
    for name in ["artifact.zip", "config.json", "notes.utf8", "kiln-release.json", "artifact.sha256", "plan.json", "origin.json"] {
        assert!(dir.join(name).is_file(), "{name}");
    }
    assert!(!dir.join("journal.json").exists(), "a plan sends nothing and journals nothing");
    println!("PASS publish.no-network-plan");

    // a plan is never overwritten; a refused zip leaves nothing behind and says which file and why
    let o = kiln(&f, &["--json", "publish", "--artifact", "dry-run-0.3.1.zip", "--to", "github", "--plan", "plan.json"]);
    assert_eq!(json(&o)["error"]["code"], "exists");
    let o = kiln(&f, &["--json", "publish", "--artifact", "leaky.zip", "--to", "github", "--plan", "plan2.json"]);
    let v = json(&o);
    assert_eq!((v["error"]["detail"]["reason"].as_str(), v["error"]["detail"]["member"].as_str()), (Some("secret-file"), Some("dry-run/.env")));
    assert!(!project.join("plan2.json").exists());
    assert_eq!(std::fs::read_dir(f.root.join("data/publish/transactions")).unwrap().count(), 1);
    let o = kiln(&f, &["--json", "publish", "--artifact", "dry-run-0.3.1.zip", "--to", "nexus", "--plan", "plan3.json"]);
    assert!(!o.status.success() && !project.join("plan3.json").exists());

    // sending needs --confirm (usage, exit 2), before anything is loaded
    for args in [&["publish", "--apply", "plan.json"][..], &["publish", "--resume", &tx], &["publish", "--publish-draft", &tx], &["publish", "--resolve-step", "draft", "--remote-id", "1", "--transaction", &tx]] {
        let o = kiln(&f, args);
        assert_eq!(o.status.code(), Some(2), "{args:?}: {}", String::from_utf8_lossy(&o.stderr));
    }
    // a changed plan file, a changed frozen file, a changed project config: refused with --confirm too, before a
    // token is asked for (no journal appears)
    std::fs::write(project.join("edited.json"), plan_text.replace("example/dry-run", "evil/dry-run")).unwrap();
    let o = kiln(&f, &["--json", "publish", "--apply", "edited.json", "--confirm"]);
    assert_eq!(json(&o)["error"]["code"], "hash_mismatch");
    let config = std::fs::read_to_string(project.join("kiln.publish.json")).unwrap();
    std::fs::write(project.join("kiln.publish.json"), config.replace("v{version}", "release-{version}")).unwrap();
    let o = kiln(&f, &["--json", "publish", "--apply", "plan.json", "--confirm"]);
    assert_eq!(json(&o)["error"]["code"], "refused");
    std::fs::write(project.join("kiln.publish.json"), &config).unwrap();
    let notes = dir.join("notes.utf8");
    let mut perm = std::fs::metadata(&notes).unwrap().permissions();
    #[allow(clippy::permissions_set_readonly_false)]
    perm.set_readonly(false);
    std::fs::set_permissions(&notes, perm).unwrap();
    std::fs::write(&notes, "Other notes.\n").unwrap();
    let o = kiln(&f, &["--json", "publish", "--apply", "plan.json", "--confirm"]);
    assert_eq!(json(&o)["error"]["code"], "hash_mismatch");
    let o = kiln(&f, &["--json", "publish", "--resume", &tx, "--confirm"]);
    assert_eq!(json(&o)["error"]["code"], "hash_mismatch");
    assert!(!dir.join("journal.json").exists());
    println!("PASS publish.immutable-artifact");
}

#[cfg(target_os = "linux")]
#[test]
fn storing_a_token_does_not_echo_it_to_the_terminal() {
    use std::io::{Read, Write};
    use std::os::{fd::FromRawFd, unix::fs::PermissionsExt};
    use std::process::Stdio;
    use std::time::{Duration, Instant};
    let f = Fx { root: std::env::temp_dir().join(format!("kiln-cli-token-{}", std::process::id())) };
    std::fs::create_dir_all(&f.root).unwrap();
    let tool = f.root.join("secret-tool");
    std::fs::write(&tool, "#!/bin/sh\ncase \"$1\" in store) /bin/cat > \"$KILN_TEST_TOKEN_FILE\";; *) exit 1;; esac\n").unwrap();
    std::fs::set_permissions(&tool, std::fs::Permissions::from_mode(0o700)).unwrap();
    let captured = f.root.join("captured");
    let (mut master_fd, mut slave_fd) = (-1, -1);
    assert_eq!(unsafe { libc::openpty(&mut master_fd, &mut slave_fd, std::ptr::null_mut(), std::ptr::null(), std::ptr::null()) }, 0);
    let mut master = unsafe { std::fs::File::from_raw_fd(master_fd) };
    let slave = unsafe { std::fs::File::from_raw_fd(slave_fd) };
    let flags = || {
        let mut mode = std::mem::MaybeUninit::<libc::termios>::uninit();
        assert_eq!(unsafe { libc::tcgetattr(slave_fd, mode.as_mut_ptr()) }, 0);
        unsafe { mode.assume_init() }.c_lflag
    };
    let before = flags();
    assert_ne!(before & libc::ECHO, 0);
    let mut child = Command::new(env!("CARGO_BIN_EXE_kiln"))
        .args(["publish", "--store-token", "github"])
        .env("KILN_DATA_DIR", f.root.join("data"))
        .env("PATH", &f.root).env("KILN_TEST_TOKEN_FILE", &captured).env_remove("KILN_PUBLISH_GITHUB_TOKEN")
        .stdin(Stdio::from(slave.try_clone().unwrap())).stdout(Stdio::null())
        .stderr(Stdio::from(std::fs::File::create(f.root.join("stderr")).unwrap())).spawn().unwrap();
    let deadline = Instant::now() + Duration::from_secs(2);
    while flags() & libc::ECHO != 0 && Instant::now() < deadline { std::thread::sleep(Duration::from_millis(10)); }
    const TOKEN: &str = "ghp_PRIVATE_PTY_TEST_TOKEN";
    writeln!(master, "{TOKEN}").unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    let status = loop {
        if let Some(s) = child.try_wait().unwrap() { break s; }
        if Instant::now() >= deadline {
            child.kill().unwrap(); child.wait().unwrap();
            panic!("token command did not finish against the fake credential store");
        }
        std::thread::sleep(Duration::from_millis(10));
    };
    assert!(status.success(), "{}", std::fs::read_to_string(f.root.join("stderr")).unwrap());
    assert_eq!(std::fs::read_to_string(captured).unwrap(), TOKEN);
    assert_eq!(flags(), before, "terminal mode was not restored");
    assert_ne!(unsafe { libc::fcntl(master_fd, libc::F_SETFL, libc::O_NONBLOCK) }, -1);
    let mut echoed = String::new();
    let _ = master.read_to_string(&mut echoed);
    assert!(!echoed.contains(TOKEN), "the token was echoed to the terminal");
}
