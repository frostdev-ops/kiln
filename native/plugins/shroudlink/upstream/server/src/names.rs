//! Index of the custom names players give objects (chest "Rename" button).
//!
//! The server keeps every custom name in records of the form
//! `{ u64 key; char* text = &key + 0x28; u32 len; u32 pad; u64 capacity = 0x100; ...; char text[256] }`
//! (save-layer "CustomStrings"). We have no stable path to the owning
//! table, so a background thread scans the heap for that self-describing layout.
//! The same key can exist in several copies (live table and save snapshot);
//! the copy whose text changed most recently wins.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use chestsort_common::log;
use windows_sys::Win32::System::Diagnostics::Debug::ReadProcessMemory;
use windows_sys::Win32::System::Memory::{VirtualQuery, MEMORY_BASIC_INFORMATION, MEM_COMMIT, MEM_PRIVATE, PAGE_READWRITE};
use windows_sys::Win32::System::Threading::GetCurrentProcess;

const TEXT_OFFSET: usize = 0x28;
const CAPACITY: u64 = 0x100;
const SCAN_INTERVAL: Duration = Duration::from_secs(15);
const MIN_RESCAN_GAP: Duration = Duration::from_secs(3);
const CHUNK: usize = 1 << 20;

struct Record {
    key: u64,
    text: String,
    /// scan number in which this copy got its current text
    changed: u64,
}

#[derive(Default)]
struct Index {
    by_addr: HashMap<usize, Record>,
    scans: u64,
    last_scan: Option<Instant>,
}

static INDEX: Mutex<Option<Index>> = Mutex::new(None);

pub fn start() {
    std::thread::spawn(|| loop {
        rescan();
        std::thread::sleep(SCAN_INTERVAL);
    });
}

/// Current name for `key`, if any object carries it.
pub fn lookup(key: u64) -> Option<String> {
    let guard = INDEX.lock().unwrap();
    let index = guard.as_ref()?;
    index
        .by_addr
        .values()
        .filter(|r| r.key == key)
        .max_by_key(|r| r.changed)
        .map(|r| r.text.clone())
}

/// Like [`lookup`], but rescans immediately (rate limited) when the key is unknown,
/// e.g. right after a chest was renamed.
pub fn lookup_fresh(key: u64) -> Option<String> {
    if let Some(name) = lookup(key) {
        return Some(name);
    }
    let recent = INDEX.lock().unwrap().as_ref().and_then(|i| i.last_scan).is_some_and(|t| t.elapsed() < MIN_RESCAN_GAP);
    if recent {
        return None;
    }
    rescan();
    lookup(key)
}

fn rescan() {
    let started = Instant::now();
    let found = scan_heap();
    let mut guard = INDEX.lock().unwrap();
    let index = guard.get_or_insert_with(Index::default);
    index.scans += 1;
    let scan = index.scans;
    let mut next = HashMap::with_capacity(found.len());
    for (addr, key, text) in found {
        let changed = match index.by_addr.get(&addr) {
            Some(old) if old.key == key && old.text == text => old.changed,
            _ => scan,
        };
        next.insert(addr, Record { key, text, changed });
    }
    let count = next.len();
    let had = index.by_addr.len();
    index.by_addr = next;
    index.last_scan = Some(Instant::now());
    if count != had {
        log!("name index: {count} name record(s), scan took {} ms", started.elapsed().as_millis());
    }
}

/// All `(address, key, text)` name records in committed private read/write memory.
fn scan_heap() -> Vec<(usize, u64, String)> {
    let mut found = Vec::new();
    let mut buf = vec![0u8; CHUNK + 0x200];
    let mut addr = 0usize;
    unsafe {
        let process = GetCurrentProcess();
        let mut info: MEMORY_BASIC_INFORMATION = std::mem::zeroed();
        while VirtualQuery(addr as *const _, &mut info, std::mem::size_of::<MEMORY_BASIC_INFORMATION>()) != 0 {
            let base = info.BaseAddress as usize;
            let size = info.RegionSize;
            if info.State == MEM_COMMIT && info.Type == MEM_PRIVATE && info.Protect == PAGE_READWRITE {
                let mut offset = 0;
                while offset < size {
                    // overlap chunks so records crossing a boundary are seen whole
                    let len = (size - offset).min(CHUNK + 0x200);
                    let mut read = 0usize;
                    let ok = ReadProcessMemory(process, (base + offset) as *const _, buf.as_mut_ptr() as *mut _, len, &mut read);
                    if ok != 0 {
                        scan_chunk(&buf[..read], base + offset, &mut found);
                    }
                    offset += CHUNK;
                }
            }
            addr = base + size;
        }
    }
    found
}

fn scan_chunk(data: &[u8], base: usize, found: &mut Vec<(usize, u64, String)>) {
    let mut i = 0;
    while i + TEXT_OFFSET + 0x100 <= data.len() {
        let addr = base + i;
        let ptr = u64::from_le_bytes(data[i + 8..i + 16].try_into().unwrap());
        if ptr == (addr + TEXT_OFFSET) as u64
            && u64::from_le_bytes(data[i + 0x18..i + 0x20].try_into().unwrap()) == CAPACITY
        {
            let key = u64::from_le_bytes(data[i..i + 8].try_into().unwrap());
            let len = u32::from_le_bytes(data[i + 0x10..i + 0x14].try_into().unwrap()) as usize;
            let text = &data[i + TEXT_OFFSET..i + TEXT_OFFSET + 0x100];
            if key != 0 && (1..0x100).contains(&len) && text[len] == 0 {
                if let Ok(s) = std::str::from_utf8(&text[..len]) {
                    found.push((addr, key, s.to_owned()));
                }
            }
        }
        i += 8;
    }
}
