//! Locating game functions at runtime: wildcard byte patterns in `.text`
//! and resolution of helper functions through the `call`s of a known function.

#[path = "file_scan.rs"] // kiln: bounded file parser, also tested without Windows
mod file_scan;
use file_scan::file_text;

use iced_x86::{Decoder, DecoderOptions, Mnemonic, OpKind};
use windows_sys::Win32::System::Diagnostics::Debug::{IMAGE_NT_HEADERS64, IMAGE_SECTION_HEADER};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::SystemServices::IMAGE_DOS_HEADER;

pub struct Module {
    pub base: usize,
    text: &'static [u8],
    text_start: usize,
}

impl Module {
    /// The main executable of the current process.
    pub fn main() -> Option<Module> {
        unsafe {
            let base = GetModuleHandleW(std::ptr::null()) as usize;
            if base == 0 {
                return None;
            }
            let dos = &*(base as *const IMAGE_DOS_HEADER);
            let nt = &*((base + dos.e_lfanew as usize) as *const IMAGE_NT_HEADERS64);
            let first = (nt as *const IMAGE_NT_HEADERS64 as usize)
                + std::mem::size_of::<u32>()
                + std::mem::size_of_val(&nt.FileHeader)
                + nt.FileHeader.SizeOfOptionalHeader as usize;
            let sections = std::slice::from_raw_parts(
                first as *const IMAGE_SECTION_HEADER,
                nt.FileHeader.NumberOfSections as usize,
            );
            let text = sections.iter().find(|s| s.Name.starts_with(b".text"))?;
            let text_start = base + text.VirtualAddress as usize;
            let len = text.Misc.VirtualSize as usize;
            Some(Module { base, text: std::slice::from_raw_parts(text_start as *const u8, len), text_start })
        }
    }

    /// Finds the unique match of an IDA-style pattern ("48 8B ?? 05") in `.text`.
    pub fn find(&self, pattern: &str) -> Result<usize, String> {
        let pat: Vec<Option<u8>> = pattern
            .split_whitespace()
            .map(|t| if t.starts_with('?') { None } else { u8::from_str_radix(t, 16).ok() })
            .collect();
        if pat.is_empty() { return Err("empty pattern".into()); } // kiln: windows(0) panics
        let hits: Vec<usize> = self
            .text
            .windows(pat.len())
            .enumerate()
            .filter(|(_, w)| pat.iter().zip(w.iter()).all(|(p, b)| p.map_or(true, |p| p == *b)))
            .map(|(i, _)| self.text_start + i)
            .take(2)
            .collect();
        match hits.as_slice() {
            [one] => Ok(*one),
            // kiln: another loader (Kiln's own services, another mod) may have hooked the function already, which
            // overwrote the first bytes of its prologue in memory. Find it in the code as the exe file has it, so
            // shroudlink's hook chains onto the existing one instead of turning the feature off.
            [] => match self.find_in_file(&pat) {
                Some(addr) => {
                    crate::log!("kiln: found in the exe file (already hooked in memory): +{:#x}", addr - self.base);
                    Ok(addr)
                }
                None => Err(format!("pattern not found: {pattern}")),
            },
            _ => Err(format!("pattern not unique: {pattern}")),
        }
    }

    /// kiln: the unique match in `.text` as the exe file holds it (before any hook), as an address in this process.
    fn find_in_file(&self, pat: &[Option<u8>]) -> Option<usize> {
        let file = std::fs::read(std::env::current_exe().ok()?).ok()?;
        let text = file_text(&file, self.text_start.checked_sub(self.base)?, self.text.len())?;
        if pat.is_empty() { return None; }
        let mut hits = text
            .windows(pat.len())
            .enumerate()
            .filter(|(_, w)| pat.iter().zip(w.iter()).all(|(p, b)| p.map_or(true, |p| p == *b)))
            .map(|(i, _)| self.text_start + i);
        let one = hits.next()?;
        hits.next().is_none().then_some(one)
    }

    pub fn rva(&self, addr: usize) -> usize {
        addr - self.base
    }
}

/// Direct `call rel32` targets in the first `len` bytes of the function at `func`, in order.
pub fn call_targets(func: usize, len: usize) -> Vec<usize> {
    let code = unsafe { std::slice::from_raw_parts(func as *const u8, len) };
    let mut decoder = Decoder::with_ip(64, code, func as u64, DecoderOptions::NONE);
    let mut targets = Vec::new();
    for insn in &mut decoder {
        if insn.mnemonic() == Mnemonic::Call && insn.op0_kind() == OpKind::NearBranch64 {
            targets.push(insn.near_branch_target() as usize);
        }
    }
    targets
}

#[cfg(test)]
mod kiln_tests {
    use super::*;
    #[test]
    fn kiln_empty_pattern_is_an_error_instead_of_a_panic() {
        let module = Module { base: 0, text: &[], text_start: 0 };
        assert!(module.find("").is_err());
    }
}
