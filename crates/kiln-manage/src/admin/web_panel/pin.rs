//! The certificate check: a TLS handshake that accepts exactly the leaf certificate whose SHA-256 the mod wrote to
//! launcher-endpoint.json. It runs through the server's gateway, the same path every browser connection takes.

use crate::{Error, ErrorCode, Result};
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::crypto::{WebPkiSupportedAlgorithms, verify_tls12_signature, verify_tls13_signature};
use rustls::pki_types::{CertificateDer, ServerName, UnixTime};
use rustls::{ClientConfig, ClientConnection, DigitallySignedStruct, SignatureScheme};
use std::net::{SocketAddr, TcpStream};
use std::sync::Arc;
use std::time::Duration;

pub const TIMEOUT: Duration = Duration::from_secs(5);

/// Connects to `addr` and completes a TLS handshake that only `pin`'s certificate passes.
pub fn verify(addr: SocketAddr, pin: &str) -> Result<()> {
    let mut socket = TcpStream::connect_timeout(&addr, TIMEOUT).map_err(|_| Error::new(ErrorCode::AdminUnavailable, "The web panel did not answer"))?;
    socket.set_read_timeout(Some(TIMEOUT))?;
    socket.set_write_timeout(Some(TIMEOUT))?;
    let mut tls = ClientConnection::new(pinned_config(pin)?, ServerName::IpAddress(addr.ip().into()))
        .map_err(|_| Error::new(ErrorCode::Internal, "could not start the web panel's TLS"))?;
    while tls.is_handshaking() {
        tls.complete_io(&mut socket)
            .map_err(|e| Error::new(ErrorCode::Network, format!("the web panel's TLS could not verify the server certificate: {e}")))?;
    }
    Ok(())
}

/// (0.2.0-dev.31.19) A TLS client configuration that accepts exactly the leaf certificate whose SHA-256 is `pin`
/// (lower-case hex), whatever name it is for: the gateway's check above, and the check of the panel's public address
/// ([`super::public`]).
pub(super) fn pinned_config(pin: &str) -> Result<Arc<ClientConfig>> {
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let verifier = Arc::new(PinnedLeaf { pin: pin.to_string(), algorithms: provider.signature_verification_algorithms });
    let config = ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .map_err(|_| Error::new(ErrorCode::Internal, "could not configure the web panel's TLS"))?
        .dangerous()
        .with_custom_certificate_verifier(verifier)
        .with_no_client_auth();
    Ok(Arc::new(config))
}

#[derive(Debug)]
struct PinnedLeaf {
    pin: String,
    algorithms: WebPkiSupportedAlgorithms,
}

impl ServerCertVerifier for PinnedLeaf {
    fn verify_server_cert(&self, end_entity: &CertificateDer<'_>, _: &[CertificateDer<'_>], _: &ServerName<'_>, _: &[u8], _: UnixTime) -> std::result::Result<ServerCertVerified, rustls::Error> {
        if crate::hash::sha256_hex(end_entity.as_ref()) == self.pin {
            Ok(ServerCertVerified::assertion())
        } else {
            Err(rustls::Error::InvalidCertificate(rustls::CertificateError::ApplicationVerificationFailure))
        }
    }

    fn verify_tls12_signature(&self, msg: &[u8], cert: &CertificateDer<'_>, dss: &DigitallySignedStruct) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls12_signature(msg, cert, dss, &self.algorithms)
    }

    fn verify_tls13_signature(&self, msg: &[u8], cert: &CertificateDer<'_>, dss: &DigitallySignedStruct) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls13_signature(msg, cert, dss, &self.algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.algorithms.supported_schemes()
    }
}
