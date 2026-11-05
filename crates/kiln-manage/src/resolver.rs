//! A port of managed/Kiln.Host/Resolver.cs: which mods load, in which order, and why the others do not.
//! Parity: schema/fixtures/resolver (the same inputs and expected results the C# FixtureTests check). `rejected` is
//! compared as a set (sorted by dir, then id); reasons must match Resolver.cs's texts exactly.

use crate::manifest::{Dependency, KILN_ID, KilnManifest, RunSide, Side};
use crate::semver::{SemVersion, VersionRange};
use crate::{Error, ErrorCode, Result};
use std::collections::{BTreeMap, BTreeSet, VecDeque};
use serde::{Deserialize, Serialize};

/// A mod folder: its manifest, or why it could not be read (the manifest error text).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Candidate {
    pub dir: String,
    pub manifest: Option<KilnManifest>,
    pub error: Option<String>,
}

impl Candidate {
    /// Manifest id, else the folder name.
    pub fn id(&self) -> String {
        match &self.manifest {
            Some(m) => m.id.clone(),
            None => self.dir.trim_end_matches(['/', '\\']).rsplit(['/', '\\']).next().unwrap_or("").to_string(),
        }
    }
}

/// What the resolver checks against (fixture "env").
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Environment {
    /// Kiln core version (a dev build counts as its release for `kiln` dependencies).
    pub kiln: String,
    /// Kiln.Mods.ApiVersion
    pub api: String,
    pub side: RunSide,
    /// Features that are off; any other feature is available.
    #[serde(default)]
    pub unavailable_features: Vec<String>,
    /// Mods already running (resolve against them; they are not reordered).
    #[serde(default)]
    pub loaded: Vec<KilnManifest>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ModState {
    Pending,
    Loaded,
    Disabled,
    Failed,
    Skipped,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Rejection {
    pub id: String,
    pub dir: String,
    pub state: ModState,
    pub reason: String,
}

/// Fixture "expect": load order (ids) and the rejected mods.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Resolution {
    pub order: Vec<String>,
    pub rejected: Vec<Rejection>,
}

impl Candidate {
    /// The folder name (`Path.GetFileName(Directory.TrimEnd('/', '\\'))`).
    pub fn folder(&self) -> String {
        self.dir.trim_end_matches(['/', '\\']).rsplit(['/', '\\']).next().unwrap_or("").to_string()
    }
}

/// A manifest with its parsed version and ranges (Manifest.cs keeps them; KilnManifest keeps the texts).
struct Parsed<'a> {
    m: &'a KilnManifest,
    version: SemVersion,
    api: VersionRange,
    deps: Vec<(&'a Dependency, VersionRange)>,
    incompatible: Vec<(&'a str, VersionRange)>,
}

fn parsed(m: &KilnManifest) -> Result<Parsed<'_>> {
    let bad = |what: &str, e: Error| Error::new(ErrorCode::Schema, format!("mod {}: {what}: {}", m.id, e.message));
    let version = SemVersion::parse(&m.parsed_version).map_err(|e| bad("version", e))?;
    let api = match SemVersion::try_parse(&m.api_version) {
        Some(v) => VersionRange::at_least_same_major(&v),
        None => VersionRange::parse(&m.api_version).map_err(|e| bad("apiVersion", e))?,
    };
    let mut deps = Vec::new();
    for d in &m.dependencies {
        deps.push((d, VersionRange::parse(&d.range).map_err(|e| bad("dependency", e))?));
    }
    let mut incompatible = Vec::new();
    for x in &m.incompatible {
        incompatible.push((x.id.as_str(), VersionRange::parse(&x.range).map_err(|e| bad("incompatible", e))?));
    }
    Ok(Parsed { m, version, api, deps, incompatible })
}

fn side_name(s: Side) -> &'static str {
    match s {
        Side::Server => "server",
        Side::Client => "client",
        Side::Both => "both",
    }
}

fn side_matches(m: Side, current: RunSide) -> bool {
    match m {
        Side::Both => true,
        Side::Server => current == RunSide::Server,
        Side::Client => current == RunSide::Client,
    }
}

struct Rejected {
    state: ModState,
    reason: String,
}

/// Which mods load, in which order, and why the others do not (Resolver.Resolve). `rejected` keeps Resolver.cs's
/// order; tests compare it as a set.
pub fn resolve(candidates: &[Candidate], env: &Environment) -> Result<Resolution> {
    let kiln = SemVersion::parse(&env.kiln).map_err(|e| Error::invalid(format!("env.kiln: {}", e.message)))?;
    let api = SemVersion::parse(&env.api).map_err(|e| Error::invalid(format!("env.api: {}", e.message)))?;
    let has_feature = |f: &str| !env.unavailable_features.iter().any(|u| u == f);
    let mut loaded: BTreeMap<&str, Parsed> = BTreeMap::new();
    for m in &env.loaded {
        loaded.insert(m.id.as_str(), parsed(m)?);
    }
    let mut parsed_of: Vec<Option<Parsed>> = Vec::with_capacity(candidates.len());
    for c in candidates {
        parsed_of.push(match &c.manifest {
            Some(m) => Some(parsed(m)?),
            None => None,
        });
    }

    let mut out = Resolution { order: Vec::new(), rejected: Vec::new() };
    // id -> candidate index
    let mut active: BTreeMap<String, usize> = BTreeMap::new();
    // first rejection per id (Resolver.cs: rejected.TryAdd)
    let mut rejected: BTreeMap<String, Rejected> = BTreeMap::new();
    let reject = |out: &mut Resolution, rejected: &mut BTreeMap<String, Rejected>, c: &Candidate, state: ModState, reason: String| {
        let id = c.id();
        out.rejected.push(Rejection { id: id.clone(), dir: c.dir.clone(), state, reason: reason.clone() });
        rejected.entry(id).or_insert(Rejected { state, reason });
    };

    // 1. per-mod checks: manifest, duplicates, side, API, features
    for (i, c) in candidates.iter().enumerate() {
        let Some(p) = &parsed_of[i] else {
            let reason = c.error.clone().unwrap_or_else(|| "unreadable manifest".into());
            reject(&mut out, &mut rejected, c, ModState::Failed, reason);
            continue;
        };
        let m = p.m;
        if let Some(&first) = active.get(&m.id) {
            let reason = format!("duplicate mod id '{}' (also in folder {}); only the first loads", m.id, candidates[first].folder());
            reject(&mut out, &mut rejected, c, ModState::Failed, reason);
            continue;
        }
        if loaded.contains_key(m.id.as_str()) {
            reject(&mut out, &mut rejected, c, ModState::Failed, format!("mod {} is already loaded", m.id));
            continue;
        }
        if !side_matches(m.side, env.side) {
            let on = if env.side == RunSide::Client { "client" } else { "server" };
            reject(&mut out, &mut rejected, c, ModState::Skipped, format!("side '{}': not loaded on a {on}", side_name(m.side)));
            continue;
        }
        if !p.api.satisfies(&api) {
            let reason = format!("needs Kiln API {}; this Kiln provides API {api}", m.api_version);
            reject(&mut out, &mut rejected, c, ModState::Failed, reason);
            continue;
        }
        let missing: Vec<&str> = m.features.iter().map(String::as_str).filter(|f| !has_feature(f)).collect();
        if !missing.is_empty() {
            let reason = format!("needs feature{} {}, unavailable on this game build", if missing.len() > 1 { "s" } else { "" }, missing.join(", "));
            reject(&mut out, &mut rejected, c, ModState::Failed, reason);
            continue;
        }
        active.insert(m.id.clone(), i);
    }

    loop {
        // 2. dependencies and incompatibilities, to a fixpoint (a failure can break a dependent)
        let mut changed = true;
        while changed {
            changed = false;
            let ids: Vec<(String, usize)> = active.iter().map(|(k, &v)| (k.clone(), v)).collect();
            for (id, i) in ids {
                let p = parsed_of[i].as_ref().expect("active mods have manifests");
                if let Some(reason) = check_dependencies(p, &active, &parsed_of, &loaded, &rejected, &kiln) {
                    active.remove(&id);
                    reject(&mut out, &mut rejected, &candidates[i], ModState::Failed, reason);
                    changed = true;
                }
            }
        }

        // 3. order (Kahn, ties by id); what is left sits in or behind a cycle
        let edges = edges(&active, &parsed_of);
        let (order, leftover) = topological_order(&active, &edges);
        if leftover.is_empty() {
            out.order = order;
            return Ok(out);
        }
        let mut cycles = find_cycles(&leftover, &edges);
        if cycles.is_empty() {
            cycles = vec![leftover.iter().cloned().collect()]; // cannot happen; never loop forever
        }
        for cycle in cycles {
            let mut text: Vec<&str> = cycle.iter().map(String::as_str).collect();
            text.push(&cycle[0]);
            let text = text.join(" -> ");
            let mut seen = BTreeSet::new();
            for id in &cycle {
                if !seen.insert(id.clone()) {
                    continue;
                }
                if let Some(i) = active.remove(id) {
                    let reason = format!("dependency cycle: {text} (each must load after the next)");
                    reject(&mut out, &mut rejected, &candidates[i], ModState::Failed, reason);
                }
            }
        }
    }
}

fn check_dependencies(p: &Parsed, active: &BTreeMap<String, usize>, parsed_of: &[Option<Parsed>],
    loaded: &BTreeMap<&str, Parsed>, rejected: &BTreeMap<String, Rejected>, kiln: &SemVersion) -> Option<String> {
    let provider = |id: &str| -> Option<&Parsed> {
        match active.get(id) {
            Some(&i) => parsed_of[i].as_ref(),
            None => loaded.get(id),
        }
    };
    for (d, range) in &p.deps {
        if d.id == KILN_ID {
            // a dev build (0.1.0-dev) counts as its release for dependency checks
            if !range.satisfies(kiln) && !range.satisfies(&kiln.release()) {
                return Some(format!("needs Kiln {}; this is Kiln {kiln}", d.range));
            }
            continue;
        }
        let Some(prov) = provider(&d.id) else {
            if d.optional {
                continue;
            }
            return Some(match rejected.get(&d.id) {
                Some(r) => format!("dependency {} {} did not load ({}: {})", d.id, d.range,
                    if r.state == ModState::Skipped { "skipped" } else { "failed" }, r.reason),
                None => format!("missing dependency {} {}", d.id, d.range),
            });
        };
        if !range.satisfies(&prov.version) {
            return Some(format!("needs {} {}, found {} {}{}", d.id, d.range, prov.m.id, prov.m.version,
                if d.optional { " (an optional dependency must match when present)" } else { "" }));
        }
    }
    for (id, range) in &p.incompatible {
        if let Some(other) = provider(id)
            && range.satisfies(&other.version)
        {
            return Some(format!("incompatible with {} {} (declared in its incompatible list)", other.m.id, other.m.version));
        }
    }
    None
}

type Edges = BTreeMap<String, BTreeSet<String>>;

/// before -> after edges among the active mods: dependencies (required and optional), loadAfter, loadBefore.
fn edges(active: &BTreeMap<String, usize>, parsed_of: &[Option<Parsed>]) -> Edges {
    let mut e: Edges = active.keys().map(|k| (k.clone(), BTreeSet::new())).collect();
    for (id, &i) in active {
        let m = parsed_of[i].as_ref().expect("active mods have manifests").m;
        for d in &m.dependencies {
            if active.contains_key(&d.id) {
                e.get_mut(&d.id).unwrap().insert(id.clone());
            }
        }
        for x in &m.load_after {
            if active.contains_key(x) {
                e.get_mut(x).unwrap().insert(id.clone());
            }
        }
        for x in &m.load_before {
            if active.contains_key(x) {
                e.get_mut(id).unwrap().insert(x.clone());
            }
        }
    }
    e
}

fn topological_order(active: &BTreeMap<String, usize>, edges: &Edges) -> (Vec<String>, BTreeSet<String>) {
    let mut indegree: BTreeMap<&str, usize> = active.keys().map(|k| (k.as_str(), 0)).collect();
    for targets in edges.values() {
        for t in targets {
            *indegree.get_mut(t.as_str()).unwrap() += 1;
        }
    }
    let mut ready: BTreeSet<&str> = indegree.iter().filter(|e| *e.1 == 0).map(|e| *e.0).collect();
    let mut order = Vec::new();
    while let Some(next) = ready.pop_first() {
        order.push(next.to_string());
        for t in &edges[next] {
            let d = indegree.get_mut(t.as_str()).unwrap();
            *d -= 1;
            if *d == 0 {
                ready.insert(t.as_str());
            }
        }
    }
    let leftover = indegree.iter().filter(|e| *e.1 > 0).map(|e| e.0.to_string()).collect();
    (order, leftover)
}

/// Strongly connected components with a cycle, each as a cycle path in "must load after" direction.
fn find_cycles(nodes: &BTreeSet<String>, edges: &Edges) -> Vec<Vec<String>> {
    // depends_on[a] = {b | b must load before a}, restricted to the leftover nodes
    let mut depends_on: Edges = nodes.iter().map(|n| (n.clone(), BTreeSet::new())).collect();
    for (from, targets) in edges {
        if !nodes.contains(from) {
            continue;
        }
        for to in targets {
            if nodes.contains(to) {
                depends_on.get_mut(to).unwrap().insert(from.clone());
            }
        }
    }
    let mut cycles = Vec::new();
    for scc in strongly_connected(&depends_on) {
        let start = scc.iter().next().expect("non-empty component").clone();
        if scc.len() == 1 && !depends_on[&start].contains(&start) {
            continue;
        }
        cycles.push(shortest_cycle(&start, &depends_on, &scc));
    }
    cycles
}

fn shortest_cycle(start: &str, next: &Edges, within: &BTreeSet<String>) -> Vec<String> {
    let mut parent: BTreeMap<String, String> = BTreeMap::new();
    let mut queue: VecDeque<String> = VecDeque::new();
    for n in next[start].iter().filter(|n| within.contains(*n)) {
        if n == start {
            return vec![start.to_string()];
        }
        if !parent.contains_key(n) {
            parent.insert(n.clone(), start.to_string());
            queue.push_back(n.clone());
        }
    }
    while let Some(cur) = queue.pop_front() {
        for n in next[&cur].iter().filter(|n| within.contains(*n)) {
            if n == start {
                let mut path = vec![cur.clone()];
                let mut p = cur.clone();
                while parent[&p] != start {
                    path.push(parent[&p].clone());
                    p = parent[&p].clone();
                }
                path.push(start.to_string());
                path.reverse();
                return path;
            }
            if !parent.contains_key(n) {
                parent.insert(n.clone(), cur.clone());
                queue.push_back(n.clone());
            }
        }
    }
    within.iter().cloned().collect()
}

/// Tarjan over the keys in ordinal order (Resolver.StronglyConnected).
fn strongly_connected(graph: &Edges) -> Vec<BTreeSet<String>> {
    struct T<'g> {
        graph: &'g Edges,
        index: BTreeMap<&'g str, usize>,
        low: BTreeMap<&'g str, usize>,
        on_stack: BTreeSet<&'g str>,
        stack: Vec<&'g str>,
        result: Vec<BTreeSet<String>>,
        counter: usize,
    }
    impl<'g> T<'g> {
        fn visit(&mut self, v: &'g str) {
            self.index.insert(v, self.counter);
            self.low.insert(v, self.counter);
            self.counter += 1;
            self.stack.push(v);
            self.on_stack.insert(v);
            for w in &self.graph[v] {
                let w = w.as_str();
                if !self.index.contains_key(w) {
                    self.visit(w);
                    let lw = self.low[w];
                    let lv = self.low.get_mut(v).unwrap();
                    *lv = (*lv).min(lw);
                } else if self.on_stack.contains(w) {
                    let iw = self.index[w];
                    let lv = self.low.get_mut(v).unwrap();
                    *lv = (*lv).min(iw);
                }
            }
            if self.low[v] != self.index[v] {
                return;
            }
            let mut scc = BTreeSet::new();
            loop {
                let x = self.stack.pop().expect("v is on the stack");
                self.on_stack.remove(x);
                scc.insert(x.to_string());
                if x == v {
                    break;
                }
            }
            self.result.push(scc);
        }
    }
    let mut t = T { graph, index: BTreeMap::new(), low: BTreeMap::new(), on_stack: BTreeSet::new(), stack: Vec::new(), result: Vec::new(), counter: 0 };
    for v in graph.keys() {
        if !t.index.contains_key(v.as_str()) {
            t.visit(v.as_str());
        }
    }
    t.result
}
