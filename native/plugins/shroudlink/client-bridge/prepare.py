#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Source-only, offline, opt-in recipe. Run with python -I from outside the source.
# Copyright (c) 2026 bjck's MIT notice in the supplied source/LICENSE must remain.
# Usage: prepare.py PRISTINE_SHROUDLINK_SOURCE NEW_PRIVATE_OUTPUT_DIRECTORY
# Does not build, install, replace, download or distribute an addon. Refuses an
# existing destination and unknown inputs; count-independent prototype is separate.
import hashlib
import pathlib
import shutil
import sys

HASHES = {
    'input.rs': '38fc89e8a3a1b30653139a35e575359537542eb7d05470212c6462b88cbb5a9a',
    'native.rs': '7c28d472343a75c3e39b532b7eade4fd3c49cac1b174d717633fd62ef1427bfb',
    'lib.rs': 'e8cb7baea5783c1961d87e9fd417f62ca7f8847222a9ee8eff6f1191f2cd21d1',
    'link.rs': '4329a2772332b74afea0260c3e257b3e0ad3bbb6b07d92ce88997a7fe3b506c9',
    'ui.rs': 'c264c604579241d122c1b995abd44ca7cb8d7829aa0095b919a7539424e5ac37',
}
def prepare(src, dst):
    src, dst = pathlib.Path(src).resolve(), pathlib.Path(dst).resolve()
    if dst.exists() or dst.is_relative_to(src):
        raise ValueError('destination must be new and outside the source')
    license_text = (src / 'LICENSE').read_text()
    if 'MIT License' not in license_text or 'bjck' not in license_text:
        raise ValueError('expected upstream MIT notice')
    original = {}
    for name, expected in HASHES.items():
        data = (src / 'mod/client/src' / name).read_bytes()
        if hashlib.sha256(data).hexdigest() != expected:
            raise ValueError('unsupported pristine source: ' + name)
        original[name] = data.decode('utf-8')
    def rep(name, before, after):
        assert original[name].count(before) == 1, (name, before)
        original[name] = original[name].replace(before, after)
    rep('input.rs', 'unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {', '''unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    // The cooperating presenter owns interaction. Forward unchanged to the saved
    // procedure (including Kiln), without native text/hotkey/wheel side effects.
    if crate::bridge_adapter::presenting() {
        let original: unsafe extern "system" fn(HWND, u32, WPARAM, LPARAM) -> LRESULT = std::mem::transmute(ORIGINAL.load(Ordering::Relaxed));
        return CallWindowProcW(Some(original), hwnd, msg, wparam, lparam);
    }''')
    rep('lib.rs', 'mod native;', 'mod native;\nmod bridge_core;\nmod bridge_adapter;')
    rep('lib.rs', 'pub extern "C" fn shroudlink_shutdown() {', 'pub extern "C" fn shroudlink_shutdown() {\n    bridge_adapter::stop();')
    rep('lib.rs', '    replace_previous_copy(own);', '    replace_previous_copy(own);\n    bridge_adapter::start();')
    rep('link.rs', '*link = Link { nonce: new_nonce(), slot: Some(slot), ..Link::default() };', '*link = Link { nonce: new_nonce(), slot: Some(slot), ..Link::default() };\n                    crate::bridge_adapter::session();')
    rep('link.rs', '            Some("welcome") => {', '            Some("welcome") => {\n                crate::bridge_adapter::welcome(message.get("ui_protocol").and_then(Value::as_u64).unwrap_or(1));')
    rep('link.rs', '    let packets: Vec<String> = with_link(|link| {', '    let mut packets: Vec<String> = with_link(|link| {')
    rep('link.rs', '    if !packets.is_empty() {', '''    // Cooperative UI events never enter the persistent stock outbox. Validate
    // again under its session lock, use its actual nonce/message counter, and fit
    // the entire event into this tick's remaining packet budget.
    if let Some((ticket,message)) = crate::bridge_adapter::tick(client as usize, PACKETS_PER_TICK - packets.len()) {
        let extra = with_link(|link| {
            if link.slot.is_none() || !crate::bridge_adapter::valid(&ticket) { return Vec::new(); }
            let bytes = serde_json::to_vec(&message).unwrap();
            let extra = split_message(link.nonce,link.next_msg,&bytes);
            if extra.len() > PACKETS_PER_TICK - packets.len() { return Vec::new(); }
            link.next_msg = link.next_msg.wrapping_add(1);
            extra
        });
        if !extra.is_empty() { crate::bridge_adapter::committed(&ticket); packets.extend(extra); }
    }
    if !packets.is_empty() {''')
    rep('ui.rs', '    let mut ui = UI.lock().unwrap();\n    match op {', '    crate::bridge_adapter::observe(&op);\n    let mut ui = UI.lock().unwrap();\n    match op {')
    rep('ui.rs', '    let screen = unsafe { api.screen() };', '''    let screen = unsafe { api.screen() };
    crate::bridge_adapter::world(screen != 0);
    if crate::bridge_adapter::presenting() { game_tab_chosen(); }''')
    rep('ui.rs', '    if open {\n        if let Some((owner, entry))', '    if open && !crate::bridge_adapter::presenting() {\n        if let Some((owner, entry))')
    rep('ui.rs', '        let page = mods_page_open() && menu_visible;', '        let page = mods_page_open() && menu_visible && !crate::bridge_adapter::presenting();')
    rep('ui.rs', '        for (owner, id) in &pending.closed {', '        for (owner, id) in &pending.closed {\n            crate::bridge_adapter::closed(owner,id);')
    rep('ui.rs', '    for (owner, event) in pending.events {\n        log!("ui: event', '    for (owner, event) in pending.events {\n        if crate::bridge_adapter::presenting() { continue; }\n        log!("ui: event')
    original['ui.rs'] += '\npub fn bridge_closed(owner:&str,id:&str) { UI.lock().unwrap().windows.retain(|w|w.owner!=owner || w.window.id!=id); }\n'
    # All input checks and exact replacements succeed before copying anything.
    shutil.copytree(src, dst, ignore=shutil.ignore_patterns('.git', 'target'))
    target = dst / 'mod/client/src'
    for name, text in original.items():
        (target / name).write_bytes(text.encode('utf-8'))  # preserve embedded NULs
    here = pathlib.Path(__file__).resolve().parent
    shutil.copy2(here / 'core.rs', target / 'bridge_core.rs')
    shutil.copy2(here / 'adapter.rs', target / 'bridge_adapter.rs')
    shutil.copy2(here / 'tests.rs', target / 'tests.rs')
    print('Prepared private MIT addon source:', dst)
    print('Build from outside it using CL=/FIstring.h CARGO_BUILD_JOBS=2 CMAKE_BUILD_PARALLEL_LEVEL=2 cargo xwin build --offline --release --locked --manifest-path <output>/mod/Cargo.toml -p shroudlink-client --target x86_64-pc-windows-msvc')
    print('Set CARGO_TARGET_DIR and CARGO_BUILD_BUILD_DIR to private scratch paths first; output is shroudlink_client.dll. Do not install or distribute it as part of this test.')
if __name__ == '__main__':
    if len(sys.argv) != 3: raise SystemExit('usage: prepare.py PRISTINE_SOURCE NEW_PRIVATE_OUTPUT')
    prepare(*sys.argv[1:])
