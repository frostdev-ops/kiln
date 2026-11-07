//! The native gamepad reader of the Deck UI: Linux evdev (`/dev/input/event*`) on a background thread, emitted as
//! `kiln://gamepad` (contracts.md section 10). WebKitGTK's Gamepad API is unreliable (often missing, and blind until a
//! button is pressed), so the Deck UI reads the controllers here; the browser Gamepad API and the keyboard stay as
//! fallbacks in the frontend (launcher/src/deck/input.ts).
//!
//! What it sends:
//! - buttons A, B, X, Y, LB, RB, LT, RT, Select (View), Start (Menu), Guide, LS, RS, and the four directions;
//! - directions come from the D-pad (hat or buttons) and from the left stick (pressed past 0.6, released under 0.4),
//!   merged, and repeat while held (first after 380 ms, then every 120 ms, every 70 ms after 1.5 s);
//! - the right stick as `axis` `rx`/`ry` (-1..1, at most ~25 per second while pushed, then one 0) for scrolling;
//! - `connected` / `disconnected` per device.
//!
//! Button positions follow xpad, which Steam's virtual controller (what a game gets in Game Mode) and the Deck's
//! hid-steam driver use: `BTN_SOUTH` = A, `BTN_EAST` = B, `BTN_X` (0x133) = X (the left button), `BTN_Y` (0x134) = Y
//! (the top one). Devices are never grabbed: the game and Steam keep their input. The frontend ignores the events
//! while the launcher's window has no focus.

use serde::Serialize;
use std::collections::HashMap;
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Button {
    A,
    B,
    X,
    Y,
    Lb,
    Rb,
    Lt,
    Rt,
    Select,
    Start,
    Guide,
    Ls,
    Rs,
    Up,
    Down,
    Left,
    Right,
}

impl Button {
    pub fn is_direction(self) -> bool {
        matches!(self, Button::Up | Button::Down | Button::Left | Button::Right)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Axis {
    Rx,
    Ry,
}

/// The `kiln://gamepad` payload.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum PadEvent {
    Button { button: Button, pressed: bool, repeat: bool, device: String },
    Axis { axis: Axis, value: f32, device: String },
    Connected { device: String, name: String, vendor: u16, product: u16 },
    Disconnected { device: String, name: String },
}

// ---------------------------------------------------------------- evdev codes (linux/input-event-codes.h)

pub const EV_KEY: u16 = 0x01;
pub const EV_ABS: u16 = 0x03;
pub const BTN_SOUTH: u16 = 0x130;
pub const ABS_X: u16 = 0x00;
pub const ABS_Y: u16 = 0x01;
pub const ABS_Z: u16 = 0x02;
pub const ABS_RX: u16 = 0x03;
pub const ABS_RY: u16 = 0x04;
pub const ABS_RZ: u16 = 0x05;
pub const ABS_GAS: u16 = 0x09;
pub const ABS_BRAKE: u16 = 0x0a;
pub const ABS_HAT0X: u16 = 0x10;
pub const ABS_HAT0Y: u16 = 0x11;

/// The button an `EV_KEY` code stands for.
pub fn button_for_key(code: u16) -> Option<Button> {
    Some(match code {
        0x130 => Button::A,      // BTN_SOUTH
        0x131 => Button::B,      // BTN_EAST
        0x133 => Button::X,      // BTN_X (= BTN_NORTH's code; xpad's X, the left button)
        0x134 => Button::Y,      // BTN_Y (= BTN_WEST's code; xpad's Y, the top button)
        0x136 => Button::Lb,     // BTN_TL
        0x137 => Button::Rb,     // BTN_TR
        0x138 => Button::Lt,     // BTN_TL2
        0x139 => Button::Rt,     // BTN_TR2
        0x13a => Button::Select, // BTN_SELECT (View, the Deck's ⧉)
        0x13b => Button::Start,  // BTN_START (Menu, the Deck's ☰)
        0x13c => Button::Guide,  // BTN_MODE
        0x13d => Button::Ls,     // BTN_THUMBL
        0x13e => Button::Rs,     // BTN_THUMBR
        0x220 => Button::Up,     // BTN_DPAD_UP
        0x221 => Button::Down,
        0x222 => Button::Left,
        0x223 => Button::Right,
        _ => return None,
    })
}

/// What one device's raw events mean, before directions from several sources are merged.
#[derive(Debug, Clone, PartialEq)]
pub enum Raw {
    Press(Source, Button),
    Release(Source, Button),
    Axis(Axis, f32),
}

/// Where a press came from (the same direction can be held on the D-pad and the stick at once).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Source {
    Key,
    Hat,
    Stick,
    Trigger,
}

/// An axis' range from its absinfo.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Range {
    pub min: i32,
    pub max: i32,
}

impl Range {
    /// -1..1 (centred axes: sticks).
    pub fn centred(&self, v: i32) -> f32 {
        let span = (self.max - self.min) as f32;
        if span <= 0.0 {
            return 0.0;
        }
        (((v - self.min) as f32 / span) * 2.0 - 1.0).clamp(-1.0, 1.0)
    }

    /// 0..1 (triggers).
    pub fn unit(&self, v: i32) -> f32 {
        let span = (self.max - self.min) as f32;
        if span <= 0.0 {
            return 0.0;
        }
        ((v - self.min) as f32 / span).clamp(0.0, 1.0)
    }
}

pub const STICK_PRESS: f32 = 0.6;
pub const STICK_RELEASE: f32 = 0.4;
pub const TRIGGER_PRESS: f32 = 0.6;
pub const TRIGGER_RELEASE: f32 = 0.3;
pub const SCROLL_DEADZONE: f32 = 0.22;

/// One device: raw evdev events in, [`Raw`] out.
#[derive(Debug, Default)]
pub struct Mapper {
    ranges: HashMap<u16, Range>,
    lx: f32,
    ly: f32,
    stick: Option<Button>,
    hat_x: i32,
    hat_y: i32,
    lt: bool,
    rt: bool,
}

impl Mapper {
    pub fn new(ranges: impl IntoIterator<Item = (u16, Range)>) -> Mapper {
        Mapper { ranges: ranges.into_iter().collect(), ..Default::default() }
    }

    fn range(&self, code: u16) -> Range {
        // without absinfo: the common signed 16-bit stick range
        self.ranges.get(&code).copied().unwrap_or(Range { min: -32768, max: 32767 })
    }

    pub fn feed(&mut self, type_: u16, code: u16, value: i32, out: &mut Vec<Raw>) {
        match type_ {
            EV_KEY => {
                if let Some(b) = button_for_key(code) {
                    match value {
                        0 => out.push(Raw::Release(Source::Key, b)),
                        1 => out.push(Raw::Press(Source::Key, b)),
                        _ => {} // the kernel's own autorepeat (2): ours is the one that counts
                    }
                }
            }
            EV_ABS => match code {
                ABS_X | ABS_Y => {
                    let v = self.range(code).centred(value);
                    if code == ABS_X { self.lx = v } else { self.ly = v }
                    self.stick_update(out);
                }
                ABS_RX => out.push(Raw::Axis(Axis::Rx, self.range(code).centred(value))),
                ABS_RY => out.push(Raw::Axis(Axis::Ry, self.range(code).centred(value))),
                ABS_HAT0X => self.hat(true, value.signum(), out),
                ABS_HAT0Y => self.hat(false, value.signum(), out),
                ABS_Z | ABS_BRAKE => self.trigger(false, self.range(code).unit(value), out),
                ABS_RZ | ABS_GAS => self.trigger(true, self.range(code).unit(value), out),
                _ => {}
            },
            _ => {}
        }
    }

    fn hat(&mut self, x: bool, v: i32, out: &mut Vec<Raw>) {
        let (old, neg, pos) = if x { (self.hat_x, Button::Left, Button::Right) } else { (self.hat_y, Button::Up, Button::Down) };
        if old == v {
            return;
        }
        match old {
            -1 => out.push(Raw::Release(Source::Hat, neg)),
            1 => out.push(Raw::Release(Source::Hat, pos)),
            _ => {}
        }
        match v {
            -1 => out.push(Raw::Press(Source::Hat, neg)),
            1 => out.push(Raw::Press(Source::Hat, pos)),
            _ => {}
        }
        if x { self.hat_x = v } else { self.hat_y = v }
    }

    /// The left stick as a direction: the dominant axis past [`STICK_PRESS`]; held until both fall under
    /// [`STICK_RELEASE`] (or another direction wins clearly).
    fn stick_update(&mut self, out: &mut Vec<Raw>) {
        let (ax, ay) = (self.lx.abs(), self.ly.abs());
        let want = if ax.max(ay) >= STICK_PRESS {
            Some(if ax >= ay { if self.lx < 0.0 { Button::Left } else { Button::Right } } else if self.ly < 0.0 { Button::Up } else { Button::Down })
        } else if let Some(cur) = self.stick {
            let along = match cur { Button::Left => -self.lx, Button::Right => self.lx, Button::Up => -self.ly, _ => self.ly };
            if along >= STICK_RELEASE { Some(cur) } else { None }
        } else {
            None
        };
        if want != self.stick {
            if let Some(old) = self.stick {
                out.push(Raw::Release(Source::Stick, old));
            }
            if let Some(new) = want {
                out.push(Raw::Press(Source::Stick, new));
            }
            self.stick = want;
        }
    }

    fn trigger(&mut self, right: bool, v: f32, out: &mut Vec<Raw>) {
        let held = if right { &mut self.rt } else { &mut self.lt };
        let b = if right { Button::Rt } else { Button::Lt };
        if !*held && v >= TRIGGER_PRESS {
            *held = true;
            out.push(Raw::Press(Source::Trigger, b));
        } else if *held && v <= TRIGGER_RELEASE {
            *held = false;
            out.push(Raw::Release(Source::Trigger, b));
        }
    }

    /// Everything this device holds, released (it was unplugged).
    pub fn release_all(&mut self, out: &mut Vec<Raw>) {
        self.hat(true, 0, out);
        self.hat(false, 0, out);
        if let Some(s) = self.stick.take() {
            out.push(Raw::Release(Source::Stick, s));
        }
        self.trigger(false, 0.0, out);
        self.trigger(true, 0.0, out);
    }
}

pub const REPEAT_FIRST: Duration = Duration::from_millis(380);
pub const REPEAT_EVERY: Duration = Duration::from_millis(120);
pub const REPEAT_FAST: Duration = Duration::from_millis(70);
pub const REPEAT_FAST_AFTER: Duration = Duration::from_millis(1500);
const SCROLL_EVERY: Duration = Duration::from_millis(40);

/// Merges every device's [`Raw`] into [`PadEvent`]s: one press per button however many sources hold it, the
/// repeat of the newest held direction, the right stick throttled.
#[derive(Debug, Default)]
pub struct Router {
    held: HashMap<(String, Source, Button), ()>,
    /// The direction that repeats: (button, device, held since, next repeat).
    repeat: Option<(Button, String, Instant, Instant)>,
    axes: HashMap<Axis, (f32, Option<Instant>)>,
}

impl Router {
    fn count(&self, b: Button) -> usize {
        self.held.keys().filter(|(_, _, x)| *x == b).count()
    }

    pub fn feed(&mut self, device: &str, raw: Raw, now: Instant, out: &mut Vec<PadEvent>) {
        match raw {
            Raw::Press(src, b) => {
                let first = self.count(b) == 0;
                self.held.insert((device.to_string(), src, b), ());
                if first {
                    out.push(PadEvent::Button { button: b, pressed: true, repeat: false, device: device.to_string() });
                    if b.is_direction() {
                        self.repeat = Some((b, device.to_string(), now, now + REPEAT_FIRST));
                    }
                }
            }
            Raw::Release(src, b) => {
                if self.held.remove(&(device.to_string(), src, b)).is_some() && self.count(b) == 0 {
                    out.push(PadEvent::Button { button: b, pressed: false, repeat: false, device: device.to_string() });
                    if self.repeat.as_ref().is_some_and(|r| r.0 == b) {
                        self.repeat = None;
                    }
                }
            }
            Raw::Axis(a, v) => {
                let v = if v.abs() < SCROLL_DEADZONE { 0.0 } else { v };
                let (last, at) = self.axes.get(&a).copied().unwrap_or((0.0, None));
                let due = at.is_none_or(|t| now.duration_since(t) >= SCROLL_EVERY);
                if (v != 0.0 && due) || (v == 0.0 && last != 0.0) {
                    out.push(PadEvent::Axis { axis: a, value: (v * 100.0).round() / 100.0, device: device.to_string() });
                    self.axes.insert(a, (v, Some(now)));
                } else if v != 0.0 {
                    // keep the latest value for the next due send
                    self.axes.insert(a, (v, at));
                }
            }
        }
    }

    /// Repeats that fell due, and a pushed right stick that has not moved (evdev only reports changes).
    pub fn tick(&mut self, now: Instant, out: &mut Vec<PadEvent>) {
        if let Some((b, dev, since, next)) = self.repeat.clone() {
            if now >= next {
                out.push(PadEvent::Button { button: b, pressed: true, repeat: true, device: dev.clone() });
                let every = if now.duration_since(since) >= REPEAT_FAST_AFTER { REPEAT_FAST } else { REPEAT_EVERY };
                self.repeat = Some((b, dev, since, now + every));
            }
        }
        for (a, (v, at)) in self.axes.iter_mut() {
            if *v != 0.0 && at.is_none_or(|t| now.duration_since(t) >= SCROLL_EVERY) {
                out.push(PadEvent::Axis { axis: *a, value: (*v * 100.0).round() / 100.0, device: String::new() });
                *at = Some(now);
            }
        }
    }

    /// Drops a device's holds (unplugged): releases what only it held.
    pub fn forget(&mut self, device: &str, now: Instant, out: &mut Vec<PadEvent>) {
        let keys: Vec<_> = self.held.keys().filter(|(d, _, _)| d == device).cloned().collect();
        for (d, s, b) in keys {
            self.feed(&d, Raw::Release(s, b), now, out);
        }
    }
}

// ---------------------------------------------------------------- the reader thread

/// `deck_gamepad`'s answer.
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GamepadStatus {
    /// This platform has the native reader (Linux).
    pub native: bool,
    /// The reader thread runs.
    pub running: bool,
    pub devices: Vec<DeviceInfo>,
    /// Gamepads seen in /dev/input that could not be opened (permissions): the frontend falls back to the browser.
    pub denied: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceInfo {
    pub device: String,
    pub name: String,
    pub vendor: u16,
    pub product: u16,
}

#[cfg(target_os = "linux")]
mod reader {
    use super::*;
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::{Arc, LazyLock, Mutex};

    struct Shared {
        stop: Option<Arc<AtomicBool>>,
        devices: Vec<DeviceInfo>,
        denied: usize,
    }

    static STATE: LazyLock<Mutex<Shared>> = LazyLock::new(|| Mutex::new(Shared { stop: None, devices: vec![], denied: 0 }));

    struct Open {
        id: String,
        name: String,
        dev: evdev::Device,
        map: Mapper,
    }

    /// A gamepad: it has BTN_SOUTH (keyboards and mice don't; the Deck's touchpads and motion sensors are separate
    /// devices without it).
    fn is_gamepad(dev: &evdev::Device) -> bool {
        dev.supported_keys().is_some_and(|k| k.contains(evdev::KeyCode(BTN_SOUTH)))
    }

    fn open(path: &Path) -> std::io::Result<Option<Open>> {
        let dev = evdev::Device::open(path)?;
        if !is_gamepad(&dev) {
            return Ok(None);
        }
        dev.set_nonblocking(true)?;
        let ranges: Vec<(u16, Range)> = dev
            .get_absinfo()
            .map(|it| it.map(|(c, i)| (c.0, Range { min: i.minimum(), max: i.maximum() })).collect())
            .unwrap_or_default();
        let id = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let name = dev.name().unwrap_or("Gamepad").to_string();
        Ok(Some(Open { id, name, dev, map: Mapper::new(ranges) }))
    }

    fn event_nodes(dir: &Path) -> Vec<PathBuf> {
        let mut v: Vec<PathBuf> = std::fs::read_dir(dir)
            .into_iter()
            .flatten()
            .flatten()
            .map(|e| e.path())
            .filter(|p| p.file_name().is_some_and(|n| n.to_string_lossy().starts_with("event")))
            .collect();
        v.sort();
        v
    }

    pub fn status() -> GamepadStatus {
        let s = STATE.lock().unwrap();
        GamepadStatus { native: true, running: s.stop.is_some(), devices: s.devices.clone(), denied: s.denied }
    }

    pub fn stop() {
        if let Some(f) = STATE.lock().unwrap().stop.take() {
            f.store(true, Ordering::Relaxed);
        }
    }

    pub fn start(input_dir: PathBuf, emit: impl Fn(PadEvent) + Send + 'static) {
        let mut s = STATE.lock().unwrap();
        if s.stop.is_some() {
            return;
        }
        let stop = Arc::new(AtomicBool::new(false));
        s.stop = Some(stop.clone());
        drop(s);
        std::thread::Builder::new()
            .name("kiln-gamepad".into())
            .spawn(move || run(&input_dir, &stop, &emit))
            .ok();
    }

    fn run(dir: &Path, stop: &AtomicBool, emit: &dyn Fn(PadEvent)) {
        let mut open: Vec<Open> = Vec::new();
        // nodes that opened but are not gamepads: skipped until /dev/input changes. Nodes that did not open are tried
        // again at every scan: udev applies a new pad's access rights (uaccess) a moment after its node appears.
        let mut not_pads: Vec<PathBuf> = Vec::new();
        let mut denied = usize::MAX;
        let mut router = Router::default();
        let mut last_scan: Option<Instant> = None;
        let mut nodes_seen: Vec<PathBuf> = Vec::new();
        let mut raws = Vec::new();
        let mut out = Vec::new();
        while !stop.load(Ordering::Relaxed) {
            let now = Instant::now();
            if last_scan.is_none_or(|t| now.duration_since(t) >= Duration::from_secs(2)) {
                last_scan = Some(now);
                let nodes = event_nodes(dir);
                let mut changed = nodes != nodes_seen;
                if changed {
                    not_pads.retain(|p| nodes.contains(p));
                    nodes_seen = nodes.clone();
                }
                let mut no_access = 0;
                for p in nodes {
                    let id = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
                    if open.iter().any(|o| o.id == id) || not_pads.contains(&p) {
                        continue;
                    }
                    match open_node(&p) {
                        Ok(Some(o)) => {
                            emit(PadEvent::Connected { device: o.id.clone(), name: o.name.clone(), vendor: o.dev.input_id().vendor(), product: o.dev.input_id().product() });
                            crate::commands::app::launcher_log("gamepad", &format!("{} connected: {}", o.id, o.name));
                            open.push(o);
                            changed = true;
                        }
                        Ok(None) => not_pads.push(p),
                        Err(e) => {
                            if e.kind() == std::io::ErrorKind::PermissionDenied && looks_like_gamepad(&p) {
                                no_access += 1;
                            }
                        }
                    }
                }
                if changed || no_access != denied {
                    denied = no_access;
                    publish(&open, denied);
                }
            }
            let mut gone = Vec::new();
            for (i, o) in open.iter_mut().enumerate() {
                match o.dev.fetch_events() {
                    Ok(evs) => {
                        for ev in evs {
                            o.map.feed(ev.event_type().0, ev.code(), ev.value(), &mut raws);
                        }
                    }
                    Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {}
                    Err(_) => gone.push(i),
                }
                for r in raws.drain(..) {
                    router.feed(&o.id, r, now, &mut out);
                }
            }
            for i in gone.into_iter().rev() {
                let mut o = open.remove(i);
                o.map.release_all(&mut raws);
                for r in raws.drain(..) {
                    router.feed(&o.id, r, now, &mut out);
                }
                router.forget(&o.id, now, &mut out);
                out.push(PadEvent::Disconnected { device: o.id.clone(), name: o.name.clone() });
                crate::commands::app::launcher_log("gamepad", &format!("{} disconnected", o.id));
                nodes_seen.clear(); // rescan soon
                publish(&open, denied);
            }
            router.tick(now, &mut out);
            for e in out.drain(..) {
                emit(e);
            }
            std::thread::sleep(Duration::from_millis(8));
        }
        let mut s = STATE.lock().unwrap();
        s.devices.clear();
    }

    fn open_node(p: &Path) -> std::io::Result<Option<Open>> {
        open(p)
    }

    /// udev marks joysticks in /run/udev/data (readable without the device): used only to count gamepads that
    /// could not be opened.
    fn looks_like_gamepad(p: &Path) -> bool {
        let Ok(meta) = std::fs::metadata(p) else { return false };
        use std::os::unix::fs::MetadataExt;
        let rdev = meta.rdev();
        let (major, minor) = (((rdev >> 8) & 0xfff) as u32, ((rdev & 0xff) | ((rdev >> 12) & 0xfff00)) as u32);
        std::fs::read_to_string(format!("/run/udev/data/c{major}:{minor}")).is_ok_and(|t| t.contains("ID_INPUT_JOYSTICK=1"))
    }

    fn publish(open: &[Open], denied: usize) {
        let mut s = STATE.lock().unwrap();
        s.devices = open
            .iter()
            .map(|o| DeviceInfo { device: o.id.clone(), name: o.name.clone(), vendor: o.dev.input_id().vendor(), product: o.dev.input_id().product() })
            .collect();
        s.denied = denied;
    }
}

/// Starts the reader (once; later calls do nothing) and returns what it sees. Not Linux: `native: false`.
pub fn start(emit: impl Fn(PadEvent) + Send + 'static) -> GamepadStatus {
    #[cfg(target_os = "linux")]
    {
        let dir = std::env::var_os("KILN_INPUT_DIR").map(std::path::PathBuf::from).unwrap_or_else(|| "/dev/input".into());
        reader::start(dir, emit);
        reader::status()
    }
    #[cfg(not(target_os = "linux"))]
    {
        let _ = emit;
        GamepadStatus::default()
    }
}

pub fn stop() -> GamepadStatus {
    #[cfg(target_os = "linux")]
    {
        reader::stop();
        let mut s = reader::status();
        s.running = false;
        s
    }
    #[cfg(not(target_os = "linux"))]
    GamepadStatus::default()
}

pub fn status() -> GamepadStatus {
    #[cfg(target_os = "linux")]
    {
        reader::status()
    }
    #[cfg(not(target_os = "linux"))]
    GamepadStatus::default()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed(m: &mut Mapper, evs: &[(u16, u16, i32)]) -> Vec<Raw> {
        let mut out = vec![];
        for &(t, c, v) in evs {
            m.feed(t, c, v, &mut out);
        }
        out
    }

    #[test]
    fn xpad_buttons_map_to_their_faces() {
        let pairs = [
            (0x130, Button::A), (0x131, Button::B), (0x133, Button::X), (0x134, Button::Y), (0x136, Button::Lb), (0x137, Button::Rb),
            (0x13a, Button::Select), (0x13b, Button::Start), (0x13c, Button::Guide), (0x13d, Button::Ls), (0x13e, Button::Rs),
            (0x220, Button::Up), (0x221, Button::Down), (0x222, Button::Left), (0x223, Button::Right),
        ];
        for (code, b) in pairs {
            assert_eq!(button_for_key(code), Some(b), "{code:#x}");
        }
        // BTN_C, keyboard keys, mouse buttons: nothing
        for code in [0x132, 30, 0x110, 0x14a] {
            assert_eq!(button_for_key(code), None);
        }
    }

    #[test]
    fn key_press_release_and_kernel_repeat() {
        let mut m = Mapper::default();
        let out = feed(&mut m, &[(EV_KEY, 0x130, 1), (EV_KEY, 0x130, 2), (EV_KEY, 0x130, 0)]);
        assert_eq!(out, vec![Raw::Press(Source::Key, Button::A), Raw::Release(Source::Key, Button::A)]);
    }

    #[test]
    fn hat_is_four_directions() {
        let mut m = Mapper::default();
        let out = feed(&mut m, &[(EV_ABS, ABS_HAT0Y, -1), (EV_ABS, ABS_HAT0Y, 0), (EV_ABS, ABS_HAT0X, 1), (EV_ABS, ABS_HAT0X, -1), (EV_ABS, ABS_HAT0X, 0)]);
        assert_eq!(out, vec![
            Raw::Press(Source::Hat, Button::Up), Raw::Release(Source::Hat, Button::Up),
            Raw::Press(Source::Hat, Button::Right), Raw::Release(Source::Hat, Button::Right), Raw::Press(Source::Hat, Button::Left),
            Raw::Release(Source::Hat, Button::Left),
        ]);
    }

    #[test]
    fn left_stick_has_hysteresis_and_a_dominant_axis() {
        // the Steam virtual controller: -32768..32767
        let mut m = Mapper::new([(ABS_X, Range { min: -32768, max: 32767 }), (ABS_Y, Range { min: -32768, max: 32767 })]);
        // a little push: nothing
        assert!(feed(&mut m, &[(EV_ABS, ABS_Y, 12000)]).is_empty());
        // past 0.6 down
        assert_eq!(feed(&mut m, &[(EV_ABS, ABS_Y, 24000)]), vec![Raw::Press(Source::Stick, Button::Down)]);
        // back to 0.5: still held (release under 0.4)
        assert!(feed(&mut m, &[(EV_ABS, ABS_Y, 16400)]).is_empty());
        // under 0.4: released
        assert_eq!(feed(&mut m, &[(EV_ABS, ABS_Y, 9000)]), vec![Raw::Release(Source::Stick, Button::Down)]);
        // a diagonal: the larger axis wins
        assert_eq!(feed(&mut m, &[(EV_ABS, ABS_X, -30000), (EV_ABS, ABS_Y, -22000)]), vec![Raw::Press(Source::Stick, Button::Left)]);
        // swinging to up clearly: left released, up pressed
        assert_eq!(feed(&mut m, &[(EV_ABS, ABS_X, -2000), (EV_ABS, ABS_Y, -32000)]), vec![Raw::Release(Source::Stick, Button::Left), Raw::Press(Source::Stick, Button::Up)]);
    }

    #[test]
    fn unsigned_ranges_and_triggers() {
        // a pad reporting 0..255 sticks and 0..1023 triggers
        let mut m = Mapper::new([(ABS_X, Range { min: 0, max: 255 }), (ABS_Y, Range { min: 0, max: 255 }), (ABS_Z, Range { min: 0, max: 1023 })]);
        assert!(feed(&mut m, &[(EV_ABS, ABS_X, 128), (EV_ABS, ABS_Y, 128)]).is_empty());
        assert_eq!(feed(&mut m, &[(EV_ABS, ABS_X, 255)]), vec![Raw::Press(Source::Stick, Button::Right)]);
        assert_eq!(feed(&mut m, &[(EV_ABS, ABS_Z, 900)]), vec![Raw::Press(Source::Trigger, Button::Lt)]);
        assert!(feed(&mut m, &[(EV_ABS, ABS_Z, 500)]).is_empty());
        assert_eq!(feed(&mut m, &[(EV_ABS, ABS_Z, 100)]), vec![Raw::Release(Source::Trigger, Button::Lt)]);
    }

    #[test]
    fn router_merges_sources_and_repeats() {
        let t0 = Instant::now();
        let mut r = Router::default();
        let mut out = vec![];
        r.feed("event5", Raw::Press(Source::Hat, Button::Down), t0, &mut out);
        // the stick also pushes down: no second press
        r.feed("event5", Raw::Press(Source::Stick, Button::Down), t0, &mut out);
        assert_eq!(out, vec![PadEvent::Button { button: Button::Down, pressed: true, repeat: false, device: "event5".into() }]);
        out.clear();
        r.tick(t0 + Duration::from_millis(300), &mut out);
        assert!(out.is_empty(), "no repeat before 380 ms");
        r.tick(t0 + Duration::from_millis(390), &mut out);
        r.tick(t0 + Duration::from_millis(450), &mut out);
        r.tick(t0 + Duration::from_millis(515), &mut out);
        assert_eq!(out.len(), 2, "{out:?}");
        assert!(out.iter().all(|e| matches!(e, PadEvent::Button { button: Button::Down, pressed: true, repeat: true, .. })));
        out.clear();
        // after 1.5 s the repeat speeds up
        let mut n = 0;
        let mut t = t0 + Duration::from_millis(1600);
        r.tick(t, &mut out);
        out.clear();
        for _ in 0..10 {
            t += Duration::from_millis(70);
            r.tick(t, &mut out);
            n += out.drain(..).count();
        }
        assert!(n >= 9, "fast repeat: {n}");
        // one source let go: still held
        r.feed("event5", Raw::Release(Source::Hat, Button::Down), t, &mut out);
        assert!(out.is_empty());
        r.feed("event5", Raw::Release(Source::Stick, Button::Down), t, &mut out);
        assert_eq!(out, vec![PadEvent::Button { button: Button::Down, pressed: false, repeat: false, device: "event5".into() }]);
        out.clear();
        r.tick(t + Duration::from_secs(1), &mut out);
        assert!(out.is_empty(), "no repeat after release");
    }

    #[test]
    fn buttons_do_not_repeat_and_the_newest_direction_repeats() {
        let t0 = Instant::now();
        let mut r = Router::default();
        let mut out = vec![];
        r.feed("e", Raw::Press(Source::Key, Button::A), t0, &mut out);
        r.tick(t0 + Duration::from_secs(2), &mut out);
        assert_eq!(out.len(), 1);
        out.clear();
        r.feed("e", Raw::Press(Source::Hat, Button::Up), t0, &mut out);
        r.feed("e", Raw::Press(Source::Hat, Button::Right), t0 + Duration::from_millis(100), &mut out);
        out.clear();
        r.tick(t0 + Duration::from_millis(500), &mut out);
        assert_eq!(out, vec![PadEvent::Button { button: Button::Right, pressed: true, repeat: true, device: "e".into() }]);
    }

    #[test]
    fn unplugging_releases_what_the_device_held() {
        let t0 = Instant::now();
        let mut r = Router::default();
        let mut out = vec![];
        r.feed("event9", Raw::Press(Source::Hat, Button::Left), t0, &mut out);
        r.feed("event9", Raw::Press(Source::Key, Button::Lb), t0, &mut out);
        out.clear();
        r.forget("event9", t0, &mut out);
        assert_eq!(out.len(), 2);
        assert!(out.iter().all(|e| matches!(e, PadEvent::Button { pressed: false, .. })));
        out.clear();
        r.tick(t0 + Duration::from_secs(1), &mut out);
        assert!(out.is_empty());
    }

    #[test]
    fn right_stick_scrolls_throttled_then_stops() {
        let t0 = Instant::now();
        let mut r = Router::default();
        let mut out = vec![];
        r.feed("e", Raw::Axis(Axis::Ry, 0.1), t0, &mut out);
        assert!(out.is_empty(), "inside the deadzone");
        r.feed("e", Raw::Axis(Axis::Ry, 0.8), t0, &mut out);
        r.feed("e", Raw::Axis(Axis::Ry, 0.9), t0 + Duration::from_millis(10), &mut out);
        assert_eq!(out.len(), 1);
        // held still: the tick keeps sending the last value
        r.tick(t0 + Duration::from_millis(45), &mut out);
        assert_eq!(out.len(), 2);
        assert!(matches!(out[1], PadEvent::Axis { axis: Axis::Ry, value, .. } if (value - 0.9).abs() < 1e-6));
        r.feed("e", Raw::Axis(Axis::Ry, 0.0), t0 + Duration::from_millis(50), &mut out);
        assert!(matches!(out[2], PadEvent::Axis { value, .. } if value == 0.0));
        out.clear();
        r.tick(t0 + Duration::from_secs(1), &mut out);
        assert!(out.is_empty());
    }

    /// End to end through the kernel: a uinput pad (xpad-like, a test vendor id, no Guide button) pressed while the real
    /// reader runs. Opt-in: it creates a system-wide input device for a few seconds, so it needs /dev/uinput access and
    /// should not run while a game has focus.
    ///   cargo test -p kiln-launcher deck::gamepad::tests::uinput_pad_end_to_end -- --ignored
    #[test]
    #[ignore]
    #[cfg(target_os = "linux")]
    fn uinput_pad_end_to_end() {
        use evdev::uinput::VirtualDevice;
        use evdev::{AbsInfo, AbsoluteAxisCode, AttributeSet, BusType, InputEvent, InputId, KeyCode, UinputAbsSetup};
        use std::sync::{Arc, Mutex};
        let keys: AttributeSet<KeyCode> = [KeyCode::BTN_SOUTH, KeyCode::BTN_EAST, KeyCode::BTN_NORTH, KeyCode::BTN_WEST, KeyCode::BTN_TL, KeyCode::BTN_TR,
            KeyCode::BTN_SELECT, KeyCode::BTN_START].into_iter().collect();
        let stick = AbsInfo::new(0, -32768, 32767, 16, 128, 0);
        let hat = AbsInfo::new(0, -1, 1, 0, 0, 0);
        let mut b = VirtualDevice::builder().expect("/dev/uinput").name("Kiln Deck Test Pad (cargo test)")
            .input_id(InputId::new(BusType::BUS_USB, 0x1209, 0x4b4d, 1)).with_keys(&keys).unwrap();
        for (code, info) in [(AbsoluteAxisCode::ABS_X, stick), (AbsoluteAxisCode::ABS_Y, stick), (AbsoluteAxisCode::ABS_RX, stick),
            (AbsoluteAxisCode::ABS_RY, stick), (AbsoluteAxisCode::ABS_HAT0X, hat), (AbsoluteAxisCode::ABS_HAT0Y, hat)] {
            b = b.with_absolute_axis(&UinputAbsSetup::new(code, info)).unwrap();
        }
        let mut pad = b.build().unwrap();
        let got: Arc<Mutex<Vec<PadEvent>>> = Arc::default();
        let sink = got.clone();
        let st = start(move |e| sink.lock().unwrap().push(e));
        assert!(st.native);
        // the reader scans /dev/input every 2 s
        let deadline = Instant::now() + Duration::from_secs(6);
        while !got.lock().unwrap().iter().any(|e| matches!(e, PadEvent::Connected { name, .. } if name.contains("cargo test"))) {
            assert!(Instant::now() < deadline, "the reader never saw the pad (permissions?): {:?}", status());
            std::thread::sleep(Duration::from_millis(100));
        }
        let ev = |t: u16, c: u16, v: i32| InputEvent::new(t, c, v);
        let syn = || InputEvent::new(0, 0, 0);
        let press = |pad: &mut VirtualDevice, code: KeyCode| {
            pad.emit(&[ev(EV_KEY, code.0, 1), syn()]).unwrap();
            std::thread::sleep(Duration::from_millis(60));
            pad.emit(&[ev(EV_KEY, code.0, 0), syn()]).unwrap();
            std::thread::sleep(Duration::from_millis(60));
        };
        press(&mut pad, KeyCode::BTN_TR);
        press(&mut pad, KeyCode::BTN_SOUTH);
        press(&mut pad, KeyCode::BTN_NORTH);
        // the D-pad held down for 700 ms: a press and repeats
        pad.emit(&[ev(EV_ABS, ABS_HAT0Y, 1), syn()]).unwrap();
        std::thread::sleep(Duration::from_millis(700));
        pad.emit(&[ev(EV_ABS, ABS_HAT0Y, 0), syn()]).unwrap();
        // the left stick pushed left
        pad.emit(&[ev(EV_ABS, ABS_X, -30000), syn()]).unwrap();
        std::thread::sleep(Duration::from_millis(100));
        pad.emit(&[ev(EV_ABS, ABS_X, 0), syn()]).unwrap();
        std::thread::sleep(Duration::from_millis(200));
        drop(pad);
        std::thread::sleep(Duration::from_millis(2500));
        stop();
        let evs = got.lock().unwrap().clone();
        let presses: Vec<(Button, bool)> = evs.iter().filter_map(|e| match e {
            PadEvent::Button { button, pressed: true, repeat, .. } => Some((*button, *repeat)),
            _ => None,
        }).collect();
        let first: Vec<Button> = presses.iter().filter(|p| !p.1).map(|p| p.0).collect();
        assert_eq!(first, vec![Button::Rb, Button::A, Button::X, Button::Down, Button::Left], "{evs:?}");
        let repeats = presses.iter().filter(|p| p.1 && p.0 == Button::Down).count();
        assert!((1..=4).contains(&repeats), "repeats of a 700 ms hold: {repeats}");
        assert!(evs.iter().any(|e| matches!(e, PadEvent::Disconnected { .. })), "unplugging is reported");
    }

    #[test]
    fn events_serialize_for_the_frontend() {
        let e = PadEvent::Button { button: Button::Lb, pressed: true, repeat: false, device: "event3".into() };
        assert_eq!(serde_json::to_value(&e).unwrap(), serde_json::json!({"kind": "button", "button": "lb", "pressed": true, "repeat": false, "device": "event3"}));
        let e = PadEvent::Axis { axis: Axis::Ry, value: -0.5, device: "event3".into() };
        assert_eq!(serde_json::to_value(&e).unwrap(), serde_json::json!({"kind": "axis", "axis": "ry", "value": -0.5, "device": "event3"}));
        let e = PadEvent::Connected { device: "event3".into(), name: "Steam Deck".into(), vendor: 0x28de, product: 0x1205 };
        assert_eq!(serde_json::to_value(&e).unwrap()["kind"], "connected");
    }
}
