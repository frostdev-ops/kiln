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
