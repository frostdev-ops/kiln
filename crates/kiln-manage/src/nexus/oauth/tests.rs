use super::*;
use super::super::api::{HttpResponse, HttpStream};
use std::sync::atomic::{AtomicUsize, Ordering};

fn config() -> Config { Config::new("kiln-test", "http://127.0.0.1:8089/callback").unwrap() }
fn tokens() -> Tokens { Tokens { client_id: "kiln-test".into(), access_token: "access-secret".into(), refresh_token: "refresh-secret".into(), expires_at: 1 } }
fn store() -> (crate::library::TempDir, TokenStore) {
    let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "oauth-test").unwrap();
    let store = TokenStore::with_backend(tmp.path(), None); (tmp, store)
}
struct Reply { calls: AtomicUsize, status: u16 }
impl Transport for Reply {
    fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
        assert_eq!(req.url, TOKEN_URL);
        assert!(req.headers.iter().all(|(name,_)| !name.eq_ignore_ascii_case("authorization") && !name.eq_ignore_ascii_case("apikey")));
        assert!(!format!("{req:?}").contains("refresh-secret"));
        assert!(!String::from_utf8_lossy(req.body.as_deref().unwrap()).contains("client_secret"));
        self.calls.fetch_add(1, Ordering::Relaxed);
        Ok(HttpResponse { status: self.status, headers: vec![], body: br#"{"access_token":"new-access","refresh_token":"new-refresh","expires_in":21600,"token_type":"Bearer"}"#.to_vec() })
    }
    fn open(&self, _: &str, _: &[(String,String)]) -> Result<HttpStream> { panic!("not a download") }
}
fn login() -> Login {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap(); listener.set_nonblocking(true).unwrap();
    let cfg = Config::new("kiln-test", &format!("http://127.0.0.1:{}/callback", listener.local_addr().unwrap().port())).unwrap();
    Login { config: cfg, verifier: "verifier-secret".into(), state: "state-secret".into(), listener }
}

#[test]
fn pkce_matches_rfc7636_and_no_secret_is_required() {
    assert_eq!(challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"), "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    let l = login(); let url = l.authorize_url();
    assert!(url.starts_with("https://users.nexusmods.com/oauth/authorize?"));
    assert!(url.contains("code_challenge_method=S256"));
    assert!(!url.contains("verifier-secret") && !url.contains("client_secret"));
}

#[test]
fn callback_requires_exact_path_single_parameters_and_matching_state() {
    let l = login();
    assert_eq!(l.callback("/callback?state=state-secret&code=abc%2B123").unwrap(), Some("abc+123".into()));
    for target in ["/callback?state=wrong&code=secret", "/callback?state=state-secret&code=x&code=y", "/callback?state=state-secret&state=state-secret&code=x", "/callback?state=state-secret&code=%GG", "/callback?code=secret"] {
        assert!(l.callback(target).is_err(), "{target}");
    }
    assert!(l.callback("/other?state=state-secret&code=secret").unwrap().is_none());
    assert_eq!(l.callback("/callback?state=state-secret&error=access_denied").unwrap_err().code, ErrorCode::Canceled);
}

#[test]
fn config_never_accepts_remote_or_ambiguous_callback_uris() {
    for uri in ["https://evil.test/callback", "http://localhost:8089/callback", "http://127.0.0.1:0/callback", "http://127.0.0.1:08089/callback", "http://127.0.0.1:+8089/callback", "http://127.0.0.1:8089/callback?x=y", "http://127.0.0.1:8089/a/../callback", "http://127.0.0.1:8089//callback"] {
        assert!(Config::new("kiln", uri).is_err(), "{uri}");
    }
    assert!(Config::new("borrowed id\n", "http://127.0.0.1:8089/callback").is_err());
}

#[test]
fn canceled_listener_closes_without_exchange() {
    let l = login(); let address = l.listener.local_addr().unwrap();
    let reply = Reply { calls: AtomicUsize::new(0), status: 200 };
    assert_eq!(l.wait(&reply, || true).unwrap_err().code, ErrorCode::Canceled);
    assert_eq!(reply.calls.load(Ordering::Relaxed), 0);
    assert!(TcpListener::bind(address).is_ok());
}

#[test]
fn timed_out_listener_closes_without_exchange() {
    let l = login(); let address = l.listener.local_addr().unwrap();
    let reply = Reply { calls: AtomicUsize::new(0), status: 200 };
    assert_eq!(l.wait_timeout(&reply, || false, Duration::ZERO).unwrap_err().code, ErrorCode::Timeout);
    assert_eq!(reply.calls.load(Ordering::Relaxed), 0);
    assert!(TcpListener::bind(address).is_ok());
}

#[test]
fn bound_callback_exchanges_once_and_closes_after_replay() {
    let l = login(); let address = l.listener.local_addr().unwrap();
    let reply = Reply { calls: AtomicUsize::new(0), status: 200 };
    std::thread::scope(|scope| {
        let task = scope.spawn(|| l.wait(&reply, || false).unwrap());
        for (state, host) in [("wrong-state", address.to_string()), ("state-secret", "evil.test".into())] {
            let mut stream = std::net::TcpStream::connect(address).unwrap();
            write!(stream, "GET /callback?state={state}&code=one-code HTTP/1.1\r\nHost: {host}\r\n\r\n").unwrap();
            let mut response = String::new(); stream.read_to_string(&mut response).unwrap();
            assert!(response.contains("400 Bad Request"));
        }
        let mut stream = std::net::TcpStream::connect(address).unwrap();
        write!(stream, "GET /callback?state=state-secret&code=one-code HTTP/1.1\r\nHost: {address}\r\n\r\n").unwrap();
        let mut response = String::new(); stream.read_to_string(&mut response).unwrap();
        assert!(response.contains("200 OK") && !response.contains("one-code"));
        assert_eq!(task.join().unwrap().access_token, "new-access");
    });
    assert_eq!(reply.calls.load(Ordering::Relaxed), 1);
    assert!(std::net::TcpStream::connect(address).is_err());
}

#[test]
fn expired_tokens_refresh_once_for_concurrent_callers_and_are_persisted() {
    let (_tmp, store) = store(); store.set(&tokens()).unwrap();
    let reply = Arc::new(Reply { calls: AtomicUsize::new(0), status: 200 });
    let session = Session::new(config(), reply.clone(), store).unwrap();
    std::thread::scope(|scope| {
        for _ in 0..8 { scope.spawn(|| assert_eq!(session.access_token().unwrap().as_deref(), Some("new-access"))); }
    });
    assert_eq!(reply.calls.load(Ordering::Relaxed), 1);
    assert_eq!(session.store.get().unwrap().unwrap().0.refresh_token, "new-refresh");
    assert!(!format!("{:?}", session.store.get().unwrap()).contains("new-refresh"));
    session.clear().unwrap();
    assert!(session.access_token().unwrap().is_none());
}

#[test]
fn independent_sessions_share_rotation_and_observe_sign_out() {
    let (tmp, store) = store(); store.set(&tokens()).unwrap();
    let reply = Arc::new(Reply { calls: AtomicUsize::new(0), status: 200 });
    let first = Session::new(config(), reply.clone(), store).unwrap();
    let second = Session::new(config(), reply.clone(), TokenStore::with_backend(tmp.path(), None)).unwrap();
    std::thread::scope(|scope| {
        for session in [&first, &second] { scope.spawn(move || assert_eq!(session.access_token().unwrap().as_deref(), Some("new-access"))); }
    });
    assert_eq!(reply.calls.load(Ordering::Relaxed), 1);
    first.clear().unwrap();
    assert!(second.access_token().unwrap().is_none());
    assert!(!second.present());
}

#[test]
fn api_rejection_refreshes_and_retries_once_then_forgets_a_still_rejected_session() {
    use super::super::api::{Client, AppId};
    struct Provider { refreshes: Reply, api_calls: AtomicUsize, final_status: u16 }
    impl Transport for Provider {
        fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
            if req.url == TOKEN_URL { return self.refreshes.send(req); }
            assert_eq!(req.url, "https://api.nexusmods.com/v1/users/validate.json");
            assert!(req.headers.iter().all(|(name,_)| !name.eq_ignore_ascii_case("apikey")));
            let n = self.api_calls.fetch_add(1, Ordering::Relaxed);
            let expected = if n == 0 { "Bearer access-secret" } else { "Bearer new-access" };
            assert!(req.headers.iter().any(|(name, value)| name.eq_ignore_ascii_case("authorization") && value == expected));
            Ok(HttpResponse { status: if n == 0 { 401 } else { self.final_status }, headers: vec![], body: br#"{"name":"Test player","is_premium":true}"#.to_vec() })
        }
        fn open(&self, _: &str, _: &[(String,String)]) -> Result<HttpStream> { panic!("not a download") }
    }
    for final_status in [200, 401] {
        let (_tmp, store) = store(); let mut original = tokens(); original.expires_at = crate::util::now_unix() + 3600; store.set(&original).unwrap();
        let provider = Arc::new(Provider { refreshes: Reply { calls: AtomicUsize::new(0), status: 200 }, api_calls: AtomicUsize::new(0), final_status });
        let session = Arc::new(Session::new(config(), provider.clone(), store).unwrap());
        let client = Client::with_credentials(provider.clone(), session.clone(), AppId::kiln_launcher("test")).without_gap();
        let result = client.validate();
        if final_status == 200 { assert_eq!(result.unwrap().name, "Test player"); }
        else { assert_eq!(result.unwrap_err().code, ErrorCode::AuthFailed); }
        assert_eq!(provider.api_calls.load(Ordering::Relaxed), 2);
        assert_eq!(provider.refreshes.calls.load(Ordering::Relaxed), 1);
        assert_eq!(session.store.get().unwrap().is_some(), final_status == 200);
    }
}

#[test]
fn stale_api_rejection_cannot_erase_a_reconnected_account() {
    use super::super::api::{Client, AppId};
    struct Provider { session: Mutex<std::sync::Weak<Session>>, api_calls: AtomicUsize, refreshes: Reply }
    impl Transport for Provider {
        fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
            if req.url == TOKEN_URL { return self.refreshes.send(req); }
            if self.api_calls.fetch_add(1, Ordering::Relaxed) == 1 {
                let mut replacement = tokens(); replacement.access_token = "reconnected-access".into(); replacement.refresh_token = "reconnected-refresh".into(); replacement.expires_at = crate::util::now_unix() + 3600;
                self.session.lock().unwrap().upgrade().unwrap().save(replacement)?;
            }
            Ok(HttpResponse { status: 401, ..Default::default() })
        }
        fn open(&self, _: &str, _: &[(String,String)]) -> Result<HttpStream> { panic!("not a download") }
    }
    let (_tmp, store) = store(); let mut original = tokens(); original.expires_at = crate::util::now_unix() + 3600; store.set(&original).unwrap();
    let provider = Arc::new(Provider { session: Mutex::new(std::sync::Weak::new()), api_calls: AtomicUsize::new(0), refreshes: Reply { calls: AtomicUsize::new(0), status: 200 } });
    let session = Arc::new(Session::new(config(), provider.clone(), store).unwrap());
    *provider.session.lock().unwrap() = Arc::downgrade(&session);
    let client = Client::with_credentials(provider, session.clone(), AppId::kiln_launcher("test")).without_gap();
    assert_eq!(client.validate().unwrap_err().code, ErrorCode::AuthFailed);
    assert_eq!(session.store.get().unwrap().unwrap().0.access_token, "reconnected-access");
}

#[test]
fn failed_refresh_persistence_forgets_the_rotated_pair_without_file_fallback() {
    use super::super::keystore::SecretBackend;
    struct Broken { value: Mutex<Option<String>> }
    impl SecretBackend for Broken {
        fn get(&self) -> Result<Option<String>> { Ok(self.value.lock().unwrap().clone()) }
        fn set(&self, _: &str) -> Result<()> { Err(Error::new(ErrorCode::Io, "keyring unavailable")) }
        fn clear(&self) -> Result<()> { *self.value.lock().unwrap() = None; Ok(()) }
    }
    let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "oauth-store-error").unwrap();
    let store = TokenStore::with_backend(tmp.path(), Some(Box::new(Broken { value: Mutex::new(Some(serde_json::to_string(&tokens()).unwrap())) })));
    let session = Session::new(config(), Arc::new(Reply { calls: AtomicUsize::new(0), status: 200 }), store).unwrap();
    assert_eq!(session.access_token().unwrap_err().code, ErrorCode::Io);
    assert!(!session.present());
    assert!(session.store.get().unwrap().is_none());
    assert!(!session.store.file_path().exists());
}

#[test]
fn failed_sign_in_persistence_removes_a_partially_written_pair_and_reports_cleanup_failure() {
    use super::super::keystore::SecretBackend;
    struct PartialWrite { value: Mutex<Option<String>>, fail_verify: std::sync::atomic::AtomicBool, fail_clear: bool }
    impl SecretBackend for PartialWrite {
        fn get(&self) -> Result<Option<String>> {
            let value = self.value.lock().unwrap().clone();
            if value.is_some() && self.fail_verify.swap(false, Ordering::Relaxed) { return Err(Error::new(ErrorCode::Io, "keyring verification failed")); }
            Ok(value)
        }
        fn set(&self, secret: &str) -> Result<()> { *self.value.lock().unwrap() = Some(secret.into()); Ok(()) }
        fn clear(&self) -> Result<()> {
            if self.fail_clear { return Err(Error::new(ErrorCode::Io, "keyring cleanup failed")); }
            *self.value.lock().unwrap() = None; Ok(())
        }
    }
    for fail_clear in [false, true] {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "oauth-partial-save").unwrap();
        let store = TokenStore::with_backend(tmp.path(), Some(Box::new(PartialWrite { value: Mutex::new(None), fail_verify: std::sync::atomic::AtomicBool::new(true), fail_clear })));
        let session = Session::new(config(), Arc::new(Reply { calls: AtomicUsize::new(0), status: 200 }), store).unwrap();
        let error = session.save(tokens()).unwrap_err();
        assert_eq!(error.code, ErrorCode::Io);
        assert!(error.message.contains("keyring verification failed"));
        assert!(!format!("{error:?}").contains("access-secret"));
        assert!(!session.present());
        assert!(!session.store.file_path().exists());
        if fail_clear { assert!(error.message.contains("keyring cleanup failed")); }
        else {
            assert!(session.store.get().unwrap().is_none());
            let restarted = Session::new(config(), session.transport.clone(), session.store).unwrap();
            assert!(!restarted.present());
        }
    }
}

#[test]
fn revoked_refresh_logs_out_and_network_failure_keeps_retryable_state() {
    for status in [400, 401, 503] {
        let (_tmp, store) = store(); store.set(&tokens()).unwrap();
        let session = Session::new(config(), Arc::new(Reply { calls: AtomicUsize::new(0), status }), store).unwrap();
        let err = session.access_token().unwrap_err();
        assert_eq!(err.code, if status == 503 { ErrorCode::Network } else { ErrorCode::AuthFailed });
        assert_eq!(session.present(), status == 503);
        assert_eq!(session.store.get().unwrap().is_some(), status == 503);
    }
}

#[test]
fn another_applications_credentials_are_not_used() {
    let (_tmp, store) = store(); let mut t = tokens(); t.client_id = "other-app".into(); store.set(&t).unwrap();
    let session = Session::new(config(), Arc::new(Reply { calls: AtomicUsize::new(0), status: 200 }), store).unwrap();
    assert!(!session.present());
    assert!(session.save(t).is_err());
}
