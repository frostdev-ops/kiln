//! The rules every host request passes before it is sent: what a URL may look like, which hosts a provider may
//! reach, which of them may see a credential, and which addresses a name may resolve to.
//!
//! Nothing here opens a socket. [`super::Client`] applies the URL and host rules; a [`super::Transport`] applies
//! [`vet_addresses`] to what it resolved, before it connects.

use crate::{Error, ErrorCode, Result};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};

/// The longest URL accepted (bytes).
pub const MAX_URL: usize = 16 * 1024;

/// A URL that passed [`parse_url`]: https, port 443, an ASCII DNS name.
#[derive(Clone, PartialEq, Eq)]
pub struct Url {
    /// Lower case, no trailing dot.
    pub host: String,
    /// Starts with `/`; holds the query when there is one, exactly as given (signed parameters are not rewritten).
    pub path_and_query: String,
}

impl Url {
    /// `https://host`.
    pub fn origin(&self) -> String {
        format!("https://{}", self.host)
    }

    /// The whole URL, query included. For the wire only: never for a message, a log or a journal.
    pub fn wire(&self) -> String {
        format!("https://{}{}", self.host, self.path_and_query)
    }

    /// `https://host/path`: the form for messages and logs (a query can carry a signature or a token).
    pub fn redacted(&self) -> String {
        let path = self.path_and_query.split('?').next().unwrap_or("/");
        format!("https://{}{}", self.host, path)
    }
}

impl std::fmt::Debug for Url {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.redacted())
    }
}

fn bad_url(why: &str) -> Error {
    // The URL itself is never echoed: it may be a signed one.
    Error::new(ErrorCode::Refused, format!("The address was refused: {why}."))
}

/// Parses a URL once, strictly: `https://`, an ASCII DNS host name of at least two labels, port 443 (given or
/// implied), no user info, no fragment, no IP literal, no `localhost`, no trailing dot, printable ASCII only, at
/// most [`MAX_URL`] bytes. Names with non-ASCII characters are refused, not converted: give the `xn--` form.
pub fn parse_url(url: &str) -> Result<Url> {
    if url.len() > MAX_URL {
        return Err(bad_url("it is too long"));
    }
    if !url.bytes().all(|b| (0x21..=0x7e).contains(&b)) {
        return Err(bad_url("it has a space, a control character or a non-ASCII character"));
    }
    if url.contains('#') {
        return Err(bad_url("it has a fragment"));
    }
    if url.contains('\\') {
        return Err(bad_url("it has a backslash"));
    }
    let rest = url.strip_prefix("https://").ok_or_else(|| bad_url("it is not an https address"))?;
    let end = rest.find(['/', '?']).unwrap_or(rest.len());
    let (authority, tail) = rest.split_at(end);
    if authority.contains('@') {
        return Err(bad_url("it carries user information"));
    }
    if authority.contains('[') || authority.contains(']') {
        return Err(bad_url("it names an IP address"));
    }
    let host = match authority.split_once(':') {
        None => authority,
        Some((h, "443")) => h,
        Some(_) => return Err(bad_url("only port 443 is allowed")),
    };
    let host = host.to_ascii_lowercase();
    check_host(&host)?;
    let path_and_query = if tail.is_empty() {
        "/".to_string()
    } else if tail.starts_with('?') {
        format!("/{tail}")
    } else {
        tail.to_string()
    };
    Ok(Url { host, path_and_query })
}

fn check_host(host: &str) -> Result<()> {
    if host.is_empty() || host.len() > 253 {
        return Err(bad_url("the host name is empty or too long"));
    }
    if host.ends_with('.') {
        return Err(bad_url("the host name ends with a dot"));
    }
    let labels: Vec<&str> = host.split('.').collect();
    if labels.len() < 2 {
        return Err(bad_url("the host name has a single label"));
    }
    for l in &labels {
        let ok = !l.is_empty()
            && l.len() <= 63
            && l.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
            && !l.starts_with('-')
            && !l.ends_with('-');
        if !ok {
            return Err(bad_url("the host name is not a DNS name"));
        }
    }
    let last = labels[labels.len() - 1];
    // A numeric last label is how every IPv4 spelling ends (127.0.0.1, 127.1, 0x7f.0.0.1 has a hex first label
    // but still a numeric last one); no public top-level domain is numeric.
    if last.bytes().all(|b| b.is_ascii_digit()) || last.starts_with("0x") {
        return Err(bad_url("it names an IP address"));
    }
    if matches!(last, "localhost" | "local" | "internal" | "lan" | "home" | "localdomain") {
        return Err(bad_url("the host name is a local one"));
    }
    Ok(())
}

/// One host a provider may reach.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HostRule {
    pub host: String,
    /// Whether a credential header may be sent to this host.
    pub credentials: bool,
}

/// The fixed hosts of one provider. A request to any other host is refused; so is a credential header to a host
/// that is not marked for it. Hosts come from code, never from a config file, mod metadata or user input.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Policy {
    pub provider: String,
    pub hosts: Vec<HostRule>,
}

impl Policy {
    pub fn new(provider: &str) -> Policy {
        Policy { provider: provider.to_string(), hosts: Vec::new() }
    }

    /// Adds a host that may receive this provider's credential.
    pub fn credential_host(mut self, host: &str) -> Policy {
        self.hosts.push(HostRule { host: host.to_ascii_lowercase(), credentials: true });
        self
    }

    /// Adds a host that is reached without a credential.
    pub fn plain_host(mut self, host: &str) -> Policy {
        self.hosts.push(HostRule { host: host.to_ascii_lowercase(), credentials: false });
        self
    }

    pub fn rule(&self, host: &str) -> Option<&HostRule> {
        self.hosts.iter().find(|r| r.host == host)
    }
}

/// Header names that carry a credential or session state. They go only to a host marked `credentials`, and are
/// never carried over a redirect.
pub fn is_credential_header(name: &str) -> bool {
    const NAMES: [&str; 8] =
        ["authorization", "proxy-authorization", "cookie", "apikey", "x-api-key", "x-github-token", "private-token", "x-auth-token"];
    NAMES.iter().any(|n| name.eq_ignore_ascii_case(n))
}

/// Headers the client never lets a caller set: they would leak where the user came from, or change who is asked.
pub fn is_forbidden_header(name: &str) -> bool {
    const NAMES: [&str; 6] = ["host", "referer", "origin", "forwarded", "x-forwarded-for", "via"];
    NAMES.iter().any(|n| name.eq_ignore_ascii_case(n))
}

/// Whether an address is a public one. Loopback, private, link-local, CGNAT, multicast, reserved, documentation
/// and unspecified addresses are not; an IPv4 address inside IPv6 (mapped, compatible, 6to4, NAT64, Teredo) is
/// judged by the IPv4 address it carries.
pub fn is_global(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => global_v4(v4),
        IpAddr::V6(v6) => global_v6(v6),
    }
}

fn global_v4(ip: Ipv4Addr) -> bool {
    let o = ip.octets();
    let n = u32::from(ip);
    !(o[0] == 0                                   // 0.0.0.0/8 "this network"
        || o[0] == 10                             // private
        || (o[0] == 100 && (o[1] & 0xc0) == 64)   // 100.64.0.0/10 CGNAT
        || o[0] == 127                            // loopback
        || (o[0] == 169 && o[1] == 254)           // link-local
        || (o[0] == 172 && (o[1] & 0xf0) == 16)   // private
        || (o[0] == 192 && o[1] == 0 && o[2] == 0) // 192.0.0.0/24 protocol assignments
        || (o[0] == 192 && o[1] == 0 && o[2] == 2) // documentation
        || (o[0] == 192 && o[1] == 88 && o[2] == 99) // 6to4 relay anycast
        || (o[0] == 192 && o[1] == 168)           // private
        || (o[0] == 198 && (o[1] & 0xfe) == 18)   // benchmarking
        || (o[0] == 198 && o[1] == 51 && o[2] == 100) // documentation
        || (o[0] == 203 && o[1] == 0 && o[2] == 113) // documentation
        || o[0] >= 224                            // multicast, reserved, broadcast
        || n == 0xffff_ffff)
}

fn global_v6(ip: Ipv6Addr) -> bool {
    let s = ip.segments();
    // IPv4 carried inside IPv6: judge the IPv4 address.
    if let Some(v4) = ip.to_ipv4_mapped() {
        return global_v4(v4);
    }
    if s[..6] == [0, 0, 0, 0, 0, 0] {
        // ::, ::1 and the deprecated IPv4-compatible form ::a.b.c.d
        return false;
    }
    if s[0] == 0x64 && s[1] == 0xff9b {
        // NAT64 (64:ff9b::/96 and the local-use 64:ff9b:1::/48): reaches an IPv4 address we cannot vet as global
        // unless it is the well-known prefix with a global IPv4.
        if s[1..6] == [0xff9b, 0, 0, 0, 0] {
            return global_v4(Ipv4Addr::new((s[6] >> 8) as u8, s[6] as u8, (s[7] >> 8) as u8, s[7] as u8));
        }
        return false;
    }
    if s[0] == 0x2002 {
        // 6to4: the IPv4 address is in bits 16..48.
        return global_v4(Ipv4Addr::new((s[1] >> 8) as u8, s[1] as u8, (s[2] >> 8) as u8, s[2] as u8));
    }
    if s[0] == 0x2001 && s[1] == 0 {
        // Teredo
        return false;
    }
    if s[0] == 0x2001 && s[1] == 0xdb8 {
        // documentation
        return false;
    }
    // Fail closed for the IETF special-purpose block (including benchmarking and ORCHID), and the additional
    // documentation prefix. Membership in 2000::/3 alone does not make either range public unicast.
    if (s[0] == 0x2001 && s[1] < 0x200) || (s[0] == 0x3fff && s[1] < 0x1000) { return false; }
    // Only 2000::/3 is global unicast. This drops fc00::/7 (unique local), fe80::/10 (link-local), ff00::/8
    // (multicast), 100::/64 (discard) and everything unassigned.
    (s[0] & 0xe000) == 0x2000
}

/// What a transport does with the addresses a host name resolved to, before it connects: every one of them has to
/// be public, or the request is refused (one private address among public ones is how a name is turned against the
/// local network). Returns the addresses to connect to. The host is named in the error, the addresses are not.
pub fn vet_addresses(host: &str, addrs: &[IpAddr]) -> Result<Vec<IpAddr>> {
    if addrs.is_empty() {
        return Err(Error::new(ErrorCode::Network, format!("{host} could not be found.")));
    }
    if addrs.iter().any(|a| !is_global(*a)) {
        return Err(Error::new(ErrorCode::Refused, format!("{host} resolves to an address that is not a public one. Nothing was sent.")));
    }
    Ok(addrs.to_vec())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ip(s: &str) -> IpAddr {
        s.parse().unwrap()
    }

    #[test]
    fn urls_are_https_dns_names_on_443() {
        let u = parse_url("https://API.github.com/repos/a/b?x=1").unwrap();
        assert_eq!(u.host, "api.github.com");
        assert_eq!(u.path_and_query, "/repos/a/b?x=1");
        assert_eq!(u.origin(), "https://api.github.com");
        assert_eq!(parse_url("https://api.github.com:443").unwrap().path_and_query, "/");
        assert_eq!(parse_url("https://api.github.com?x=1").unwrap().path_and_query, "/?x=1");
        for bad in [
            "http://api.github.com/",
            "https://api.github.com:8443/",
            "https://user@api.github.com/",
            "https://user:pw@api.github.com/",
            "https://api.github.com/#frag",
            "https://127.0.0.1/",
            "https://127.1/",
            "https://2130706433/",
            "https://0x7f.0.0.1/",
            "https://0x7f000001/",
            "https://[::1]/",
            "https://[::ffff:10.0.0.1]/",
            "https://localhost/",
            "https://foo.localhost/",
            "https://printer.local/",
            "https://api.github.com./",
            "https://api..github.com/",
            "https://-api.github.com/",
            "https://api.github.com/a b",
            "https://api.github.com/\u{e9}",
            "https://b\u{fc}cher.example/",
            "https://api.github.com\\@evil.example/",
            "https://api.github.com/a\\b",
            "https:///path",
            "ftp://api.github.com/",
            "//api.github.com/",
            "api.github.com",
        ] {
            assert!(parse_url(bad).is_err(), "accepted {bad}");
        }
        let long = format!("https://api.github.com/{}", "a".repeat(MAX_URL));
        assert!(parse_url(&long).is_err());
    }

    #[test]
    fn a_url_never_prints_its_query() {
        let u = parse_url("https://storage.example.com/upload/9?X-Amz-Signature=SECRETSIG&token=SECRETTOKEN").unwrap();
        let shown = format!("{u:?} {}", u.redacted());
        assert!(!shown.contains("SECRET") && !shown.contains('?'), "{shown}");
        assert!(u.wire().ends_with("token=SECRETTOKEN"));
        // a refusal does not echo the URL either
        let e = parse_url("http://storage.example.com/x?sig=SECRETSIG").unwrap_err();
        assert!(!format!("{e:?} {}", e.message).contains("SECRETSIG"));
    }

    #[test]
    fn private_and_special_addresses_are_not_global() {
        for a in [
            "0.0.0.0", "0.1.2.3", "10.0.0.1", "10.255.255.255", "100.64.0.1", "100.127.255.255", "127.0.0.1", "127.8.8.8",
            "169.254.169.254", "172.16.0.1", "172.31.255.255", "192.0.0.1", "192.0.2.1", "192.88.99.1", "192.168.1.1",
            "198.18.0.1", "198.19.255.255", "198.51.100.7", "203.0.113.9", "224.0.0.1", "239.255.255.255", "240.0.0.1",
            "255.255.255.255",
            "::", "::1", "::ffff:10.0.0.1", "::ffff:127.0.0.1", "::ffff:169.254.169.254", "::10.0.0.1", "::8.8.8.8",
            "64:ff9b::10.0.0.1", "64:ff9b:1::1", "2002:0a00:0001::1", "2002:7f00:0001::1", "2001:0:4136:e378::1",
            "2001:db8::1", "fc00::1", "fd12:3456::1", "fe80::1", "ff02::1", "100::1", "4000::1",
        ] {
            assert!(!is_global(ip(a)), "{a} counted as public");
        }
        for a in [
            "1.1.1.1", "8.8.8.8", "100.63.255.255", "100.128.0.1", "140.82.112.5", "172.15.0.1", "172.32.0.1", "192.0.1.1",
            "198.17.0.1", "198.20.0.1", "223.255.255.254", "::ffff:8.8.8.8", "64:ff9b::8.8.8.8", "2002:0808:0808::1",
            "2606:50c0:8000::154", "2a00:1450:4001::1",
        ] {
            assert!(is_global(ip(a)), "{a} counted as not public");
        }
    }

    #[test]
    fn one_private_address_refuses_the_whole_name() {
        assert_eq!(vet_addresses("a.example.com", &[ip("8.8.8.8"), ip("1.1.1.1")]).unwrap().len(), 2);
        let e = vet_addresses("a.example.com", &[ip("8.8.8.8"), ip("10.0.0.5")]).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert!(!e.message.contains("10.0.0.5"));
        assert_eq!(vet_addresses("a.example.com", &[ip("::ffff:192.168.0.1")]).unwrap_err().code, ErrorCode::Refused);
        assert_eq!(vet_addresses("a.example.com", &[]).unwrap_err().code, ErrorCode::Network);
    }

    #[test]
    fn credential_headers_are_known_in_any_case() {
        assert!(is_credential_header("Authorization") && is_credential_header("COOKIE") && is_credential_header("apikey"));
        assert!(!is_credential_header("Accept") && !is_credential_header("Content-Type"));
        assert!(is_forbidden_header("Referer") && is_forbidden_header("host") && !is_forbidden_header("accept"));
    }

    #[test]
    fn ipv6_protocol_and_documentation_ranges_are_not_public() {
        for a in ["2001:2::1", "2001:10::1", "2001:20::1", "3fff::1", "3fff:fff::1"] {
            assert!(!is_global(ip(a)), "{a} is special-purpose, not public unicast");
        }
        assert!(is_global(ip("2001:4860:4860::8888")));
    }
}
