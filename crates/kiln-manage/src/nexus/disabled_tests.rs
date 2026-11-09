//! Run with KILN_NEXUS_ENABLED=0 and the `disabled_nexus` test filter.
use super::*;
use crate::{ErrorCode, Result};
use std::path::Path;
use std::sync::Arc;

struct Tripwire;
impl api::Transport for Tripwire {
    fn send(&self, _: &api::HttpRequest) -> Result<api::HttpResponse> { panic!("disabled Nexus reached provider") }
    fn open(&self, _: &str, _: &[(String,String)]) -> Result<api::HttpStream> { panic!("disabled Nexus opened download") }
}
impl api::Credentials for Tripwire {
    fn present(&self) -> bool { panic!("disabled Nexus inspected credentials") }
    fn access_token(&self) -> Result<Option<String>> { panic!("disabled Nexus read credentials") }
}
impl keystore::SecretBackend for Tripwire {
    fn get(&self) -> Result<Option<String>> { panic!("disabled Nexus read keyring") }
    fn set(&self, _: &str) -> Result<()> { panic!("disabled Nexus wrote keyring") }
    fn clear(&self) -> Result<()> { panic!("disabled Nexus cleared keyring") }
}
impl handler::HandlerPlatform for Tripwire {
    fn supported(&self) -> bool { panic!("disabled Nexus queried handler") }
    fn current(&self) -> Result<Option<handler::Previous>> { panic!("disabled Nexus queried association") }
    fn is_ours(&self) -> bool { panic!("disabled Nexus queried association") }
    fn install(&self, _: &Path) -> Result<()> { panic!("disabled Nexus registered handler") }
    fn restore(&self, _: Option<&handler::Previous>) -> Result<()> { panic!("disabled Nexus changed handler") }
    fn forward(&self, _: &handler::Previous, _: &str) -> Result<()> { panic!("disabled Nexus forwarded link") }
}
fn unsupported<T>(result: Result<T>) { assert!(matches!(result, Err(e) if e.code == ErrorCode::Unsupported)); }

#[test]
fn disabled_nexus_never_reaches_provider_credentials_or_handler_and_preserves_files() {
    if enabled() { return; }
    let temp = crate::library::TempDir::new(&std::env::temp_dir(), "nexus-disabled").unwrap();
    let root = temp.path().join("untouched");
    let store = keystore::TokenStore::with_backend(&root, Some(Box::new(Tripwire)));
    let tokens = oauth::Tokens { client_id: "fixture".into(), access_token: "fixture-access".into(), refresh_token: "fixture-refresh".into(), expires_at: 1 };
    unsupported(store.get()); unsupported(store.set(&tokens)); unsupported(store.clear());
    unsupported(keystore::write_private(&root.join("direct-write.json"), &tokens));
    unsupported(keystore::retire_legacy(&root));
    let config = oauth::Config::new("fixture", "http://127.0.0.1:8089/callback").unwrap();
    assert!(oauth::Config::registered().unwrap().is_none());
    unsupported(oauth::Login::bind(config.clone()));
    unsupported(oauth::Session::new(config, Arc::new(Tripwire), store));
    let client = api::Client::with_credentials(Arc::new(Tripwire), Arc::new(Tripwire), api::AppId::kiln_launcher("test"));
    assert!(!client.has_credentials());
    let _ = format!("{client:?}");
    unsupported(client.validate()); unsupported(client.search("enshrouded", "endorsements", "hide", None, 10));
    unsupported(client.manifest("https://example.invalid/archive")); unsupported(client.open_download("https://example.invalid/file"));
    let cat = catalog::Catalog::new(root.clone(), client);
    unsupported(cat.installed(&crate::paths::DataDirs::at(root.join("data"))));
    unsupported(cat.check_updates(&crate::paths::DataDirs::at(root.join("data")), true));
    assert!(!handler::status(&root, &Tripwire).supported);
    unsupported(handler::register(&root, &Tripwire, Path::new("never.exe")));
    unsupported(handler::unregister(&root, &Tripwire));
    unsupported(handler::forward(&root, &Tripwire, "nxm://other/mods/1/files/2"));
    unsupported(handler::acquire_instance(&root));
    unsupported(handler::queue_link(&root, "nxm://enshrouded/mods/1/files/2"));
    assert!(handler::take_links(&root).is_empty());
    assert!(!root.exists(), "disabled entrypoints created Nexus data/lock/handler files");
}

// ---------------------------------------------------------------- what older builds left behind
//
// Everything below runs in every build (it names the leftovers, not the Nexus switch): a build with Nexus off must still
// remove the dev.19 personal API key and give back dev.19's nxm registration, and it must do so without touching
// anything of the OAuth slot or another manager's association. Nothing here reaches this machine's keyring,
// mimeapps.list or registry: fakes and temporary XDG folders only.

use std::sync::Mutex;

#[derive(Default)]
struct Ring { value: Mutex<Option<String>>, fail_clear: bool }
impl keystore::SecretBackend for Ring {
    fn get(&self) -> Result<Option<String>> { Ok(self.value.lock().unwrap().clone()) }
    fn set(&self, _: &str) -> Result<()> { panic!("the retired slot is never written") }
    fn clear(&self) -> Result<()> {
        if self.fail_clear { return Err(crate::Error::new(ErrorCode::Io, "locked")); }
        *self.value.lock().unwrap() = None;
        Ok(())
    }
}

#[test]
fn disabled_nexus_retires_the_dev19_personal_key_file_and_keyring_entry() {
    let temp = crate::library::TempDir::new(&std::env::temp_dir(), "nexus-legacy-key").unwrap();
    let root = temp.path().join("nexus");
    // nothing stored: nothing created, nothing reported
    assert!(!keystore::retire_personal_key_in(&root, Some(&Ring::default())).unwrap());
    assert!(!root.exists());
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("apikey.json"), br#"{"key":"fixture-not-a-real-key"}"#).unwrap();
    std::fs::write(root.join("oauth.json"), b"fixture oauth file").unwrap();
    std::fs::write(root.join("handler.json"), b"fixture handler record").unwrap();
    let ring = Ring { value: Mutex::new(Some("fixture-keyring-entry".into())), fail_clear: false };
    assert!(keystore::retire_personal_key_in(&root, Some(&ring)).unwrap());
    assert!(!root.join("apikey.json").exists());
    assert!(ring.value.lock().unwrap().is_none());
    // nothing else of the data folder was touched
    assert_eq!(std::fs::read(root.join("oauth.json")).unwrap(), b"fixture oauth file");
    assert_eq!(std::fs::read(root.join("handler.json")).unwrap(), b"fixture handler record");
    // a second start finds nothing
    assert!(!keystore::retire_personal_key_in(&root, Some(&ring)).unwrap());
    // only the keyring holds one (the file is long gone)
    *ring.value.lock().unwrap() = Some("fixture-keyring-entry".into());
    assert!(keystore::retire_personal_key_in(&root, Some(&ring)).unwrap());
    // no keyring on this system: the file alone
    std::fs::write(root.join("apikey.json"), b"{}").unwrap();
    assert!(keystore::retire_personal_key_in(&root, None).unwrap());
    // a keyring that will not clear is an error, and no error text carries the entry
    let stuck = Ring { value: Mutex::new(Some("fixture-keyring-entry".into())), fail_clear: true };
    let error = keystore::retire_personal_key_in(&root, Some(&stuck)).unwrap_err();
    assert!(!format!("{error:?}").contains("fixture-keyring-entry"));
}

// ---- nxm: a registration dev.19 made

#[derive(Default)]
struct FakeOs {
    current: Mutex<Option<String>>,
    restored: Mutex<Vec<Option<String>>>,
    forwarded: Mutex<Vec<(String, String)>>,
    fail_restore: bool,
}
impl handler::HandlerPlatform for FakeOs {
    fn supported(&self) -> bool { panic!("a build with Nexus off never asks whether it can take the links") }
    fn current(&self) -> Result<Option<handler::Previous>> { panic!("a build with Nexus off never looks for a handler to replace") }
    fn is_ours(&self) -> bool { self.current.lock().unwrap().as_deref() == Some("kiln") }
    fn install(&self, _: &Path) -> Result<()> { panic!("a build with Nexus off never registers") }
    fn restore(&self, previous: Option<&handler::Previous>) -> Result<()> {
        if self.fail_restore { return Err(crate::Error::new(ErrorCode::Io, "xdg-mime failed")); }
        let id = previous.map(|p| match p { handler::Previous::Desktop { id } => id.clone(), handler::Previous::Command { command, .. } => command.clone() });
        self.restored.lock().unwrap().push(id.clone());
        *self.current.lock().unwrap() = id;
        Ok(())
    }
    fn forward(&self, p: &handler::Previous, url: &str) -> Result<()> {
        self.forwarded.lock().unwrap().push((p.label(), url.into()));
        Ok(())
    }
}

fn vortex() -> handler::Previous { handler::Previous::Desktop { id: "com.nexusmods.vortex.desktop".into() } }

/// The state file a dev.19 registration wrote (`register`: format 2, the previous handler, the launcher's path).
fn write_record(root: &Path, previous: Option<handler::Previous>, snapshot: Option<serde_json::Value>) {
    std::fs::create_dir_all(root).unwrap();
    let state = handler::HandlerState { format: 2, registered: true, previous, exe: Some("/apps/Kiln.AppImage".into()),
        registered_at: Some("2026-10-01T00:00:00Z".into()), snapshot };
    std::fs::write(root.join("handler.json"), serde_json::to_vec_pretty(&state).unwrap()).unwrap();
}

#[test]
fn disabled_nexus_gives_back_kilns_own_nxm_registration_and_only_that() {
    let temp = crate::library::TempDir::new(&std::env::temp_dir(), "nexus-leftover").unwrap();
    let root = temp.path().join("nexus");

    // 1. no record: nothing is read, nothing is created, the system is not asked anything
    let os = FakeOs::default();
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::Nothing);
    assert!(!root.exists());
    assert_eq!(handler::recorded_previous(&root), None);

    // 2. Kiln is the handler and Vortex was recorded: Vortex is back, the record is gone, a link can go to Vortex
    write_record(&root, Some(vortex()), None);
    *os.current.lock().unwrap() = Some("kiln".into());
    assert_eq!(handler::recorded_previous(&root), Some(vortex()));
    let given = handler::retire_leftover(&root, &os).unwrap();
    assert_eq!(given, handler::Leftover::GivenBack { previous: Some(vortex()) });
    assert_eq!(os.current.lock().unwrap().as_deref(), Some("com.nexusmods.vortex.desktop"));
    assert!(!root.join("handler.json").exists());
    assert_eq!(handler::forward_to_previous(&os, &vortex(), "nxm://enshrouded/mods/1/files/2?key=k&expires=9").unwrap(), "Vortex");
    assert_eq!(os.forwarded.lock().unwrap()[0].1, "nxm://enshrouded/mods/1/files/2?key=k&expires=9");
    assert!(handler::forward_to_previous(&os, &vortex(), "https://example.invalid/").is_err());
    // the next start finds nothing to do
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::Nothing);
    assert_eq!(os.restored.lock().unwrap().len(), 1);

    // 3. another manager has the links since: Kiln forgets its record and changes nothing in the system
    let os = FakeOs::default();
    write_record(&root, Some(vortex()), None);
    *os.current.lock().unwrap() = Some("org.example.other.desktop".into());
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::Forgotten);
    assert_eq!(os.current.lock().unwrap().as_deref(), Some("org.example.other.desktop"));
    assert!(os.restored.lock().unwrap().is_empty());
    assert!(!root.join("handler.json").exists());

    // 4. Kiln was the only handler: giving it back leaves none, as before Kiln
    let os = FakeOs::default();
    write_record(&root, None, None);
    *os.current.lock().unwrap() = Some("kiln".into());
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::GivenBack { previous: None });
    assert_eq!(*os.current.lock().unwrap(), None);

    // 5. a restore that fails keeps the record (the next start tries again) and the recorded handler stays known
    let os = FakeOs { fail_restore: true, ..Default::default() };
    write_record(&root, Some(vortex()), None);
    *os.current.lock().unwrap() = Some("kiln".into());
    assert!(handler::retire_leftover(&root, &os).is_err());
    assert!(root.join("handler.json").is_file());
    assert_eq!(handler::recorded_previous(&root), Some(vortex()));
    assert_eq!(os.current.lock().unwrap().as_deref(), Some("kiln"));
    std::fs::remove_file(root.join("handler.json")).unwrap();

    // 6. the option was off already, or the record is damaged or from a newer launcher: nothing changes
    let os = FakeOs::default();
    *os.current.lock().unwrap() = Some("kiln".into());
    std::fs::write(root.join("handler.json"), br#"{"format":2,"registered":false}"#).unwrap();
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::Nothing);
    for bytes in [b"{broken".as_slice(), br#"{"format":3,"registered":true}"#.as_slice()] {
        std::fs::write(root.join("handler.json"), bytes).unwrap();
        assert!(handler::retire_leftover(&root, &os).is_err());
        assert_eq!(std::fs::read(root.join("handler.json")).unwrap(), bytes);
        assert_eq!(handler::recorded_previous(&root), None);
    }
    assert!(os.restored.lock().unwrap().is_empty());
    assert_eq!(os.current.lock().unwrap().as_deref(), Some("kiln"));
}

/// The real xdg-mime and the real Linux adapter, in private XDG folders: dev.19's registration (a .desktop file and
/// the nxm default in mimeapps.list) goes back to the manager before Kiln, and everything else in the files stays.
#[cfg(target_os = "linux")]
#[test]
fn disabled_nexus_restores_the_linux_association_dev19_wrote_in_private_xdg_folders() {
    if crate::util::which("xdg-mime").is_none() {
        eprintln!("xdg-mime not installed: skipped");
        return;
    }
    let temp = crate::library::TempDir::new(&std::env::temp_dir(), "nexus-leftover-xdg").unwrap();
    let data = temp.path().join("data");
    let config = temp.path().join("config");
    let root = temp.path().join("nexus");
    std::fs::create_dir_all(data.join("applications")).unwrap();
    std::fs::create_dir_all(&config).unwrap();
    let other = "[Desktop Entry]\nType=Application\nName=Other\nExec=/bin/true --nxm %u\nMimeType=x-scheme-handler/nxm;\n";
    std::fs::write(data.join("applications/othermm.desktop"), other).unwrap();
    let before = "[Default Applications]\nx-scheme-handler/nxm=othermm.desktop\ntext/plain=gedit.desktop\n";
    let env = vec![
        ("XDG_DATA_HOME".to_string(), data.display().to_string()),
        ("XDG_CONFIG_HOME".to_string(), config.display().to_string()),
        ("XDG_DATA_DIRS".to_string(), data.display().to_string()),
        ("XDG_CURRENT_DESKTOP".to_string(), "X-Generic".to_string()),
        ("HOME".to_string(), temp.path().display().to_string()),
    ];
    let os = handler::LinuxMime { data_home: data.clone(), config_home: config.clone(), data_dirs: vec![], env };
    let kiln_entry = "[Desktop Entry]\nType=Application\nName=Kiln Launcher (Nexus Mods links)\nExec=\"/apps/Kiln.AppImage\" --nxm %u\nMimeType=x-scheme-handler/nxm;\n";
    // what dev.19's register left: Kiln's .desktop, Kiln as the nxm default, and the format-2 record with its snapshot
    let register_like_dev19 = || {
        // update-desktop-database's cache from an earlier step would still name the old handler: xdg-mime reads it
        let _ = std::fs::remove_file(data.join("applications/mimeinfo.cache"));
        std::fs::write(data.join("applications").join(handler::DESKTOP_ID), kiln_entry).unwrap();
        std::fs::write(config.join("mimeapps.list"), "[Default Applications]\nx-scheme-handler/nxm=kiln-launcher-nxm.desktop\ntext/plain=gedit.desktop\n").unwrap();
        let snapshot = serde_json::json!({"mime": before, "desktop": null, "path": config.join("mimeapps.list")});
        write_record(&root, Some(handler::Previous::Desktop { id: "othermm.desktop".into() }), Some(snapshot));
    };

    register_like_dev19();
    assert!(handler::HandlerPlatform::is_ours(&os), "the fixture must look like a registration");
    let given = handler::retire_leftover(&root, &os).unwrap();
    assert_eq!(given, handler::Leftover::GivenBack { previous: Some(handler::Previous::Desktop { id: "othermm.desktop".into() }) });
    assert_eq!(std::fs::read_to_string(config.join("mimeapps.list")).unwrap(), before);
    assert!(!data.join("applications").join(handler::DESKTOP_ID).exists());
    assert!(!root.join("handler.json").exists());

    // a format-1 record (no snapshot) gives back the recorded handler by id
    register_like_dev19();
    write_record(&root, Some(handler::Previous::Desktop { id: "othermm.desktop".into() }), None);
    handler::retire_leftover(&root, &os).unwrap();
    let list = std::fs::read_to_string(config.join("mimeapps.list")).unwrap();
    assert!(list.contains("x-scheme-handler/nxm=othermm.desktop") && list.contains("text/plain=gedit.desktop") && !list.contains("kiln-launcher-nxm"), "{list}");

    // the user chose another manager after Kiln: it keeps the links, only the record goes
    std::fs::write(config.join("mimeapps.list"), "[Default Applications]\nx-scheme-handler/nxm=chosen-later.desktop\n").unwrap();
    write_record(&root, Some(handler::Previous::Desktop { id: "othermm.desktop".into() }), None);
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::Forgotten);
    assert_eq!(std::fs::read_to_string(config.join("mimeapps.list")).unwrap(), "[Default Applications]\nx-scheme-handler/nxm=chosen-later.desktop\n");
    assert!(!root.join("handler.json").exists());
}

/// The keyring is asked until one lookup succeeds (found nothing, or removed the entry); then a marker keeps later
/// starts from asking again (no unlock prompt at every start). A failed lookup writes no marker. The file is checked
/// at every start.
#[test]
fn disabled_nexus_personal_key_keyring_is_checked_until_one_lookup_succeeds() {
    struct Locked;
    impl keystore::SecretBackend for Locked {
        fn get(&self) -> Result<Option<String>> { Err(crate::Error::new(ErrorCode::Io, "locked")) }
        fn set(&self, _: &str) -> Result<()> { panic!("never written") }
        fn clear(&self) -> Result<()> { panic!("nothing to clear") }
    }
    let temp = crate::library::TempDir::new(&std::env::temp_dir(), "nexus-legacy-marker").unwrap();
    let root = temp.path().join("nexus");
    let marker = temp.path().join("state/nexus-personal-key-retired");
    // no keyring: no marker
    assert!(!keystore::retire_personal_key_once(&root, &marker, None).unwrap());
    assert!(!marker.exists());
    // a locked keyring: an error, no marker (the next start asks again)
    assert!(keystore::retire_personal_key_once(&root, &marker, Some(&Locked)).is_err());
    assert!(!marker.exists());
    // the entry is removed and the check recorded, without the value
    let ring = Ring { value: Mutex::new(Some("fixture-keyring-entry".into())), fail_clear: false };
    assert!(keystore::retire_personal_key_once(&root, &marker, Some(&ring)).unwrap());
    assert!(!std::fs::read_to_string(&marker).unwrap().contains("fixture"));
    // later starts do not ask the keyring (Locked would fail) but still remove a key file
    assert!(!keystore::retire_personal_key_once(&root, &marker, Some(&Locked)).unwrap());
    std::fs::create_dir_all(&root).unwrap();
    std::fs::write(root.join("apikey.json"), b"{}").unwrap();
    assert!(keystore::retire_personal_key_once(&root, &marker, Some(&Locked)).unwrap());
    assert!(!root.join("apikey.json").exists());
}

/// A record that names Kiln itself, or a saved association that would put Kiln back, never leaves Kiln registered in a
/// build with Nexus off, and a link is never forwarded to Kiln (it would come straight back).
#[test]
fn disabled_nexus_never_restores_or_forwards_to_kiln_itself() {
    let temp = crate::library::TempDir::new(&std::env::temp_dir(), "nexus-leftover-self").unwrap();
    let root = temp.path().join("nexus");
    let kiln_desktop = handler::Previous::Desktop { id: handler::DESKTOP_ID.into() };
    let kiln_command = handler::Previous::Command { command: r#""C:\Apps\Kiln\kiln-launcher.exe" --nxm "%1""#.into(), icon: None, scope: "hkcu".into() };
    for kiln in [&kiln_desktop, &kiln_command] {
        assert!(handler::is_kiln_handler(kiln));
        let os = FakeOs::default();
        assert!(handler::forward_to_previous(&os, kiln, "nxm://enshrouded/mods/1/files/2").is_err());
        assert!(os.forwarded.lock().unwrap().is_empty());
    }
    assert!(!handler::is_kiln_handler(&vortex()));

    // the record names Kiln as the handler before it: the links go to nobody, not back to Kiln
    write_record(&root, Some(kiln_desktop.clone()), None);
    assert_eq!(handler::recorded_previous(&root), None);
    let os = FakeOs { current: Mutex::new(Some("kiln".into())), ..Default::default() };
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::GivenBack { previous: None });
    assert_eq!(*os.current.lock().unwrap(), None);

    // a platform whose saved association puts Kiln back: the recorded handler is restored instead
    struct Sticky(Mutex<Option<String>>);
    impl handler::HandlerPlatform for Sticky {
        fn supported(&self) -> bool { panic!() }
        fn current(&self) -> Result<Option<handler::Previous>> { panic!() }
        fn is_ours(&self) -> bool { self.0.lock().unwrap().as_deref() == Some("kiln") }
        fn install(&self, _: &Path) -> Result<()> { panic!("never registers") }
        fn restore(&self, previous: Option<&handler::Previous>) -> Result<()> {
            *self.0.lock().unwrap() = previous.map(|p| p.label());
            Ok(())
        }
        fn restore_snapshot(&self, _: Option<&handler::Previous>, _: Option<&serde_json::Value>) -> Result<()> {
            *self.0.lock().unwrap() = Some("kiln".into());
            Ok(())
        }
        fn forward(&self, _: &handler::Previous, _: &str) -> Result<()> { panic!() }
    }
    write_record(&root, Some(vortex()), Some(serde_json::json!({"mime": "kiln"})));
    let os = Sticky(Mutex::new(Some("kiln".into())));
    assert_eq!(handler::retire_leftover(&root, &os).unwrap(), handler::Leftover::GivenBack { previous: Some(vortex()) });
    assert_eq!(os.0.lock().unwrap().as_deref(), Some("Vortex"));
}
