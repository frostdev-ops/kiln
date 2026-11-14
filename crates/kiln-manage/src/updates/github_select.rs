//! Which release of a GitHub repository is a mod's newest version, and how it compares with what is installed.
//! Pure functions over a release list that was already read: no request, no file, no clock.
//!
//! The rules (frozen; a change raises [`super::POLICY_VERSION`]):
//!
//! **Tags.** A release counts when its whole tag matches the source's pattern: the text before `{version}`, a
//! version, the text after it. The version is read by the manifest's semver grammar (`1`, `1.2`, `1.2.3-pre+build`;
//! a leading `v` only when the pattern has no `v` right before `{version}`). There is no wildcard and no substring
//! match. Drafts never count. On the stable channel a release counts only when GitHub does not mark it pre-release
//! and its version has no pre-release part; the pre-release channel takes both.
//!
//! **Ranking.** The candidate is the highest version by semver precedence (`1.10` over `1.9`, `dev.10` over `dev.9`,
//! build metadata not compared). Two releases with the same precedence are `ambiguous_release`: nothing breaks the
//! tie, not the publication date either. A list that was not read to its end is `incomplete_list` whatever it
//! holds: "newest" is never claimed without having seen everything.
//!
//! **The release binding.** Only the candidate's `kiln-release.json` is looked at ([`pick`] says which asset; the
//! caller reads it). Present: it must be valid, hold exactly one record of this mod, name the tag's version and an
//! asset of the release with the stated size (and the `asset` template's name, when the source has one); else the
//! release is not selectable (`invalid_metadata`). Absent: the result is advisory (a version and a link), with
//! compatibility unknown.
//!
//! **The asset.** Exactly one asset with the expected name: none is `missing_asset`, several are `ambiguous_asset`.
//! The name is the template with the version as the tag spells it.
//!
//! **Comparing.** Installed lower: `available`. Installed higher: `current`. Equal: `current` when the installed
//! archive's digest is known and is the candidate's; `new-upload` when a recorded release id, asset id or digest
//! differs from the candidate's; `same_version_unbound` when Kiln does not know which bytes are installed (a mod
//! copied by hand). A release the install receipt names that is gone from a complete list: `release_removed`.

use super::status::{Compatibility, Coverage, UnknownReason, UpdateState};
use crate::manifest::{expand_asset, Side, UpdateChannel};
use crate::release_binding::{self, ReleaseBinding};
use crate::semver::{SemVersion, VersionRange};
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;

/// One asset of a release.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Asset {
    /// The provider's id, as text.
    pub id: String,
    pub name: String,
    pub size: u64,
    /// The digest GitHub states for the asset (`"digest": "sha256:<hex>"`), when it does.
    pub sha256: Option<String>,
}

/// One release of the list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Release {
    pub id: String,
    pub tag: String,
    pub draft: bool,
    pub prerelease: bool,
    pub assets: Vec<Asset>,
}

fn id_of(v: &serde_json::Value) -> Option<String> {
    match v {
        serde_json::Value::Number(n) => n.as_u64().map(|n| n.to_string()),
        serde_json::Value::String(s) if !s.is_empty() && s.len() <= 64 && s.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-' | b'=')) => Some(s.clone()),
        _ => None,
    }
}

/// Reads one page of GitHub's "list releases" answer: a JSON array of release objects. Only the fields selection
/// uses are read (id, tag_name, draft, prerelease, assets: id, name, size, digest); everything else, the release
/// notes and every URL included, is ignored. An answer of another shape is refused as a whole.
pub fn parse_page(body: &[u8]) -> Option<Vec<Release>> {
    let serde_json::Value::Array(items) = serde_json::from_slice::<serde_json::Value>(body).ok()? else { return None };
    if items.len() > super::github::PER_PAGE {
        return None;
    }
    let mut out = Vec::with_capacity(items.len());
    for r in &items {
        let mut assets = Vec::new();
        for a in r.get("assets")?.as_array()? {
            let sha256 = a.get("digest").and_then(|d| d.as_str()).and_then(|d| d.strip_prefix("sha256:")).filter(|h| release_binding::is_sha256_hex(h)).map(str::to_string);
            assets.push(Asset { id: id_of(a.get("id")?)?, name: a.get("name")?.as_str()?.to_string(), size: a.get("size")?.as_u64()?, sha256 });
        }
        out.push(Release {
            id: id_of(r.get("id")?)?,
            tag: r.get("tag_name")?.as_str()?.to_string(),
            draft: r.get("draft")?.as_bool()?,
            prerelease: r.get("prerelease")?.as_bool()?,
            assets,
        });
    }
    Some(out)
}

/// A version taken from a tag.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TagVersion {
    /// As the tag spells it (`1.10`, `v2.0.0-beta.1+linux`): what an asset name is made with.
    pub spelled: String,
    pub version: SemVersion,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TagMatch {
    /// The tag does not have the pattern's text before and after a version.
    No,
    /// It has, and what stands at `{version}` is not a semantic version.
    NotSemver,
    Version(TagVersion),
}

/// Matches a whole tag against a tag pattern ([`crate::publish::config::is_tag_pattern`]).
pub fn match_tag(pattern: &str, tag: &str) -> TagMatch {
    if !crate::publish::config::is_tag_pattern(pattern) {
        return TagMatch::No;
    }
    let Some((pre, post)) = pattern.split_once("{version}") else { return TagMatch::No };
    let Some(middle) = tag.strip_prefix(pre).and_then(|rest| rest.strip_suffix(post)) else { return TagMatch::No };
    if middle.is_empty() {
        return TagMatch::No;
    }
    // the manifest's grammar, without what it forgives around a version: no blanks, and one "v" at most
    let doubled_v = pre.ends_with(['v', 'V']) && middle.starts_with(['v', 'V']);
    if middle.len() > 128 || middle.trim() != middle || doubled_v {
        return TagMatch::NotSemver;
    }
    match SemVersion::try_parse(middle) {
        Some(version) => TagMatch::Version(TagVersion { spelled: middle.to_string(), version }),
        None => TagMatch::NotSemver,
    }
}

/// The GitHub source of a mod (the manifest's `updates` or a manual mapping).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct GithubSource<'a> {
    pub repository: &'a str,
    pub tag_pattern: &'a str,
    pub asset: Option<&'a str>,
    pub channel: UpdateChannel,
}

/// The release that would be the candidate, before its binding is read.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Candidate<'a> {
    pub release: &'a Release,
    pub tag: TagVersion,
}

impl<'a> Candidate<'a> {
    /// The release's `kiln-release.json` assets (the fixed name, exact).
    pub fn binding_assets(&self) -> Vec<&'a Asset> {
        self.release.assets.iter().filter(|a| a.name == release_binding::FILE_NAME).collect()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Pick<'a> {
    /// No release counts: `not_found`, or `non_semver` when tags had the pattern's frame but none was a version.
    Nothing(UnknownReason),
    /// Several releases share the highest precedence.
    Ambiguous,
    Winner(Candidate<'a>),
}

/// Ranks the releases of a list for a source. Reads tags and flags only.
pub fn pick<'a>(source: &GithubSource, releases: &'a [Release]) -> Pick<'a> {
    let mut framed_not_semver = false;
    let mut best: Vec<Candidate<'a>> = Vec::new();
    for r in releases.iter().filter(|r| !r.draft) {
        let tag = match match_tag(source.tag_pattern, &r.tag) {
            TagMatch::No => continue,
            TagMatch::NotSemver => {
                framed_not_semver = true;
                continue;
            }
            TagMatch::Version(t) => t,
        };
        if source.channel == UpdateChannel::Stable && (r.prerelease || tag.version.is_prerelease()) {
            continue;
        }
        match best.first().map(|b| tag.version.compare(&b.tag.version)) {
            Some(Ordering::Less) => {}
            Some(Ordering::Equal) => best.push(Candidate { release: r, tag }),
            Some(Ordering::Greater) | None => best = vec![Candidate { release: r, tag }],
        }
    }
    match best.len() {
        0 => Pick::Nothing(if framed_not_semver { UnknownReason::NonSemver } else { UnknownReason::NotFound }),
        1 => Pick::Winner(best.remove(0)),
        _ => Pick::Ambiguous,
    }
}

/// What reading the candidate's `kiln-release.json` gave.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BindingRead<'a> {
    /// The release has no such asset.
    Absent,
    Bytes(&'a [u8]),
    /// The release has the asset and it cannot be used as it is listed: more than one of the name, or larger than a
    /// binding may be.
    Refused,
    /// The request for it failed.
    Unreadable,
}

/// What a release's binding says about loading the candidate.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Declared {
    pub side: Side,
    /// The candidate's `apiVersion`, as written.
    pub api_version: String,
}

/// The candidate of a source.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Selected {
    /// The version as the tag spells it.
    pub version: String,
    pub tag: String,
    pub release_id: String,
    pub asset_id: Option<String>,
    pub asset_name: Option<String>,
    /// The zip's digest: the binding's, else the one GitHub lists for the asset.
    pub artifact_sha256: Option<String>,
    /// SHA-256 of the `kiln-release.json` that was read.
    pub metadata_sha256: Option<String>,
    /// None: the release has no binding (advisory result).
    pub declared: Option<Declared>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "kind", content = "value")]
pub enum Outcome {
    Unknown(UnknownReason),
    Candidate(Selected),
}

/// What a source's releases say, independent of what is installed (so it can be remembered and compared again when
/// another version is installed).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Selection {
    pub outcome: Outcome,
    pub coverage: Coverage,
    /// A release id an install receipt named that is not in the (complete) list.
    #[serde(default)]
    pub missing_release: Option<String>,
}

impl Selection {
    pub fn unknown(reason: UnknownReason, coverage: Coverage) -> Selection {
        Selection { outcome: Outcome::Unknown(reason), coverage, missing_release: None }
    }
}

fn one_asset<'a>(release: &'a Release, name: &str) -> Result<&'a Asset, UnknownReason> {
    let mut named = release.assets.iter().filter(|a| a.name == name);
    match (named.next(), named.next()) {
        (Some(a), None) => Ok(a),
        (None, _) => Err(UnknownReason::MissingAsset),
        (Some(_), Some(_)) => Err(UnknownReason::AmbiguousAsset),
    }
}

/// Decides the candidate from the picked release and what reading its binding gave.
pub fn finish(source: &GithubSource, mod_id: &str, c: &Candidate, binding: BindingRead) -> Outcome {
    // the name the source expects, with the version as the tag spells it
    let expected = match source.asset {
        Some(template) => match expand_asset(template, &c.tag.spelled) {
            Some(name) => Some(name),
            None => return Outcome::Unknown(UnknownReason::MissingAsset),
        },
        None => None,
    };
    let mut selected = Selected {
        version: c.tag.spelled.clone(),
        tag: c.release.tag.clone(),
        release_id: c.release.id.clone(),
        asset_id: None,
        asset_name: None,
        artifact_sha256: None,
        metadata_sha256: None,
        declared: None,
    };
    let asset = match binding {
        BindingRead::Refused => return Outcome::Unknown(UnknownReason::InvalidMetadata),
        BindingRead::Unreadable => return Outcome::Unknown(UnknownReason::Network),
        BindingRead::Bytes(bytes) => {
            let Ok(parsed) = ReleaseBinding::parse(bytes) else { return Outcome::Unknown(UnknownReason::InvalidMetadata) };
            // exactly one record of this mod; a file that names it twice, or not at all, is not about this release
            let Ok(record) = parsed.record_for(mod_id) else { return Outcome::Unknown(UnknownReason::InvalidMetadata) };
            if record.semver() != c.tag.version {
                return Outcome::Unknown(UnknownReason::InvalidMetadata);
            }
            if expected.as_deref().is_some_and(|name| name != record.artifact.name) {
                return Outcome::Unknown(UnknownReason::InvalidMetadata);
            }
            let asset = match one_asset(c.release, &record.artifact.name) {
                Ok(a) => a,
                Err(why) => return Outcome::Unknown(why),
            };
            if asset.size != record.artifact.bytes || asset.sha256.as_deref().is_some_and(|d| d != record.artifact.sha256) {
                return Outcome::Unknown(UnknownReason::InvalidMetadata);
            }
            selected.artifact_sha256 = Some(record.artifact.sha256.clone());
            selected.metadata_sha256 = Some(crate::hash::sha256_hex(bytes));
            selected.declared = Some(Declared { side: record.side, api_version: record.api_version.clone() });
            Some(asset)
        }
        BindingRead::Absent => match &expected {
            Some(name) => match one_asset(c.release, name) {
                Ok(a) => {
                    selected.artifact_sha256 = a.sha256.clone();
                    Some(a)
                }
                Err(why) => return Outcome::Unknown(why),
            },
            // no binding and no asset name: a version and a link, nothing about a file
            None => None,
        },
    };
    if let Some(a) = asset {
        selected.asset_id = Some(a.id.clone());
        selected.asset_name = Some(a.name.clone());
    }
    Outcome::Candidate(selected)
}

/// What the caller's read of the candidate's `kiln-release.json` gave.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Fetched {
    Bytes(Vec<u8>),
    /// The answer cannot be a binding (larger than one may be, a redirect to somewhere else).
    Refused,
    /// No answer.
    Unreadable,
}

/// The whole selection over a list. `complete`: the list was read to its end. `receipt_release`: the release id an
/// install receipt names, if one does. `read_binding` is called at most once, for the candidate's binding asset
/// only, and never for a list that is not complete (its candidate proves nothing).
pub fn select<'a>(source: &GithubSource, mod_id: &str, releases: &'a [Release], complete: bool, receipt_release: Option<&str>,
                  read_binding: impl FnOnce(&Candidate<'a>, &'a Asset) -> Fetched) -> Selection {
    if !complete {
        return Selection::unknown(UnknownReason::IncompleteList, Coverage::Incomplete);
    }
    let missing_release = receipt_release.filter(|id| !releases.iter().any(|r| r.id == *id)).map(str::to_string);
    let outcome = match pick(source, releases) {
        Pick::Nothing(why) => Outcome::Unknown(why),
        Pick::Ambiguous => Outcome::Unknown(UnknownReason::AmbiguousRelease),
        Pick::Winner(c) => match c.binding_assets().as_slice() {
            [] => finish(source, mod_id, &c, BindingRead::Absent),
            // listed larger than a binding may be: not worth a request
            [one] if one.size <= release_binding::MAX_BYTES as u64 => match read_binding(&c, one) {
                Fetched::Bytes(bytes) => finish(source, mod_id, &c, BindingRead::Bytes(&bytes)),
                Fetched::Refused => finish(source, mod_id, &c, BindingRead::Refused),
                Fetched::Unreadable => finish(source, mod_id, &c, BindingRead::Unreadable),
            },
            _ => finish(source, mod_id, &c, BindingRead::Refused),
        },
    };
    Selection { outcome, coverage: Coverage::Complete, missing_release }
}

/// What is installed, as far as Kiln recorded it.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Installed<'a> {
    pub version: &'a str,
    /// The digest of the archive the installed copy was unpacked from (a receipt), when known.
    pub artifact_sha256: Option<&'a str>,
    pub release_id: Option<&'a str>,
    pub asset_id: Option<&'a str>,
    pub side: Option<Side>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Verdict {
    pub state: UpdateState,
    pub reason: Option<UnknownReason>,
    pub compatibility: Compatibility,
}

impl Verdict {
    fn unknown(reason: UnknownReason) -> Verdict {
        Verdict { state: UpdateState::Unknown, reason: Some(reason), compatibility: Compatibility::Unknown }
    }
}

/// Whether a candidate would load here, from its declared API range and side only.
pub fn compatibility(declared: Option<&Declared>, api_version: &str, installed_side: Option<Side>) -> Compatibility {
    let Some(d) = declared else { return Compatibility::Unknown };
    let Some(current) = SemVersion::try_parse(api_version) else { return Compatibility::Unknown };
    // as the manifest reads apiVersion: a bare version means "this or a later one of the same major"
    let range = match SemVersion::try_parse(&d.api_version) {
        Some(min) => VersionRange::at_least_same_major(&min),
        None => match VersionRange::try_parse(&d.api_version) {
            Ok(r) => r,
            Err(_) => return Compatibility::Unknown,
        },
    };
    // a candidate that no longer loads on the side the installed copy loads on is not a drop-in update
    let side_ok = installed_side.is_none_or(|s| d.side == Side::Both || d.side == s);
    if range.satisfies(&current) && side_ok { Compatibility::Compatible } else { Compatibility::Incompatible }
}

/// Compares what is installed with a source's selection.
pub fn compare(installed: &Installed, selection: &Selection, api_version: &str) -> Verdict {
    if selection.coverage == Coverage::Incomplete {
        return Verdict::unknown(UnknownReason::IncompleteList);
    }
    if installed.release_id.is_some() && installed.release_id == selection.missing_release.as_deref() {
        return Verdict::unknown(UnknownReason::ReleaseRemoved);
    }
    let c = match &selection.outcome {
        Outcome::Unknown(why) => return Verdict::unknown(*why),
        Outcome::Candidate(c) => c,
    };
    let (Some(have), Some(theirs)) = (SemVersion::try_parse(installed.version), SemVersion::try_parse(&c.version)) else {
        return Verdict::unknown(UnknownReason::NonSemver);
    };
    let compatibility = compatibility(c.declared.as_ref(), api_version, installed.side);
    let state = match have.compare(&theirs) {
        Ordering::Less => UpdateState::Available,
        Ordering::Greater => UpdateState::Current,
        Ordering::Equal => {
            // each "differs" needs a recorded fact about the installed copy: without one nothing is claimed
            let differs = installed.release_id.is_some_and(|id| id != c.release_id)
                || matches!((installed.asset_id, c.asset_id.as_deref()), (Some(a), Some(b)) if a != b)
                || matches!((installed.artifact_sha256, c.artifact_sha256.as_deref()), (Some(a), Some(b)) if a != b);
            let same_bytes = matches!((installed.artifact_sha256, c.artifact_sha256.as_deref()), (Some(a), Some(b)) if a == b);
            if differs {
                UpdateState::NewUpload
            } else if same_bytes {
                UpdateState::Current
            } else {
                return Verdict { state: UpdateState::Unknown, reason: Some(UnknownReason::SameVersionUnbound), compatibility };
            }
        }
    };
    Verdict { state, reason: None, compatibility }
}

/// The page of a release (`https://github.com/<owner>/<repo>/releases/tag/<tag>`), made from the source's repository
/// and a tag that matched its pattern: never a URL taken from the provider's answer.
pub fn release_page(repository: &str, tag: Option<&str>) -> String {
    match tag {
        // a matched tag holds A-Z a-z 0-9 . _ / - and, in the version, + (which a URL path must escape)
        Some(tag) => format!("https://github.com/{repository}/releases/tag/{}", tag.replace('+', "%2B")),
        None => format!("https://github.com/{repository}/releases"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(name: &str) -> Vec<u8> {
        let p = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixture/updates-github").join(name);
        std::fs::read(&p).unwrap_or_else(|e| panic!("{}: {e}", p.display()))
    }

    fn list(name: &str) -> Vec<Release> {
        parse_page(&fixture(name)).unwrap_or_else(|| panic!("{name} is not a release list"))
    }

    fn source<'a>(repository: &'a str, tag_pattern: &'a str, asset: Option<&'a str>, channel: UpdateChannel) -> GithubSource<'a> {
        GithubSource { repository, tag_pattern, asset, channel }
    }

    const GLOW: &str = "example-org/lantern-glow";
    const GLOW_SHA: &str = "ad9b19c4df20898a8b46b7793aeb81b779db01fc468b2768e193186a1eee003d";

    fn glow(channel: UpdateChannel) -> GithubSource<'static> {
        source(GLOW, "v{version}", Some("lantern-glow-{version}.zip"), channel)
    }

    fn rel(id: u32, tag: &str) -> Release {
        Release { id: id.to_string(), tag: tag.into(), draft: false, prerelease: false, assets: Vec::new() }
    }

    fn with(mut r: Release, name: &str, size: u64, sha: Option<&str>) -> Release {
        let id = format!("{}{}", r.id, r.assets.len());
        r.assets.push(Asset { id, name: name.into(), size, sha256: sha.map(str::to_string) });
        r
    }

    /// A read that must not happen.
    fn no_read(c: &Candidate, _: &Asset) -> Fetched {
        panic!("the binding of {} was read", c.release.tag)
    }

    fn winner<'a>(s: &GithubSource, releases: &'a [Release]) -> &'a str {
        match pick(s, releases) {
            Pick::Winner(c) => c.release.tag.as_str(),
            other => panic!("no winner: {other:?}"),
        }
    }

    fn candidate(sel: &Selection) -> &Selected {
        match &sel.outcome {
            Outcome::Candidate(c) => c,
            Outcome::Unknown(why) => panic!("no candidate: {why:?}"),
        }
    }

    fn reason(sel: &Selection) -> UnknownReason {
        match &sel.outcome {
            Outcome::Unknown(why) => *why,
            Outcome::Candidate(c) => panic!("a candidate: {c:?}"),
        }
    }

    #[test]
    fn a_tag_matches_as_a_whole_or_not_at_all() {
        let v = |pattern: &str, tag: &str| match match_tag(pattern, tag) {
            TagMatch::Version(t) => format!("{} = {}", t.spelled, t.version),
            TagMatch::NotSemver => "not semver".into(),
            TagMatch::No => "no".into(),
        };
        assert_eq!(v("v{version}", "v1.10.0"), "1.10.0 = 1.10.0");
        assert_eq!(v("v{version}", "v1.9"), "1.9 = 1.9.0", "a short version is read as the manifest reads it");
        assert_eq!(v("{version}", "v2.0.0"), "v2.0.0 = 2.0.0", "a leading v is fine when the pattern has none");
        assert_eq!(v("{version}", "2.0.0"), "2.0.0 = 2.0.0");
        assert_eq!(v("v{version}", "vv2.0.0"), "not semver", "but not a second one");
        assert_eq!(v("V{version}", "Vv2.0.0"), "not semver");
        assert_eq!(v("mods/example/v{version}", "mods/example/v1.5.0"), "1.5.0 = 1.5.0");
        assert_eq!(v("mods/example/v{version}", "mods/other/v9.0.0"), "no");
        assert_eq!(v("mods/example/v{version}", "mods/example-extras/v8.0.0"), "no");
        assert_eq!(v("mods/example/v{version}", "v7.0.0"), "no", "a tag is not matched from its end");
        assert_eq!(v("v{version}", "mods/example/v1.5.0"), "no", "nor by a part of it");
        assert_eq!(v("v{version}-kiln", "v1.2.3-kiln"), "1.2.3 = 1.2.3");
        assert_eq!(v("v{version}-kiln", "v1.2.3"), "no");
        assert_eq!(v("v{version}-kiln", "v1.2.3-beta-kiln"), "1.2.3-beta = 1.2.3-beta", "the suffix is taken from the end");
        assert_eq!(v("v{version}", "v1.3.0+build.2"), "1.3.0+build.2 = 1.3.0+build.2", "build metadata stays in the identity");
        assert_eq!(v("v{version}", "v2.0.0-beta.1"), "2.0.0-beta.1 = 2.0.0-beta.1");
        assert_eq!(v("v{version}", "v"), "no");
        for tag in ["vNext", "v1.x", "v1.2.3.4", "v01.2.3", "v1.2.3-", "v 1.2.3", "v1.2.3 ", "v*"] {
            assert_eq!(v("v{version}", tag), "not semver", "{tag}");
        }
        assert_eq!(v("v{version}", "nightly"), "no");
        assert_eq!(v("v{version}", "V1.0.0"), "no", "the pattern's text is compared exactly");
        // the manifest's grammar is the judge, also where it is generous: this is 2026.0.0 with the pre-release part "11"
        assert_eq!(v("release-{version}", "release-2026-11"), "2026-11 = 2026.0.0-11");
        // a pattern that is not one matches nothing
        for pattern in ["v*", "v{version}{version}", "release", "v{version}/", "a/../v{version}", ".*"] {
            assert_eq!(v(pattern, "v1.0.0"), "no", "{pattern}");
        }
    }

    /// The acceptance line of release selection: the intended tag, channel and file are chosen, by rule, from a
    /// list that holds drafts, pre-releases, other mods' tags and tags that are no versions.
    #[test]
    fn branch_selection() {
        let glow_list = list("lantern-glow.releases.json");
        assert_eq!(glow_list.len(), 8);
        // stable: not the draft (v3.0.0), not what GitHub marks pre-release (v2.0.0-beta.1), not a pre-release
        // version GitHub does not mark (v1.10.0-rc.1), not "nightly"; and 1.10 is above 1.9
        assert_eq!(winner(&glow(UpdateChannel::Stable), &glow_list), "v1.10.0");
        // pre-release channel: stable and pre-release versions compete; the draft still does not
        assert_eq!(winner(&glow(UpdateChannel::Prerelease), &glow_list), "v2.0.0-beta.1");
        // dev.9 against dev.10: numeric parts compare as numbers
        let devs: Vec<Release> = glow_list.iter().filter(|r| r.tag.contains("-dev.")).cloned().collect();
        assert_eq!(devs.len(), 2);
        assert_eq!(winner(&glow(UpdateChannel::Prerelease), &devs), "v1.2.0-dev.10");
        assert_eq!(pick(&glow(UpdateChannel::Stable), &devs), Pick::Nothing(UnknownReason::NotFound), "no stable release among them");
        // v1.9 against v1.10, whatever the order of the list
        let mut two = vec![rel(1, "v1.9"), rel(2, "v1.10")];
        assert_eq!(winner(&glow(UpdateChannel::Stable), &two), "v1.10");
        two.reverse();
        assert_eq!(winner(&glow(UpdateChannel::Stable), &two), "v1.10");
        // a repository of several mods: only this mod's tags, though another mod's version and the repository's own
        // are higher
        let mono = list("kiln-mods.releases.json");
        let example = source("example-org/kiln-mods", "mods/example/v{version}", Some("example-{version}.zip"), UpdateChannel::Stable);
        assert_eq!(winner(&example, &mono), "mods/example/v1.5.0");
        assert_eq!(winner(&GithubSource { channel: UpdateChannel::Prerelease, ..example }, &mono), "mods/example/v2.0.0-rc.1");
        assert_eq!(winner(&source("example-org/kiln-mods", "mods/other/v{version}", None, UpdateChannel::Stable), &mono), "mods/other/v9.0.0");
        assert_eq!(winner(&source("example-org/kiln-mods", "v{version}", None, UpdateChannel::Stable), &mono), "v7.0.0");
        // two releases of one precedence that differ in build metadata: nobody picks, the date does not either
        let builds = list("build-tags.releases.json");
        assert_eq!(pick(&source("example-org/build-tags", "v{version}", None, UpdateChannel::Stable), &builds), Pick::Ambiguous);
        assert_eq!(winner(&source("example-org/build-tags", "v{version}", None, UpdateChannel::Stable), &builds[..2].iter().rev().take(1).cloned().collect::<Vec<_>>()), "v1.3.0+build.1");
        // 1.3 and 1.3.0 are the same version too
        assert_eq!(pick(&glow(UpdateChannel::Stable), &[rel(1, "v1.3"), rel(2, "v1.3.0")]), Pick::Ambiguous);
        // an ambiguity below the top does not matter
        assert_eq!(winner(&glow(UpdateChannel::Stable), &[rel(1, "v1.3"), rel(2, "v1.3.0"), rel(3, "v1.4.0")]), "v1.4.0");
        // tags that are no versions: nothing is guessed from their text
        let odd = list("odd-tags.releases.json");
        assert_eq!(pick(&source("example-org/odd-tags", "v{version}", None, UpdateChannel::Prerelease), &odd), Pick::Nothing(UnknownReason::NonSemver));
        assert_eq!(pick(&source("example-org/odd-tags", "stable-{version}", None, UpdateChannel::Prerelease), &odd), Pick::Nothing(UnknownReason::NotFound));
        assert_eq!(pick(&glow(UpdateChannel::Stable), &[]), Pick::Nothing(UnknownReason::NotFound));

        // the whole selection on the main list: the binding of the winner, and only of the winner, is read
        let binding = fixture("lantern-glow.kiln-release.json");
        let mut read = Vec::new();
        let sel = select(&glow(UpdateChannel::Stable), "lantern-glow", &glow_list, true, None, |c, a| {
            read.push((c.release.tag.clone(), a.name.clone(), a.id.clone()));
            Fetched::Bytes(binding.clone())
        });
        assert_eq!(read, [("v1.10.0".to_string(), "kiln-release.json".to_string(), "7052".to_string())]);
        let c = candidate(&sel);
        assert_eq!((c.version.as_str(), c.tag.as_str(), c.release_id.as_str()), ("1.10.0", "v1.10.0", "9005"));
        assert_eq!((c.asset_id.as_deref(), c.asset_name.as_deref(), c.artifact_sha256.as_deref()), (Some("7051"), Some("lantern-glow-1.10.0.zip"), Some(GLOW_SHA)));
        assert_eq!(c.declared, Some(Declared { side: Side::Both, api_version: "0.2".into() }));
        assert_eq!(c.metadata_sha256.as_deref(), Some(crate::hash::sha256_hex(&binding).as_str()));
        assert_eq!((sel.coverage, sel.missing_release.as_ref()), (Coverage::Complete, None));
        // the pre-release channel's winner has no binding: advisory, its file by the template, nothing read
        let sel = select(&glow(UpdateChannel::Prerelease), "lantern-glow", &glow_list, true, None, no_read);
        let c = candidate(&sel);
        assert_eq!((c.version.as_str(), c.asset_name.as_deref(), c.declared.as_ref()), ("2.0.0-beta.1", Some("lantern-glow-2.0.0-beta.1.zip"), None));
        println!("PASS updates.branch-selection");
    }

    #[test]
    fn a_list_that_was_cut_is_unknown_whatever_it_holds() {
        let glow_list = list("lantern-glow.releases.json");
        let sel = select(&glow(UpdateChannel::Stable), "lantern-glow", &glow_list, false, Some("9005"), no_read);
        assert_eq!((reason(&sel), sel.coverage, sel.missing_release.as_ref()), (UnknownReason::IncompleteList, Coverage::Incomplete, None));
        // also when nothing in it matched, and when it was ambiguous
        assert_eq!(reason(&select(&glow(UpdateChannel::Stable), "lantern-glow", &[], false, None, no_read)), UnknownReason::IncompleteList);
        let have = Installed { version: "1.0.0", ..Default::default() };
        let v = compare(&have, &sel, "0.2.3");
        assert_eq!((v.state, v.reason), (UpdateState::Unknown, Some(UnknownReason::IncompleteList)));
    }

    #[test]
    fn the_binding_must_be_valid_and_about_this_mod_and_this_release() {
        let glow_list = list("lantern-glow.releases.json");
        let good = String::from_utf8(fixture("lantern-glow.kiln-release.json")).unwrap();
        let with_binding = |text: &str, mod_id: &str, s: &GithubSource| {
            let bytes = text.as_bytes().to_vec();
            select(s, mod_id, &glow_list, true, None, move |_, _| Fetched::Bytes(bytes))
        };
        let stable = glow(UpdateChannel::Stable);
        assert_eq!(candidate(&with_binding(&good, "lantern-glow", &stable)).version, "1.10.0");
        let edit = |from: &str, to: &str| {
            assert!(good.contains(from), "{from}");
            good.replacen(from, to, 1)
        };
        let two_records = {
            let v: serde_json::Value = serde_json::from_str(&good).unwrap();
            let m = v["mods"][0].clone();
            serde_json::json!({"format": 1, "mods": [m.clone(), m]}).to_string()
        };
        let cases: Vec<(&str, String)> = vec![
            ("not JSON", "{".into()),
            ("a newer format", edit("\"format\": 1", "\"format\": 2")),
            ("another mod's record", edit("\"id\": \"lantern-glow\"", "\"id\": \"night-sky\"")),
            ("the mod named twice", two_records),
            ("another version than the tag", edit("\"version\": \"1.10.0\"", "\"version\": \"1.9.0\"")),
            ("an asset the release does not have", edit("lantern-glow-1.10.0.zip", "lantern-glow.zip")),
            ("another size than the asset's", edit("48213", "48214")),
            ("another digest than GitHub lists", edit(GLOW_SHA, &"0".repeat(64))),
            ("a digest that is none", edit(GLOW_SHA, "abc")),
            ("a side that is none", edit("\"both\"", "\"everywhere\"")),
            ("a key twice", edit("\"format\": 1,", "\"format\": 1, \"format\": 1,")),
        ];
        for (what, text) in &cases {
            let sel = with_binding(text, "lantern-glow", &stable);
            assert_eq!(reason(&sel), UnknownReason::InvalidMetadata, "{what}");
            // the release is not selectable: there is no falling back to an older one, and no "current"
            let v = compare(&Installed { version: "1.10.0", artifact_sha256: Some(GLOW_SHA), ..Default::default() }, &sel, "0.2.3");
            assert_eq!((v.state, v.reason), (UpdateState::Unknown, Some(UnknownReason::InvalidMetadata)), "{what}");
        }
        // the mod id asked for is another one than the binding's
        assert_eq!(reason(&with_binding(&good, "night-sky", &stable)), UnknownReason::InvalidMetadata);
        // a version the tag also means (1.10 = 1.10.0), and build metadata, are the same version
        assert_eq!(candidate(&with_binding(&edit("\"version\": \"1.10.0\"", "\"version\": \"1.10\""), "lantern-glow", &stable)).version, "1.10.0");
        // the source has no asset template: the binding names the file
        let no_template = GithubSource { asset: None, ..stable };
        assert_eq!(candidate(&with_binding(&good, "lantern-glow", &no_template)).asset_name.as_deref(), Some("lantern-glow-1.10.0.zip"));
        // the source's template names another file than the binding: they contradict each other
        let other_template = GithubSource { asset: Some("glow-{version}.zip"), ..stable };
        assert_eq!(reason(&with_binding(&good, "lantern-glow", &other_template)), UnknownReason::InvalidMetadata);
        // the binding names a file the release lacks, with no template in the source: the file is what is missing
        assert_eq!(reason(&with_binding(&edit("lantern-glow-1.10.0.zip", "lantern-glow.zip"), "lantern-glow", &no_template)), UnknownReason::MissingAsset);
        // reading it failed, or it was refused as too large
        let failed = |f: Fetched| select(&stable, "lantern-glow", &glow_list, true, None, move |_, _| f);
        assert_eq!(reason(&failed(Fetched::Unreadable)), UnknownReason::Network);
        assert_eq!(reason(&failed(Fetched::Refused)), UnknownReason::InvalidMetadata);
        // listed as larger than a binding may be, or listed twice: not read at all
        let big = vec![with(with(rel(1, "v1.0.0"), "lantern-glow-1.0.0.zip", 10, None), "kiln-release.json", 256 * 1024 + 1, None)];
        assert_eq!(reason(&select(&stable, "lantern-glow", &big, true, None, no_read)), UnknownReason::InvalidMetadata);
        let twice = vec![with(with(rel(1, "v1.0.0"), "kiln-release.json", 300, None), "kiln-release.json", 300, None)];
        assert_eq!(reason(&select(&stable, "lantern-glow", &twice, true, None, no_read)), UnknownReason::InvalidMetadata);
        // a file that only looks like the binding by its name's case is not the binding
        let cased = vec![with(with(rel(1, "v1.0.0"), "lantern-glow-1.0.0.zip", 10, None), "Kiln-Release.json", 300, None)];
        assert!(candidate(&select(&stable, "lantern-glow", &cased, true, None, no_read)).declared.is_none());
    }

    #[test]
    fn a_binding_keeps_the_tags_build_identity() {
        let releases = [with(with(rel(1, "v1.0.0+linux"), "mod.zip", 10, None), "kiln-release.json", 300, None)];
        let src = source(GLOW, "v{version}", Some("mod.zip"), UpdateChannel::Stable);
        let binding = |version: &str| serde_json::json!({"format": 1, "mods": [{"id": "m", "version": version, "side": "both", "apiVersion": "0.2",
            "artifact": {"name": "mod.zip", "sha256": GLOW_SHA, "bytes": 10}}]}).to_string().into_bytes();
        for version in ["1.0.0+windows", "1.0.0"] {
            let sel = select(&src, "m", &releases, true, None, |_, _| Fetched::Bytes(binding(version)));
            assert_eq!(sel.outcome, Outcome::Unknown(UnknownReason::InvalidMetadata), "{version}");
        }
        let sel = select(&src, "m", &releases, true, None, |_, _| Fetched::Bytes(binding("1.0+linux")));
        assert!(matches!(sel.outcome, Outcome::Candidate(_)), "normalized spelling with the same build is accepted");
    }

    #[test]
    fn a_page_cannot_exceed_the_requested_release_count() {
        let release = serde_json::json!({"id": 1, "tag_name": "v1.0.0", "draft": false, "prerelease": false, "assets": []});
        let body = serde_json::to_vec(&vec![release; 101]).unwrap();
        assert!(parse_page(&body).is_none());
    }

    #[test]
    fn exactly_one_asset_has_the_expected_name() {
        let stable = glow(UpdateChannel::Stable);
        let sel = |r: Release| select(&stable, "lantern-glow", &[r], true, None, no_read);
        // none
        assert_eq!(reason(&sel(rel(1, "v2.0.0"))), UnknownReason::MissingAsset);
        assert_eq!(reason(&sel(with(rel(1, "v2.0.0"), "lantern-glow.zip", 10, None))), UnknownReason::MissingAsset);
        assert_eq!(reason(&sel(with(rel(1, "v2.0.0"), "Lantern-Glow-2.0.0.zip", 10, None))), UnknownReason::MissingAsset, "names are compared exactly");
        // two
        let dup = with(with(rel(1, "v2.0.0"), "lantern-glow-2.0.0.zip", 10, None), "lantern-glow-2.0.0.zip", 11, None);
        assert_eq!(reason(&sel(dup)), UnknownReason::AmbiguousAsset);
        // one, with the version as the tag spells it (not 2.0.0 for a tag v2.0)
        let short = with(with(rel(1, "v2.0"), "lantern-glow-2.0.zip", 10, Some(GLOW_SHA)), "lantern-glow-2.0.0.zip", 10, None);
        let s = sel(short);
        assert_eq!((candidate(&s).asset_name.as_deref(), candidate(&s).artifact_sha256.as_deref()), (Some("lantern-glow-2.0.zip"), Some(GLOW_SHA)));
        // a version that cannot stand in a file name gives no name to look for
        assert_eq!(reason(&sel(with(rel(1, "v2.0.0+linux"), "lantern-glow-2.0.0+linux.zip", 10, None))), UnknownReason::MissingAsset);
        // without a template and without a binding: a version and a link, no file
        let advisory = select(&GithubSource { asset: None, ..stable }, "lantern-glow", &[with(rel(1, "v2.0.0"), "whatever.zip", 10, None)], true, None, no_read);
        let c = candidate(&advisory);
        assert_eq!((c.asset_id.as_ref(), c.asset_name.as_ref(), c.artifact_sha256.as_ref(), c.declared.as_ref()), (None, None, None, None));
    }

    /// A source whose one release has this tag and one zip (asset id 5000 of release 500) with this digest.
    fn selection_of(version_tag: &str, sha: Option<&str>) -> Selection {
        let r = with(rel(500, version_tag), "lantern-glow.zip", 10, sha);
        let s = select(&source(GLOW, "v{version}", Some("lantern-glow.zip"), UpdateChannel::Prerelease), "lantern-glow", &[r], true, None, no_read);
        assert!(matches!(s.outcome, Outcome::Candidate(_)), "{s:?}");
        s
    }

    #[test]
    fn comparing_with_what_is_installed() {
        let (a, b) = (GLOW_SHA, "b".repeat(64));
        let state = |have: Installed, sel: &Selection| {
            let v = compare(&have, sel, "0.2.3");
            assert_eq!(v.reason.is_some(), v.state == UpdateState::Unknown, "{v:?}");
            (v.state, v.reason)
        };
        let inst = |version: &'static str| Installed { version, ..Default::default() };
        let cand = selection_of("v1.10.0", Some(a));
        // lower and higher
        assert_eq!(state(inst("1.9.0"), &cand), (UpdateState::Available, None));
        assert_eq!(state(inst("1.9"), &cand), (UpdateState::Available, None));
        assert_eq!(state(inst("1.10.0-rc.1"), &cand), (UpdateState::Available, None), "a pre-release of it is below it");
        assert_eq!(state(inst("1.10.1"), &cand), (UpdateState::Current, None), "ahead of what is published");
        assert_eq!(state(inst("2.0.0-dev.1"), &cand), (UpdateState::Current, None));
        // dev.9 installed, dev.10 published
        assert_eq!(state(inst("1.2.0-dev.9"), &selection_of("v1.2.0-dev.10", None)), (UpdateState::Available, None));
        assert_eq!(state(inst("1.2.0-dev.10"), &selection_of("v1.2.0-dev.9", None)), (UpdateState::Current, None));
        // equal, and Kiln knows the installed archive: the same bytes are current, other bytes are a new upload
        assert_eq!(state(Installed { artifact_sha256: Some(a), ..inst("1.10.0") }, &cand), (UpdateState::Current, None));
        assert_eq!(state(Installed { artifact_sha256: Some(&b), ..inst("1.10.0") }, &cand), (UpdateState::NewUpload, None));
        assert_eq!(state(Installed { artifact_sha256: Some(a), ..inst("1.10") }, &cand), (UpdateState::Current, None), "1.10 is 1.10.0");
        // equal, and nothing is recorded about the installed copy (a folder copied by hand): never "new upload" from
        // a guess, never "current"
        assert_eq!(state(inst("1.10.0"), &cand), (UpdateState::Unknown, Some(UnknownReason::SameVersionUnbound)));
        // equal, the installed archive is known but the release states no digest: nothing to compare with
        let no_digest = selection_of("v1.10.0", None);
        assert_eq!(state(Installed { artifact_sha256: Some(a), ..inst("1.10.0") }, &no_digest), (UpdateState::Unknown, Some(UnknownReason::SameVersionUnbound)));
        // a recorded release or asset id (an install that knew them) that is another one now: a new upload
        assert_eq!(state(Installed { release_id: Some("499"), ..inst("1.10.0") }, &cand), (UpdateState::NewUpload, None));
        assert_eq!(state(Installed { release_id: Some("500"), asset_id: Some("1"), ..inst("1.10.0") }, &cand), (UpdateState::NewUpload, None));
        assert_eq!(state(Installed { release_id: Some("500"), asset_id: Some("5000"), artifact_sha256: Some(a), ..inst("1.10.0") }, &cand), (UpdateState::Current, None));
        assert_eq!(state(Installed { release_id: Some("500"), asset_id: Some("5000"), ..inst("1.10.0") }, &cand), (UpdateState::Unknown, Some(UnknownReason::SameVersionUnbound)),
            "the same ids without a digest do not prove the same bytes");
        // build metadata does not order versions: +a against +b is "equal", decided by the bytes like any equal pair
        let built = selection_of("v1.3.0+build.2", Some(a));
        assert_eq!(state(Installed { artifact_sha256: Some(&b), ..inst("1.3.0+build.1") }, &built), (UpdateState::NewUpload, None));
        assert_eq!(state(inst("1.3.0+build.1"), &built), (UpdateState::Unknown, Some(UnknownReason::SameVersionUnbound)));
        assert_eq!(state(inst("1.2.9+zzz"), &built), (UpdateState::Available, None));
        // an installed version that is no semantic version is not ordered by its text
        for odd in ["nov-2026", "final", "", "1.2.3.4"] {
            assert_eq!(state(inst(odd), &cand), (UpdateState::Unknown, Some(UnknownReason::NonSemver)), "{odd}");
        }
    }

    #[test]
    fn a_release_the_receipt_names_that_is_gone_is_unknown() {
        let stable = glow(UpdateChannel::Stable);
        let releases = [with(rel(40, "v1.2.0"), "lantern-glow-1.2.0.zip", 10, Some(GLOW_SHA))];
        // installed 1.3.0 from release 41, which the author deleted: the list's newest is lower, and still nobody says "current"
        let sel = select(&stable, "lantern-glow", &releases, true, Some("41"), no_read);
        assert_eq!(sel.missing_release.as_deref(), Some("41"));
        let v = compare(&Installed { version: "1.3.0", release_id: Some("41"), ..Default::default() }, &sel, "0.2.3");
        assert_eq!((v.state, v.reason), (UpdateState::Unknown, Some(UnknownReason::ReleaseRemoved)));
        // the same list for a copy whose receipt names no release (an archive imported by hand): ahead, so current
        let v = compare(&Installed { version: "1.3.0", artifact_sha256: Some(GLOW_SHA), ..Default::default() }, &sel, "0.2.3");
        assert_eq!((v.state, v.reason), (UpdateState::Current, None));
        // the release is there: nothing is missing
        let sel = select(&stable, "lantern-glow", &releases, true, Some("40"), no_read);
        assert_eq!(sel.missing_release, None);
        // a remembered selection made for another receipt does not speak for this one
        let other = select(&stable, "lantern-glow", &releases, true, Some("41"), no_read);
        let v = compare(&Installed { version: "1.2.0", release_id: Some("40"), artifact_sha256: Some(GLOW_SHA), ..Default::default() }, &other, "0.2.3");
        assert_eq!(v.state, UpdateState::Current);
    }

    #[test]
    fn compatibility_comes_from_the_binding_only() {
        let d = |side: Side, api: &str| Declared { side, api_version: api.into() };
        assert_eq!(compatibility(None, "0.2.3", Some(Side::Both)), Compatibility::Unknown, "no binding, no claim");
        assert_eq!(compatibility(Some(&d(Side::Both, "0.2")), "0.2.3", None), Compatibility::Compatible);
        assert_eq!(compatibility(Some(&d(Side::Both, "0.2.3")), "0.2.3", None), Compatibility::Compatible);
        assert_eq!(compatibility(Some(&d(Side::Both, "0.2.4")), "0.2.3", None), Compatibility::Incompatible, "needs a newer API than this Kiln has");
        assert_eq!(compatibility(Some(&d(Side::Both, "1.0")), "0.2.3", None), Compatibility::Incompatible);
        assert_eq!(compatibility(Some(&d(Side::Both, ">=0.2 <0.2.3")), "0.2.3", None), Compatibility::Incompatible);
        assert_eq!(compatibility(Some(&d(Side::Both, ">=0.2 <1")), "0.2.3", None), Compatibility::Compatible);
        // the installed copy loads on the server; a candidate for clients only is not a drop-in update
        assert_eq!(compatibility(Some(&d(Side::Client, "0.2")), "0.2.3", Some(Side::Server)), Compatibility::Incompatible);
        assert_eq!(compatibility(Some(&d(Side::Server, "0.2")), "0.2.3", Some(Side::Both)), Compatibility::Incompatible);
        assert_eq!(compatibility(Some(&d(Side::Both, "0.2")), "0.2.3", Some(Side::Server)), Compatibility::Compatible);
        assert_eq!(compatibility(Some(&d(Side::Server, "0.2")), "0.2.3", Some(Side::Server)), Compatibility::Compatible);
        assert_eq!(compatibility(Some(&d(Side::Both, "0.2")), "not a version", None), Compatibility::Unknown);
        // it rides along with the verdict, and does not change the state
        let binding = fixture("lantern-glow.kiln-release.json");
        let sel = select(&glow(UpdateChannel::Stable), "lantern-glow", &list("lantern-glow.releases.json"), true, None, |_, _| Fetched::Bytes(binding.clone()));
        let v = compare(&Installed { version: "1.9.0", side: Some(Side::Both), ..Default::default() }, &sel, "0.2.3");
        assert_eq!((v.state, v.compatibility), (UpdateState::Available, Compatibility::Compatible));
        let v = compare(&Installed { version: "1.9.0", ..Default::default() }, &sel, "0.1.0");
        assert_eq!((v.state, v.compatibility), (UpdateState::Available, Compatibility::Incompatible));
        let advisory = selection_of("v1.10.0", None);
        assert_eq!(compare(&Installed { version: "1.9.0", ..Default::default() }, &advisory, "0.2.3").compatibility, Compatibility::Unknown);
    }

    #[test]
    fn a_page_of_another_shape_is_refused_and_urls_are_never_taken_from_it() {
        assert!(parse_page(b"[]").unwrap().is_empty());
        for bad in [&b"{}"[..], b"null", b"[1]", b"[{}]", b"not json", br#"{"message": "Not Found"}"#,
            br#"[{"id": 1, "tag_name": "v1", "draft": false, "prerelease": false}]"#,
            br#"[{"id": 1, "tag_name": 7, "draft": false, "prerelease": false, "assets": []}]"#,
            br#"[{"id": 1, "tag_name": "v1", "draft": "no", "prerelease": false, "assets": []}]"#,
            br#"[{"id": -1, "tag_name": "v1", "draft": false, "prerelease": false, "assets": []}]"#,
            br#"[{"id": 1, "tag_name": "v1", "draft": false, "prerelease": false, "assets": [{"id": 2, "name": "a.zip"}]}]"#] {
            assert!(parse_page(bad).is_none(), "{}", String::from_utf8_lossy(bad));
        }
        let page = br#"[{"id": 1, "tag_name": "v1.0.0", "draft": false, "prerelease": false, "html_url": "https://evil.example/x",
            "assets": [{"id": 2, "name": "a.zip", "size": 3, "digest": "md5:abc", "browser_download_url": "https://evil.example/a.zip"}]}]"#;
        let r = parse_page(page).unwrap();
        assert_eq!(r[0].assets[0], Asset { id: "2".into(), name: "a.zip".into(), size: 3, sha256: None });
        assert_eq!(release_page("example-org/lantern-glow", Some("v1.0.0")), "https://github.com/example-org/lantern-glow/releases/tag/v1.0.0");
        assert_eq!(release_page("example-org/build-tags", Some("v1.3.0+build.2")), "https://github.com/example-org/build-tags/releases/tag/v1.3.0%2Bbuild.2");
        assert_eq!(release_page("example-org/kiln-mods", Some("mods/example/v1.5.0")), "https://github.com/example-org/kiln-mods/releases/tag/mods/example/v1.5.0");
        assert_eq!(release_page("example-org/odd-tags", None), "https://github.com/example-org/odd-tags/releases");
    }
}
