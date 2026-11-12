//! PE files: exports, and what a `dinput8.dll` is. Markers (contracts.md, "Proxy classification"; the proxy uses the
//! same list for its refusals):
//! - Kiln: the export `KilnProxyInfo` (`KilnProxyInfoData.abi` = 2), or the ASCII text
//!   `KilnProxy/2 <semver>`, or the UTF-16LE text `\kiln\kiln-core.dll` (older proxies, abi 1).
//! - EMF's loader: the exports `emf_loader_identity` / `emf_loader_play_once`, or its marker text
//!   `EMF-LOADER-7d3c9f`. Chained. Checked before ShroudForge: EMF 0.4 and later also export
//!   `ShroudforgeSetDinput8Provider` (EMF's loader can host ShroudForge's chain).
//! - ShroudForge: ASCII `shroudforge/shroudforge-runtime.dll` or `shroudforge/config/modloader-config.json`, or the
//!   export `ShroudforgeSetDinput8Provider`. Refused by the installer unless the user switches; never chained.
//! - Every other engine Kiln knows (EML, SET, Enshwick, Greeneye, Shroudtopia, Flame, shroudlink, Ember, ModLoader
//!   Proxy, Ultimate ASI Loader, ...): its own kind, from `engines::classify_markers`. Chained when it holds
//!   `dinput8.dll`; left in place under any other name.
//! - Anything else that is a 64-bit PE DLL: Unknown (chained). Not a 64-bit PE DLL: NotDll.
//!
//! The checks run in that order (a file with Kiln's markers is Kiln's, whatever else it has).

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;

pub const KILN_EXPORT: &str = "KilnProxyInfo";
pub const KILN_MARKER_ASCII: &str = "KilnProxy/2 ";
pub const KILN_MARKER_UTF16: &str = "\\kiln\\kiln-core.dll";
pub const SHROUDFORGE_MARKERS: [&str; 2] = ["shroudforge/shroudforge-runtime.dll", "shroudforge/config/modloader-config.json"];
pub const SHROUDFORGE_EXPORT: &str = "ShroudforgeSetDinput8Provider";
pub const EMF_EXPORTS: [&str; 2] = ["emf_loader_identity", "emf_loader_play_once"];
/// The text EMF's loader exports as `emf_loader_identity` (EMF `src/loader/loader_id.h`).
pub const EMF_LOADER_MARKER: &str = "EMF-LOADER-7d3c9f";
/// Wine writes this at offset 0x40 of its builtin PE DLLs (the proxy refuses them as `system`).
pub const WINE_BUILTIN_MARKER: &str = "Wine builtin DLL";
/// Exported by a Kiln plugin DLL (library import detection).
pub const KILN_PLUGIN_EXPORT: &str = "kiln_plugin_main";
/// Exported by an EMF mod DLL.
pub const EMF_MOD_EXPORT: &str = "emf_mod_load";
/// Exported by every dinput8 proxy.
pub const DINPUT8_EXPORT: &str = "DirectInput8Create";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ProxyKind {
    /// No file.
    None,
    Kiln,
    ShroudForge,
    Emf,
    /// EML's loader (dbghelp.dll, or dinput8.dll as its fallback).
    Eml,
    /// Shrouded Engine Tweaks (powrprof.dll).
    Set,
    /// Enshwick's stub (powrprof.dll).
    Enshwick,
    /// Greeneye Loader (dbgcore.dll, umpdc.dll, cfgmgr32.dll).
    Greeneye,
    /// Shroudtopia or its Shroudweave fork (winmm.dll).
    Shroudtopia,
    /// Flame, Creative Mode's loader (dinput8.dll; powrprof.dll on servers).
    Flame,
    /// shroudlink (steam_api64.dll on servers, dinput8.dll client addon).
    Shroudlink,
    /// Ember's server loader (powrprof.dll, iphlpapi.dll, dbghelp.dll).
    Ember,
    /// ModLoader Proxy (chainloader.ini).
    ModLoaderProxy,
    /// Ultimate ASI Loader (.asi plugins; runs only under its own proxy names).
    UltimateAsiLoader,
    /// Aldrich Mod Menu.
    Aldrich,
    /// Live Map, a stand-alone server mod (iphlpapi.dll).
    LiveMap,
    /// Shared XP, a stand-alone server mod (powrprof.dll).
    SharedXp,
    /// ReShade (dxgi.dll).
    ReShade,
    /// A loader that reads dinput8-chain.ini (XHL-Core, Auto Loot Plus and others).
    ChainLoader,
    /// Some other DLL mod or proxy.
    Unknown,
    /// Not a 64-bit PE DLL.
    NotDll,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProxyClass {
    pub kind: ProxyKind,
    /// Kiln: the proxy's version (KilnProxyInfo / marker), when known.
    pub version: Option<String>,
    /// Kiln: KilnProxyInfoData.abi (1 for older proxies).
    pub abi: Option<u32>,
    pub sha256: Option<String>,
    pub size: Option<u64>,
    /// Which marker matched, or why it is Unknown / NotDll.
    pub detail: Option<String>,
}

/// Facts from a PE header.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PeInfo {
    pub machine: u16,
    pub characteristics: u16,
    /// 0x20b PE32+, 0x10b PE32.
    pub magic: u16,
    pub exports: Vec<String>,
}

const IMAGE_FILE_DLL: u16 = 0x2000;
const IMAGE_FILE_MACHINE_AMD64: u16 = 0x8664;
const PE32_PLUS: u16 = 0x20b;

fn u16_at(b: &[u8], o: usize) -> Option<u16> {
    Some(u16::from_le_bytes(b.get(o..o.checked_add(2)?)?.try_into().ok()?))
}

fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    Some(u32::from_le_bytes(b.get(o..o.checked_add(4)?)?.try_into().ok()?))
}

fn parse_error(msg: &str) -> Error {
    Error::new(ErrorCode::Parse, format!("not a PE file: {msg}"))
}

/// Parses the headers and the export names of a PE image (file layout).
pub fn parse(b: &[u8]) -> Result<PeInfo> {
    if b.get(0..2) != Some(b"MZ") {
        return Err(parse_error("no MZ header"));
    }
    let nt = u32_at(b, 0x3c).ok_or_else(|| parse_error("truncated DOS header"))? as usize;
    if b.get(nt..nt + 4) != Some(b"PE\0\0") {
        return Err(parse_error("no PE signature"));
    }
    let coff = nt + 4;
    let machine = u16_at(b, coff).ok_or_else(|| parse_error("truncated COFF header"))?;
    let nsect = u16_at(b, coff + 2).ok_or_else(|| parse_error("truncated COFF header"))? as usize;
    let opt_size = u16_at(b, coff + 16).ok_or_else(|| parse_error("truncated COFF header"))? as usize;
    let characteristics = u16_at(b, coff + 18).ok_or_else(|| parse_error("truncated COFF header"))?;
    let opt = coff + 20;
    let magic = u16_at(b, opt).ok_or_else(|| parse_error("no optional header"))?;
    let (ndirs_rel, dirs_rel) = match magic {
        PE32_PLUS => (108, 112),
        0x10b => (92, 96),
        _ => return Err(parse_error("unknown optional header")),
    };
    let optional = b.get(opt..opt.checked_add(opt_size).ok_or_else(|| parse_error("header overflow"))?)
        .ok_or_else(|| parse_error("truncated optional header"))?;
    let ndirs = u32_at(optional, ndirs_rel).ok_or_else(|| parse_error("short optional header"))? as usize;
    if ndirs > (opt_size.saturating_sub(dirs_rel)) / 8 {
        return Err(parse_error("truncated data directories"));
    }
    let sections = opt + opt_size;
    let section_end = sections.checked_add(nsect * 40).ok_or_else(|| parse_error("section overflow"))?;
    if section_end > b.len() { return Err(parse_error("truncated section table")); }
    let mut section_list = Vec::new();
    for i in 0..nsect {
        let s = sections + i * 40;
        let va = u32_at(b, s + 12).unwrap();
        let raw = u32_at(b, s + 20).unwrap() as usize;
        let size = u32_at(b, s + 16).unwrap() as usize;
        if raw.checked_add(size).is_none_or(|end| end > b.len()) {
            return Err(parse_error("section outside file"));
        }
        section_list.push((va, raw, size));
    }
    // Return only file-backed bytes of one section (never virtual padding or another section's bytes).
    let to_bytes = |rva: u32| -> Option<&[u8]> {
        section_list.iter().find_map(|&(va, raw, size)| {
            let delta = rva.checked_sub(va)? as usize;
            (delta < size).then(|| &b[raw + delta..raw + size])
        }).or_else(|| {
            let headers = u32_at(optional, 60)? as usize;
            (headers <= b.len() && (rva as usize) < headers).then(|| &b[rva as usize..headers])
        })
    };
    let mut exports = Vec::new();
    if ndirs > 0 {
        let rva = u32_at(optional, dirs_rel).unwrap();
        if rva != 0 {
            let ed = to_bytes(rva).filter(|s| s.len() >= 40).ok_or_else(|| parse_error("bad export directory"))?;
            let n = u32_at(ed, 24).unwrap() as usize;
            if n > 65_536 { return Err(parse_error("too many export names")); }
            if n > 0 {
                let names = to_bytes(u32_at(ed, 32).unwrap()).filter(|s| s.len() >= n * 4)
                    .ok_or_else(|| parse_error("bad export name table"))?;
                for i in 0..n {
                    let rest = to_bytes(u32_at(names, i * 4).unwrap()).ok_or_else(|| parse_error("bad export name RVA"))?;
                    let end = rest.iter().take(1024).position(|&c| c == 0).ok_or_else(|| parse_error("unterminated export name"))?;
                    if end > 0 { exports.push(String::from_utf8_lossy(&rest[..end]).into_owned()); }
                }
            }
        }
    }
    Ok(PeInfo { machine, characteristics, magic, exports })
}

/// Exported names of a PE DLL.
pub fn exports(path: &Path) -> Result<Vec<String>> {
    let b = std::fs::read(path).map_err(crate::util::io_at(path, "reading"))?;
    parse(&b).map(|p| p.exports).map_err(|e| Error::new(e.code, format!("{}: {}", path.display(), e.message)))
}

/// True for a PE32+ (x64) image with the DLL characteristic.
pub fn is_pe64_dll(bytes: &[u8]) -> bool {
    parse(bytes).is_ok_and(|p| p.magic == PE32_PLUS && p.machine == IMAGE_FILE_MACHINE_AMD64 && p.characteristics & IMAGE_FILE_DLL != 0)
}

pub(crate) fn contains(hay: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || hay.len() < needle.len() {
        return None;
    }
    hay.windows(needle.len()).position(|w| w == needle)
}

/// Does `bytes` hold the ASCII text `s`, as 8-bit or as UTF-16LE text (Windows' wide strings), ASCII case ignored? A
/// DLL's file names may be either: XHL-Core 1.x held its log file's name as 8-bit text, 2.x only as wide text.
pub fn contains_text(bytes: &[u8], s: &str) -> bool {
    let a = s.as_bytes();
    if a.is_empty() { return true; }
    bytes.windows(a.len()).any(|w| w.eq_ignore_ascii_case(a))
        || bytes.windows(a.len() * 2).any(|w| w.chunks_exact(2).zip(a).all(|(c, b)| c[1] == 0 && c[0].eq_ignore_ascii_case(b)))
}

fn utf16le(s: &str) -> Vec<u8> {
    s.encode_utf16().flat_map(|c| c.to_le_bytes()).collect()
}

/// `KilnProxy/<abi> <version>` in the bytes: (abi, version).
fn kiln_marker(b: &[u8]) -> Option<(u32, Option<String>)> {
    let at = contains(b, b"KilnProxy/")?;
    let rest = &b[at + 10..];
    let digits = rest.iter().take_while(|c| c.is_ascii_digit()).count();
    let abi: u32 = std::str::from_utf8(&rest[..digits]).ok()?.parse().ok()?;
    let rest = &rest[digits..];
    let version = rest.strip_prefix(b" ").map(|v| {
        let n = v.iter().take(64).take_while(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'-' | b'+')).count();
        String::from_utf8_lossy(&v[..n]).into_owned()
    });
    Some((abi, version.filter(|v| !v.is_empty())))
}

/// Classifies DLL bytes (no I/O): kind, abi/version for Kiln, the matching marker in `detail`.
pub fn classify_bytes(b: &[u8]) -> ProxyClass {
    let mut c = ProxyClass {
        kind: ProxyKind::NotDll,
        version: None,
        abi: None,
        sha256: Some(crate::hash::sha256_hex(b)),
        size: Some(b.len() as u64),
        detail: None,
    };
    let info = match parse(b) {
        Ok(i) => i,
        Err(e) => {
            c.detail = Some(e.message);
            return c;
        }
    };
    if !(info.magic == PE32_PLUS && info.machine == IMAGE_FILE_MACHINE_AMD64 && info.characteristics & IMAGE_FILE_DLL != 0) {
        c.detail = Some(format!(
            "not a 64-bit DLL (machine {:#06x}, magic {:#x}, characteristics {:#06x})",
            info.machine, info.magic, info.characteristics
        ));
        return c;
    }
    let has = |e: &str| info.exports.iter().any(|x| x == e);
    if has(KILN_EXPORT) {
        c.kind = ProxyKind::Kiln;
        let m = kiln_marker(b);
        c.abi = Some(m.as_ref().map(|m| m.0).unwrap_or(2));
        c.version = m.and_then(|m| m.1);
        c.detail = Some(format!("export {KILN_EXPORT}"));
        return c;
    }
    if let Some((abi, version)) = kiln_marker(b) {
        c.kind = ProxyKind::Kiln;
        c.abi = Some(abi);
        c.version = version;
        c.detail = Some(format!("marker KilnProxy/{abi}"));
        return c;
    }
    if contains(b, &utf16le(KILN_MARKER_UTF16)).is_some() {
        c.kind = ProxyKind::Kiln;
        c.abi = Some(1);
        c.detail = Some("UTF-16 marker \\kiln\\kiln-core.dll (older proxy)".into());
        return c;
    }
    if let Some(e) = EMF_EXPORTS.iter().find(|e| has(e)) {
        c.kind = ProxyKind::Emf;
        c.detail = Some(format!("export {e}"));
        return c;
    }
    if b.get(0x40..0x40 + WINE_BUILTIN_MARKER.len()) != Some(WINE_BUILTIN_MARKER.as_bytes())
        && let Some((engine, marker)) = crate::engines::classify_markers(b, &info.exports)
    {
        c.kind = engine.proxy_kind();
        c.detail = Some(marker);
        return c;
    }
    c.kind = ProxyKind::Unknown;
    c.detail = Some(if b.get(0x40..0x40 + WINE_BUILTIN_MARKER.len()) == Some(WINE_BUILTIN_MARKER.as_bytes()) {
        "Wine builtin DLL".into()
    } else if has(DINPUT8_EXPORT) {
        "another dinput8 proxy (exports DirectInput8Create)".into()
    } else {
        "a 64-bit DLL without known markers".into()
    });
    c
}

/// What the file at `path` is (ProxyKind::None when it does not exist).
pub fn classify_proxy(path: &Path) -> Result<ProxyClass> {
    // the status poll, chain views and activations classify the same few DLLs again and again (a read, a sha256 and a
    // parse of each): once per file while it is unchanged
    static CLASSES: std::sync::LazyLock<crate::filecache::Memo<ProxyClass>> = std::sync::LazyLock::new(|| crate::filecache::Memo::new(256));
    CLASSES.get(path, classify_proxy_uncached)
}

fn classify_proxy_uncached(path: &Path) -> Result<ProxyClass> {
    match std::fs::read(path) {
        Ok(b) => Ok(classify_bytes(&b)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            Ok(ProxyClass { kind: ProxyKind::None, version: None, abi: None, sha256: None, size: None, detail: None })
        }
        Err(e) => Err(crate::util::io_at(path, "reading")(e)),
    }
}

/// A minimal valid PE32+ x64 DLL image with these exports, then `extra` appended (marker texts). For tests and fake
/// proxies (client-copy installs); it is never meant to be loaded.
#[doc(hidden)]
pub fn build_test_dll(exports: &[&str], extra: &[u8]) -> Vec<u8> {
    let mut b = vec![0u8; 0x200];
    b[0..2].copy_from_slice(b"MZ");
    b[0x3c..0x40].copy_from_slice(&0x80u32.to_le_bytes());
    b[0x80..0x84].copy_from_slice(b"PE\0\0");
    let coff = 0x84;
    b[coff..coff + 2].copy_from_slice(&IMAGE_FILE_MACHINE_AMD64.to_le_bytes());
    b[coff + 2..coff + 4].copy_from_slice(&1u16.to_le_bytes());
    b[coff + 16..coff + 18].copy_from_slice(&240u16.to_le_bytes());
    b[coff + 18..coff + 20].copy_from_slice(&(IMAGE_FILE_DLL | 0x0022).to_le_bytes());
    let opt = coff + 20;
    b[opt..opt + 2].copy_from_slice(&PE32_PLUS.to_le_bytes());
    b[opt + 108..opt + 112].copy_from_slice(&16u32.to_le_bytes());
    // .edata at RVA 0x1000, file offset 0x200
    let va = 0x1000u32;
    let mut sec = vec![0u8; 40];
    let n = exports.len() as u32;
    let dir_len = 40u32;
    let funcs = va + dir_len;
    let names = funcs + 4 * n;
    let ords = names + 4 * n;
    let mut strings = ords + 2 * n;
    let mut data = vec![0u8; 40];
    let dll_name = strings;
    let mut str_bytes = b"dinput8.dll\0".to_vec();
    strings += str_bytes.len() as u32;
    let mut name_rvas = Vec::new();
    for e in exports {
        name_rvas.push(strings);
        str_bytes.extend_from_slice(e.as_bytes());
        str_bytes.push(0);
        strings += e.len() as u32 + 1;
    }
    data[12..16].copy_from_slice(&dll_name.to_le_bytes());
    data[16..20].copy_from_slice(&1u32.to_le_bytes());
    data[20..24].copy_from_slice(&n.to_le_bytes());
    data[24..28].copy_from_slice(&n.to_le_bytes());
    data[28..32].copy_from_slice(&funcs.to_le_bytes());
    data[32..36].copy_from_slice(&names.to_le_bytes());
    data[36..40].copy_from_slice(&ords.to_le_bytes());
    for _ in 0..n {
        data.extend_from_slice(&0x2000u32.to_le_bytes());
    }
    for r in &name_rvas {
        data.extend_from_slice(&r.to_le_bytes());
    }
    for i in 0..n {
        data.extend_from_slice(&(i as u16).to_le_bytes());
    }
    data.extend_from_slice(&str_bytes);
    let size = data.len() as u32;
    let raw = size.div_ceil(0x200) * 0x200;
    data.resize(raw as usize, 0);
    sec[0..6].copy_from_slice(b".edata");
    sec[8..12].copy_from_slice(&size.to_le_bytes());
    sec[12..16].copy_from_slice(&va.to_le_bytes());
    sec[16..20].copy_from_slice(&raw.to_le_bytes());
    sec[20..24].copy_from_slice(&0x200u32.to_le_bytes());
    sec[36..40].copy_from_slice(&0x4000_0040u32.to_le_bytes());
    let dirs = opt + 112;
    b[dirs..dirs + 4].copy_from_slice(&va.to_le_bytes());
    b[dirs + 4..dirs + 8].copy_from_slice(&size.to_le_bytes());
    let sections = opt + 240;
    b[sections..sections + 40].copy_from_slice(&sec);
    b.extend_from_slice(&data);
    b.extend_from_slice(extra);
    b
}

#[cfg(test)]
mod tests {
    #[test]
    fn text_is_found_as_8_bit_or_wide_text_with_ascii_case_ignored() {
        let wide = |t: &str| t.encode_utf16().flat_map(|c| c.to_le_bytes()).collect::<Vec<u8>>();
        assert!(contains_text(b"..XHL-Native-Plugin-Host.log..", "xhl-native-plugin-host.log"));
        assert!(contains_text(&[b"..".to_vec(), wide("XHL-Native-Plugin-Host.log"), b"..".to_vec()].concat(), "XHL-NATIVE-PLUGIN-HOST.LOG"));
        assert!(!contains_text(&wide("XHL-Native-Plugin-Host.lo"), "XHL-Native-Plugin-Host.log"));
        // a wide character whose high byte is not zero is no ASCII letter
        assert!(!contains_text(&[b'a', 1, b'b', 0], "ab"));
    }

    use super::*;

    #[test]
    fn exports_of_a_built_dll() {
        let d = build_test_dll(&["DirectInput8Create", "emf_loader_identity"], b"");
        assert!(is_pe64_dll(&d));
        assert_eq!(parse(&d).unwrap().exports, ["DirectInput8Create", "emf_loader_identity"]);
        assert!(!is_pe64_dll(b"MZ not really"));
        assert!(!is_pe64_dll(b""));
    }

    #[test]
    fn classification_follows_the_contract_table() {
        let k = |e: &[&str], x: &[u8]| classify_bytes(&build_test_dll(e, x));
        let c = k(&["DirectInput8Create", "KilnProxyInfo"], b"..KilnProxy/2 0.2.0-dev\0");
        assert_eq!((c.kind, c.abi, c.version.as_deref()), (ProxyKind::Kiln, Some(2), Some("0.2.0-dev")));
        let c = k(&["DirectInput8Create"], b"KilnProxy/2 0.3.1+abc\0");
        assert_eq!((c.kind, c.abi, c.version.as_deref()), (ProxyKind::Kiln, Some(2), Some("0.3.1+abc")));
        let c = k(&["DirectInput8Create"], &utf16le("Z:\\x\\kiln\\kiln-core.dll"));
        assert_eq!((c.kind, c.abi), (ProxyKind::Kiln, Some(1)));
        assert_eq!(k(&["DirectInput8Create"], b"shroudforge/shroudforge-runtime.dll").kind, ProxyKind::ShroudForge);
        assert_eq!(k(&["DirectInput8Create"], b"x shroudforge/config/modloader-config.json").kind, ProxyKind::ShroudForge);
        assert_eq!(k(&["DirectInput8Create", "ShroudforgeSetDinput8Provider"], b"").kind, ProxyKind::ShroudForge);
        assert_eq!(k(&["DirectInput8Create", "emf_loader_identity"], b"").kind, ProxyKind::Emf);
        assert_eq!(k(&["emf_loader_play_once"], b"").kind, ProxyKind::Emf);
        assert_eq!(k(&["DirectInput8Create"], b"").kind, ProxyKind::Unknown);
        // EMF 0.4+ exports ShroudForge's chain-host function too: still EMF (fixture shaped like EMF 0.5.1's loader)
        let emf051 = k(
            &["DirectInput8Create", "DllCanUnloadNow", "DllGetClassObject", "DllRegisterServer", "DllUnregisterServer",
              "GetdfDIJoystick", "ShroudforgeSetDinput8Provider", "emf_loader_identity", "emf_loader_play_once"],
            b"EMF-LOADER-7d3c9f\0EMF-PLAY-ONCE\0shroudforge/shroudforge-runtime.dll\0dinput8-chain.ini\0[EMF]\0",
        );
        assert_eq!((emf051.kind, emf051.detail.as_deref()), (ProxyKind::Emf, Some("export emf_loader_identity")));
        // a build that keeps only the marker text (exports stripped) is EMF too, also next to ShroudForge's export
        assert_eq!(k(&["DirectInput8Create", "ShroudforgeSetDinput8Provider"], b"EMF-LOADER-7d3c9f").kind, ProxyKind::Emf);
        // Kiln's markers win over the others
        assert_eq!(k(&["KilnProxyInfo", "emf_loader_identity"], b"").kind, ProxyKind::Kiln);
        // a 32-bit DLL is not a proxy for a 64-bit game
        let mut x86 = build_test_dll(&["DirectInput8Create"], b"");
        x86[0x84..0x86].copy_from_slice(&0x14cu16.to_le_bytes());
        assert_eq!(classify_bytes(&x86).kind, ProxyKind::NotDll);
        assert_eq!(classify_bytes(b"hello").kind, ProxyKind::NotDll);
        let none = classify_proxy(Path::new("/nonexistent/dinput8.dll")).unwrap();
        assert_eq!(none.kind, ProxyKind::None);
    }
}
