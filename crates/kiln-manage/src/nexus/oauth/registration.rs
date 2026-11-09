//! Shared, dependency-free registration validation for the runtime and launcher build preflight.
use std::net::{Ipv4Addr, SocketAddrV4};

pub fn validate(client_id: &str, redirect_uri: &str) -> Result<(SocketAddrV4, String), &'static str> {
    if client_id.is_empty() || client_id.len() > 128 || !client_id.bytes().all(|b| b.is_ascii_alphanumeric() || b"-_.".contains(&b)) {
        return Err("Invalid Nexus OAuth client registration.");
    }
    let (port, path) = redirect_uri.strip_prefix("http://127.0.0.1:").and_then(|s| s.split_once('/'))
        .ok_or("Nexus OAuth requires an approved IPv4 loopback callback.")?;
    let port = port.parse::<u16>().ok().filter(|p| *p != 0 && p.to_string() == port)
        .ok_or("Invalid Nexus OAuth callback port.")?;
    if path.is_empty() || path.len() > 128 || !path.bytes().all(|b| b.is_ascii_alphanumeric() || b"-_/.".contains(&b))
        || path.split('/').any(|s| s.is_empty() || s == "." || s == "..") {
        return Err("Invalid Nexus OAuth callback path.");
    }
    Ok((SocketAddrV4::new(Ipv4Addr::LOCALHOST, port), format!("/{path}")))
}

/// Syntax/configuration only. Nexus approval and real-provider verification remain release gates.
pub fn preflight(release: bool, client_id: Option<&str>, redirect_uri: Option<&str>) -> Result<(), &'static str> {
    match (client_id, redirect_uri) {
        (Some(id), Some(uri)) if !id.is_empty() && !uri.is_empty() => validate(id, uri).map(|_| ()),
        _ if release => Err("KILN_RELEASE_BUILD requires Kiln's approved KILN_NEXUS_OAUTH_CLIENT_ID and KILN_NEXUS_OAUTH_REDIRECT_URI; obtain Nexus approval before releasing."),
        _ => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn missing_registration_is_allowed_only_for_development() {
        for (id, uri) in [(None, None), (Some("fixture"), None), (None, Some("http://127.0.0.1:8089/callback")), (Some(""), Some(""))] {
            assert!(preflight(false, id, uri).is_ok());
            assert!(preflight(true, id, uri).unwrap_err().contains("KILN_RELEASE_BUILD requires"));
        }
    }
    #[test]
    fn release_registration_rejects_invalid_complete_configuration() {
        // A synthetic unit-test value tests syntax; it is never supplied to a release build.
        let id = Some("unit-test-registration");
        assert!(preflight(true, id, Some("http://127.0.0.1:8089/callback")).is_ok());
        for uri in ["https://example.test/callback", "http://localhost:8089/callback", "http://127.0.0.1:08089/callback", "http://127.0.0.1:8089/a/../callback", "http://127.0.0.1:8089/callback?code=secret"] {
            assert!(preflight(true, id, Some(uri)).is_err(), "{uri}");
        }
        assert!(preflight(true, Some("invalid\nregistration"), Some("http://127.0.0.1:8089/callback")).is_err());
    }
}
