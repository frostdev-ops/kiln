//! The estimate against the owner's start of 13 Nov 2026 (the contract's timeline table: 4 Kiln mods, ShroudForge mods,
//! three native mods; cook 79 s, Steam until 85 s, Kiln's boot 30.8 s with kiln-vfx silent for 19 s, the main menu at
//! +61.6 s), replayed second by second as progress.json would say it.

use super::*;

const BASE: i64 = 1_794_590_812_000; // 2026-11-13T17:26:52Z, Play
const KILN: &str = "0.2.0-dev.27+20261113.2e75c1f";
const FP: &str = "kms1:owner";

/// One runtime step of the fixture: its duration, its items' end times (ms into the step), its parts.
struct RStep {
    id: &'static str,
    label: &'static str,
    dur: u64,
    items: Vec<u64>,
    detail: Option<&'static str>,
    parts: Vec<(&'static str, u64)>,
}

struct Owner {
    cook: Vec<(&'static str, &'static str, u64)>,
    profile: u64,
    steam: u64,
    boot_delay: u64,
    runtime: Vec<RStep>,
    /// The step that loads/finishes the boot (state `loading` after it).
    loading_after: &'static str,
    /// The kms1 of the start.
    fp: &'static str,
}

fn even(n: u64, dur: u64) -> Vec<u64> {
    (1..=n).map(|i| dur * i / n).collect()
}

impl Owner {
    fn start() -> Owner {
        // 31 services: 13 quick ones, kiln-vfx silent for 18 s (85 materials), 17 quick ones
        let mut services: Vec<u64> = (1..=13).map(|i| i * 20).collect();
        services.push(18_250);
        services.extend((1..=17).map(|i| 18_250 + i * 29));
        Owner {
            cook: vec![("kiln-equipment@kdh1:aa", "Kiln Equipment", 26_000), ("big-dummies@kdh1:bb", "BetterDummies", 26_000), ("watering-can@kdh1:cc", "Large Watering Can", 27_000)],
            profile: 1_000,
            steam: 5_000,
            boot_delay: 100,
            runtime: vec![
                RStep { id: "start", label: "Reading Kiln's settings and the profile", dur: 95, items: vec![], detail: None, parts: vec![] },
                RStep { id: "native-early", label: "Loading native mods before the hooks", dur: 605, items: even(3, 605), detail: None, parts: vec![] },
                RStep { id: "early-plugins", label: "Starting ShroudForge", dur: 9_500, items: vec![], detail: Some("kiln-shroudforge.dll"), parts: vec![("Starting ShroudForge", 9_400)] },
                RStep { id: "signatures", label: "Finding the game's functions", dur: 50, items: vec![], detail: None, parts: vec![] },
                RStep { id: "services", label: "Starting Kiln's services", dur: 18_750, items: services, detail: Some("kiln-vfx: building 85 materials"), parts: vec![("kiln-vfx", 18_000)] },
                RStep { id: "plugins", label: "Loading Kiln's plugins", dur: 800, items: even(10, 800), detail: None, parts: vec![] },
                RStep { id: "native", label: "Loading native mods", dur: 500, items: even(3, 500), detail: None, parts: vec![] },
                RStep { id: "dotnet", label: "Starting .NET", dur: 512, items: vec![], detail: None, parts: vec![] },
                RStep { id: "mods", label: "Loading the mods", dur: 6_788, items: even(4, 6_788), detail: Some("Kiln Equipment"), parts: vec![] },
                RStep { id: "content", label: "Applying the mods' content", dur: 12_400, items: even(40, 12_400), detail: Some("blocks"), parts: vec![] },
                RStep { id: "game", label: "The game is loading", dur: 11_600, items: vec![], detail: None, parts: vec![] },
            ],
            loading_after: "dotnet",
            fp: FP,
        }
    }

    /// The same start with one step `factor` times as long (its items stretched alike).
    fn slower(mut self, id: &str, factor: u64) -> Owner {
        for s in self.runtime.iter_mut().filter(|s| s.id == id) {
            s.dur *= factor;
            s.items.iter_mut().for_each(|i| *i *= factor);
        }
        if let Some(c) = self.cook.iter_mut().find(|c| c.0 == id) {
            c.2 *= factor;
        }
        self
    }

    fn cook_ms(&self) -> u64 {
        self.cook.iter().map(|c| c.2).sum()
    }
    fn process_at(&self) -> u64 {
        self.cook_ms() + self.profile + self.steam
    }
    fn boot_at(&self) -> u64 {
        self.process_at() + self.boot_delay
    }
    fn end(&self) -> u64 {
        self.boot_at() + self.runtime.iter().map(|s| s.dur).sum::<u64>()
    }

    /// The launcher's record of the start at `t` ms after Play.
    fn launcher(&self, t: u64) -> LauncherStart {
        let keys = self.cook.iter().map(|c| c.0.to_string()).collect();
        let mut l = LauncherStart::new(BASE, Some(keys), Some(KILN.into()), Some(self.fp.into()));
        let mut at = 0;
        for (i, (key, name, dur)) in self.cook.iter().enumerate() {
            if at > t {
                return l;
            }
            l.cook_event(BASE + at as i64, i, self.cook.len(), key, name, None);
            if at + 1_000 <= t {
                l.cook_event(BASE + (at + 1_000) as i64, i, self.cook.len(), key, name, Some("kiln: cooking assets/cook.json (modelcook)"));
            }
            at += dur;
        }
        if at > t {
            return l;
        }
        l.cook_done(BASE + at as i64);
        l.begin("profile", BASE + at as i64);
        at += self.profile;
        if at > t {
            return l;
        }
        l.begin("steam", BASE + at as i64);
        l.asked = true;
        l.seen = t >= self.process_at();
        l
    }

    /// progress.json at `t` ms after Play (None before Kiln's boot began).
    fn progress(&self, t: u64) -> Option<Progress> {
        let r = t.checked_sub(self.boot_at())?;
        let ready = t >= self.end();
        let mut steps = Vec::new();
        let mut begin = 0;
        let mut loading = false;
        for s in &self.runtime {
            if begin > r && !ready {
                break;
            }
            let ended = begin + s.dur <= r;
            let into = r.saturating_sub(begin);
            let done = (!s.items.is_empty()).then(|| s.items.iter().filter(|&&i| i <= into).count() as u64);
            steps.push(ProgressStep {
                id: s.id.into(),
                label: s.label.into(),
                detail: if ended { None } else { s.detail.map(str::to_string) },
                done: if ended { Some(s.items.len() as u64).filter(|n| *n > 0) } else { done },
                total: (!s.items.is_empty()).then_some(s.items.len() as u64),
                ms: begin,
                took_ms: ended.then_some(s.dur),
                parts: if ended { s.parts.iter().map(|(l, ms)| ProgressPart { label: l.to_string(), took_ms: *ms }).collect() } else { vec![] },
            });
            if ended && s.id == self.loading_after {
                loading = true;
            }
            begin += s.dur;
        }
        Some(Progress {
            format: 1,
            pid: 392,
            kiln: Some(KILN.into()),
            side: Some("client".into()),
            started: fmt_ms(BASE + self.boot_at() as i64),
            at_ms: r.min(self.end() - self.boot_at()),
            state: if ready { "ready" } else if loading { "loading" } else { "booting" }.into(),
            plan: self.runtime.iter().map(|s| s.id.to_string()).collect(),
            steps,
            why: None,
        })
    }

    fn at(&self, t: u64, history: &[KeptStart]) -> (Timeline, LauncherStart, Option<Progress>) {
        let l = self.launcher(t);
        let p = self.progress(t);
        let now = BASE + t as i64;
        let tl = timeline(&Inputs {
            launcher: Some(&l),
            progress: p.as_ref(),
            written: p.as_ref().map(|_| now),
            process: (t >= self.process_at()).then(|| BASE + self.process_at() as i64),
            running: t >= self.process_at(),
            boot: t >= self.boot_at() + 30_812,
            now,
            history,
            kiln: Some(KILN),
            fingerprint: Some(self.fp),
        }).expect("a start of this launcher");
        (tl, l, p)
    }

    /// Plays the start second by second; returns the kept start and every second's timeline.
    fn play(&self, history: &[KeptStart]) -> (KeptStart, Vec<(u64, Timeline)>) {
        let mut seen = Vec::new();
        let mut t = 0;
        loop {
            let (tl, l, p) = self.at(t, history);
            seen.push((t, tl.clone()));
            if tl.phase == Phase::Ready {
                let kept = kept_from(&tl, p.as_ref().unwrap(), KILN, FP, BASE + t as i64, Some(&l)).expect("kept at ready");
                return (kept, seen);
            }
            t = (t + 1_000).min(self.end());
        }
    }
}

fn secs(ms: Option<u64>) -> String {
    ms.map_or("none".into(), |m| format!("{:.1} s", m as f64 / 1000.0))
}

#[test]
fn the_owners_first_start_names_its_steps_and_gives_no_number() {
    let o = Owner::start();
    assert_eq!(o.end(), 146_700, "Play to the main menu: two and a half minutes");
    let (kept, seen) = o.play(&[]);
    let mut last = 0.0;
    for (t, tl) in &seen {
        assert!(tl.phase == Phase::Ready || tl.estimate.left_ms.is_none(), "{t}: no basis, no number: {:?}", tl.estimate);
        assert!(!tl.estimate.by_time || tl.phase == Phase::Ready);
        assert!(tl.estimate.fraction >= last, "{t}: the bar never moves back ({} < {last})", tl.estimate.fraction);
        last = tl.estimate.fraction;
        if tl.phase != Phase::Ready {
            assert!(tl.estimate.fraction < 1.0);
        }
    }
    // what the window has to say at a few points
    let at = |s: u64| &seen.iter().find(|(t, _)| *t == s * 1000).unwrap().1;
    let cook = at(30);
    assert_eq!(cook.phase, Phase::Launcher);
    let r = &cook.steps[cook.running.unwrap()];
    assert_eq!((r.id.as_str(), r.label.as_str(), r.done, r.total), ("cook", "Cooking BetterDummies (2 of 3)", Some(1), Some(3)));
    assert_eq!(r.detail.as_deref(), Some("kiln: cooking assets/cook.json (modelcook)"));
    assert_eq!(cook.estimate.steps_planned, 3 + 11, "before the runtime speaks: the contract's list is counted");
    let services = at(105);
    let r = &services.steps[services.running.unwrap()];
    assert_eq!((services.phase, r.id.as_str(), r.detail.as_deref(), r.done, r.total),
               (Phase::Booting, "services", Some("kiln-vfx: building 85 materials"), Some(13), Some(31)));
    assert_eq!(services.estimate.steps_planned, 14);
    assert_eq!(services.steps.iter().filter(|s| s.state == StepState::Done).count(), 7, "cook, profile, steam, start, native-early, early-plugins, signatures");
    assert_eq!(at(120).phase, Phase::Loading, "boot.json is written, the game goes on loading: not Running yet");
    // what is kept: every step with its time, the cook per mod
    assert_eq!(kept.steps.iter().map(|s| s.id.as_str()).collect::<Vec<_>>(),
               ["cook", "profile", "steam", "start", "native-early", "early-plugins", "signatures", "services", "plugins", "native", "dotnet", "mods", "content", "game"]);
    assert_eq!(kept.step("cook").unwrap().ms, 79_000);
    assert_eq!(kept.step("cook").unwrap().parts.iter().map(|p| (p.key.as_str(), p.ms)).collect::<Vec<_>>(),
               [("kiln-equipment@kdh1:aa", 26_000), ("big-dummies@kdh1:bb", 26_000), ("watering-can@kdh1:cc", 27_000)]);
    assert_eq!(kept.step("steam").unwrap().ms, 5_000);
    assert_eq!(kept.step("services").unwrap().total, Some(31));
    assert_eq!(kept.step("early-plugins").unwrap().parts, [KeptPart { key: "Starting ShroudForge".into(), label: None, ms: 9_400 }]);
    assert_eq!(kept.total_ms(), 146_600, "the 100 ms from the process to Kiln's boot is no step");
}

#[test]
fn the_second_identical_start_is_within_a_few_seconds_at_every_point() {
    let o = Owner::start();
    let (first, _) = o.play(&[]);
    let (_, seen) = o.play(std::slice::from_ref(&first));
    let mut worst = (0, 0i64);
    let mut last = 0.0;
    eprintln!("second start: t, estimate, real time left");
    for (t, tl) in &seen {
        let real = o.end() as i64 - *t as i64;
        let left = tl.estimate.left_ms.unwrap_or_else(|| panic!("{t}: a number at every point: {:?}", tl.estimate)) as i64;
        if (left - real).abs() > worst.1.abs() {
            worst = (*t, left - real);
        }
        assert!(tl.estimate.fraction >= last, "{t}: the bar never moves back");
        last = tl.estimate.fraction;
        assert_eq!((tl.estimate.starts, tl.estimate.same), (1, true), "{t}: from your last start");
        assert!(tl.estimate.slow.is_none(), "{t}");
        if [5, 60, 90, 110, 130].contains(&(t / 1000)) && t % 1000 == 0 {
            let r = tl.running.map(|i| tl.steps[i].id.clone()).unwrap_or_default();
            eprintln!("  {:>5.0} s ({r:>13}): {:>7} against {:>7}", *t as f64 / 1000.0, secs(Some(left as u64)), secs(Some(real as u64)));
        }
    }
    eprintln!("  worst: {:+.1} s at {} s", worst.1 as f64 / 1000.0, worst.0 / 1000);
    assert!(worst.1.abs() <= 3_000, "within 3 s at every point: {worst:?}");
}

#[test]
fn the_contracts_larger_of_rate_and_usual_is_far_off_while_kiln_vfx_works() {
    // the evidence for RATE_RULE: with the rate always counted, 13 of 31 services done in 0.3 s and the 14th taking 18 s
    // make "elapsed * 18 / 13" say up to 25 s when less than one second is left
    let o = Owner::start();
    let (first, _) = o.play(&[]);
    let hist = [first];
    let mut worst = 0i64;
    let mut t = 0;
    while t < o.end() {
        let (tl, l, p) = o.at(t, &hist);
        let steps = steps_now(&tl, &l, p.as_ref());
        let plan: Vec<String> = tl.steps.iter().map(|s| s.id.clone()).collect();
        let e = estimate_by(&plan, &steps, tl.at_ms, &hist, Some(KILN), Some(FP), false, RateRule::Larger);
        worst = worst.max(e.left_ms.unwrap() as i64 - (o.end() - t) as i64);
        t += 1_000;
    }
    eprintln!("RateRule::Larger: worst over-estimate {:.1} s", worst as f64 / 1000.0);
    assert!(worst > 15_000, "{worst}");
}

/// The estimate's input as `timeline` builds it (for the rule comparison).
fn steps_now(tl: &Timeline, l: &LauncherStart, _p: Option<&Progress>) -> Vec<StepNow> {
    tl.steps.iter().filter(|s| s.ms.is_some()).map(|s| {
        let ls = l.steps.iter().find(|x| x.id == s.id);
        StepNow { id: s.id.clone(), ms: s.ms.unwrap(), took_ms: s.took_ms, done: s.done, total: s.total,
            keys: ls.map(|x| x.keys.clone()).unwrap_or_default(), parts: ls.map(|x| x.parts.clone()).unwrap_or_default(),
            part_ms: ls.map_or(s.ms.unwrap(), |x| x.part_ms) }
    }).collect()
}

#[test]
fn a_start_with_one_step_twice_as_slow_says_so_and_stops_counting_down() {
    let o = Owner::start();
    let (first, _) = o.play(&[]);
    let slow = Owner::start().slower("services", 2);
    let (_, seen) = slow.play(std::slice::from_ref(&first));
    let services_at = slow.boot_at() + 95 + 605 + 9_500 + 50;
    let after: u64 = 800 + 500 + 512 + 6_788 + 12_400 + 11_600;
    let mut frozen = Vec::new();
    for (t, tl) in &seen {
        let into = t.saturating_sub(services_at);
        let in_services = tl.running.is_some_and(|r| tl.steps[r].id == "services");
        if in_services && into >= 18_750 * 3 / 2 {
            assert_eq!(tl.estimate.slow, Some(Slow { id: "services".into(), usual_ms: 18_750 }), "{t}");
            frozen.push(tl.estimate.left_ms.unwrap());
        } else {
            assert!(tl.estimate.slow.is_none(), "{t}");
        }
        if in_services && into >= 18_750 {
            assert_eq!(tl.estimate.left_ms, Some(after), "{t}: the services' share is spent; what the steps after take stays");
        }
        if !in_services && *t > services_at && tl.phase != Phase::Ready {
            let real = (slow.end() - t) as i64;
            assert!((tl.estimate.left_ms.unwrap() as i64 - real).abs() <= 3_000, "{t}: back on time after it");
        }
    }
    assert!(frozen.len() >= 8, "about 9 s of 'taking longer than usual': {}", frozen.len());
    let (t, tl) = seen.iter().find(|(t, _)| *t == services_at - services_at % 1000 + 30_000).unwrap();
    eprintln!("services 2x, {:.0} s: {} left, slow {:?} (real {})", *t as f64 / 1000.0, secs(tl.estimate.left_ms), tl.estimate.slow, secs(Some(slow.end() - t)));
}

#[test]
fn a_mods_cook_has_its_own_history_so_the_second_start_says_how_long_it_still_cooks() {
    let o = Owner::start();
    let (first, _) = o.play(&[]);
    // the second start cooks only Kiln Equipment (the others are cooked): its own time counts, not the step's 79 s
    let mut two = Owner::start();
    two.cook.truncate(1);
    let (tl, _, _) = two.at(10_000, std::slice::from_ref(&first));
    let cook = &tl.steps[tl.running.unwrap()];
    assert_eq!((cook.label.as_str(), cook.usual_ms), ("Cooking Kiln Equipment", Some(26_000)));
    assert_eq!(tl.estimate.left_ms, Some(16_000 + 1_000 + 5_000 + 61_600), "the rest of its cook, then the usual times");
    assert!(tl.estimate.same);
    // the mod changed (another identity, so another set of mods): its cook has no history of its own; the cook step's
    // history of this install counts, scaled from three mods to one, and every other step's too
    let mut new = Owner::start();
    new.cook = vec![("kiln-equipment@kdh1:changed", "Kiln Equipment", 30_000)];
    new.fp = "kms1:changed";
    let (tl, _, _) = new.at(10_000, std::slice::from_ref(&first));
    assert_eq!(tl.estimate.left_ms, Some(79_000 / 3 - 10_000 + 1_000 + 5_000 + 61_600));
    assert!(!tl.estimate.same && tl.estimate.starts == 1);
    // no history at all for a step still to come: no number
    let (tl, _, _) = new.at(10_000, &[]);
    assert_eq!(tl.estimate.left_ms, None);
}

#[test]
fn usual_times_take_the_same_kiln_and_mods_first_then_the_install_scaled_by_total() {
    let rec = |kiln: &str, fp: &str, ms: u64, total: Option<u64>| KeptStart { kiln: kiln.into(), fingerprint: fp.into(), finished: String::new(),
        started: String::new(), steps: vec![KeptStep { id: "mods".into(), ms, total, parts: vec![] }] };
    let h = vec![rec("a", "x", 10_000, Some(4)), rec("a", "y", 50_000, Some(20)), rec("a", "x", 14_000, Some(4)), rec("b", "x", 30_000, Some(4))];
    assert_eq!(usual(&h, "mods", Some(4), &[], Some("a"), Some("x")), Some(Usual { ms: 12_000, starts: 2, same: true }), "the median of the two");
    // another set of mods: every kept start of the install, each scaled to 8 mods
    assert_eq!(usual(&h, "mods", Some(8), &[], Some("a"), Some("z")), Some(Usual { ms: 24_000, starts: 4, same: false }),
               "20 000, 20 000, 28 000, 60 000: the median of the middle two");
    assert_eq!(usual(&h, "content", None, &[], Some("a"), Some("x")), None);
    assert_eq!(usual(&[], "mods", None, &[], None, None), None);
    // only the last five count
    let many: Vec<KeptStart> = (1..=8).map(|i| rec("a", "x", i * 1000, None)).collect();
    assert_eq!(usual(&many, "mods", None, &[], Some("a"), Some("x")).unwrap(), Usual { ms: 6_000, starts: 5, same: true });
}

#[test]
fn progress_json_is_read_as_the_contract_writes_it() {
    let text = r#"{
      "format": 1, "pid": 392, "kiln": "0.2.0-dev.27+20261113.2e75c1f", "side": "client",
      "started": "2026-11-13T17:28:19.975Z", "atMs": 30812, "state": "booting",
      "plan": ["start", "native-early", "early-plugins", "signatures", "services", "plugins", "native", "dotnet", "mods", "content", "game"],
      "steps": [
        { "id": "start", "label": "Reading Kiln's settings and the profile", "ms": 0, "tookMs": 95 },
        { "id": "early-plugins", "label": "Starting ShroudForge", "detail": "kiln-shroudforge.dll", "ms": 700, "tookMs": 9500,
          "parts": [ { "label": "Starting ShroudForge", "tookMs": 9400 } ] },
        { "id": "services", "label": "Starting Kiln's services", "detail": "kiln-vfx: building 85 materials", "done": 14, "total": 31, "ms": 10200 }
      ],
      "why": null, "somethingNewer": true
    }"#;
    let p = parse_progress(&format!("\u{feff}{text}")).expect("format 1");
    assert_eq!(parse_ms(&p.started), Some(parse_ms("2026-11-13T17:28:19Z").unwrap() + 975));
    assert_eq!((p.steps.len(), p.steps[2].done, p.steps[2].took_ms), (3, Some(14), None));
    assert!(parse_progress(&text.replace("\"format\": 1", "\"format\": 2")).is_none(), "a newer format is not guessed at");
    assert!(parse_progress("{").is_none());
    // the window's sentence for it: the running step, its detail and its count
    let tl = timeline(&Inputs { progress: Some(&p), written: Some(parse_ms("2026-11-13T17:28:50Z").unwrap()), running: true,
        now: parse_ms("2026-11-13T17:28:51Z").unwrap(), ..Default::default() }).unwrap();
    assert!(!tl.launcher && tl.phase == Phase::Booting);
    let r = &tl.steps[tl.running.unwrap()];
    assert_eq!((r.label.as_str(), r.detail.as_deref(), r.done, r.total), ("Starting Kiln's services", Some("kiln-vfx: building 85 materials"), Some(14), Some(31)));
    assert_eq!(tl.steps.iter().map(|s| s.state).collect::<Vec<_>>()[..5],
               [StepState::Done, StepState::Skipped, StepState::Done, StepState::Skipped, StepState::Running], "planned steps it passed over");
    assert_eq!(fmt_ms(parse_ms(&p.started).unwrap()), "2026-11-13T17:28:19.975Z");
}

#[test]
fn silence_idle_failure_an_ended_game_and_a_runtime_without_progress_are_not_progress() {
    let o = Owner::start();
    let t = o.boot_at() + 15_000; // in the services
    let p = o.progress(t).unwrap();
    let l = o.launcher(t);
    let now = BASE + t as i64;
    let base = Inputs { launcher: Some(&l), progress: Some(&p), written: Some(now), process: Some(BASE + o.process_at() as i64), running: true, now,
        ..Default::default() };
    assert_eq!(timeline(&base).unwrap().phase, Phase::Booting);
    assert_eq!(timeline(&Inputs { written: Some(now - 60_000), ..base.clone() }).unwrap().phase, Phase::Quiet, "60 s without a write");
    assert_eq!(timeline(&Inputs { written: Some(now - 59_000), ..base.clone() }).unwrap().quiet_ms, Some(59_000));
    let early = o.progress(o.boot_at() + 3_000).unwrap();
    let e = Inputs { progress: Some(&early), written: Some(now - 90_000), ..base.clone() };
    assert_eq!(timeline(&e).unwrap().phase, Phase::Booting, "inside early-plugins 120 s");
    assert_eq!(timeline(&Inputs { written: Some(now - 120_000), ..e }).unwrap().phase, Phase::Quiet);
    assert_eq!(timeline(&Inputs { running: false, ..base.clone() }).unwrap().phase, Phase::Ended);
    let mut idle = p.clone();
    idle.state = "idle".into();
    idle.why = Some("the game's code is not the one Kiln knows (build 26430625)".into());
    let tl = timeline(&Inputs { progress: Some(&idle), ..base.clone() }).unwrap();
    assert_eq!((tl.phase, tl.why.as_deref()), (Phase::Idle, idle.why.as_deref()));
    assert!(kept_from(&tl, &idle, KILN, FP, now, Some(&l)).is_none(), "only a start that reached ready is kept");
    let mut failed = p.clone();
    failed.state = "failed".into();
    failed.why = Some("x".into());
    assert_eq!(timeline(&Inputs { progress: Some(&failed), ..base.clone() }).unwrap().phase, Phase::Failed);
    // a dev.26 runtime: the process runs, boot.json comes, progress.json never does
    let l = o.launcher(o.boot_at() + 31_000);
    let legacy = Inputs { launcher: Some(&l), progress: None, written: None, boot: true, ..base.clone() };
    assert_eq!(timeline(&legacy).unwrap().phase, Phase::Legacy);
    assert_eq!(timeline(&Inputs { boot: false, ..legacy.clone() }).unwrap().phase, Phase::Waiting);
    // nothing of this launcher and no progress.json: nothing to show (the window as before)
    assert!(timeline(&Inputs { launcher: None, ..legacy }).is_none());
}

#[test]
fn the_history_keeps_ten_starts_per_install_once_each_under_its_lock() {
    let root = std::env::temp_dir().join(format!("kiln-start-times-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    let dirs = DataDirs::at(&root);
    let o = Owner::start();
    let (first, _) = o.play(&[]);
    for i in 0..12 {
        let mut k = first.clone();
        k.started = format!("2026-11-13T17:{i:02}:00.000Z");
        assert!(keep(&dirs, "client-a", k).unwrap());
    }
    let mut again = first.clone();
    again.started = "2026-11-13T17:11:00.000Z".into();
    assert!(!keep(&dirs, "client-a", again).unwrap(), "the same start is kept once (the watcher and the window both see ready)");
    assert!(keep(&dirs, "client-b", first.clone()).unwrap());
    let h = read_history(&dirs);
    assert_eq!(h.installs["client-a"].len(), KEEP);
    assert_eq!(h.installs["client-a"][0].started, "2026-11-13T17:02:00.000Z", "the oldest go");
    assert_eq!(history_of(&dirs, "client-b").len(), 1);
    assert!(history_path(&dirs).ends_with("state/start-times.json"));
    // another format is never overwritten; a broken file only loses the estimates
    std::fs::write(history_path(&dirs), r#"{"format":2,"installs":{}}"#).unwrap();
    assert!(!keep(&dirs, "client-a", first.clone()).unwrap());
    assert!(read_history(&dirs).installs.is_empty());
    std::fs::write(history_path(&dirs), "not json").unwrap();
    assert!(keep(&dirs, "client-a", first).unwrap());
    assert_eq!(history_of(&dirs, "client-a").len(), 1);
    let _ = std::fs::remove_dir_all(&root);
}
