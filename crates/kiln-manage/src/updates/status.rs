//! `ModUpdateStatus`: what one check says about one installed mod. The CLI prints it, the launcher shows it; both
//! get the same value (`kiln updates check --json` is its JSON).
//!
//! The rule every producer follows: a check that did not succeed never ends in `current`. It ends in `unknown` with
//! a reason, or it keeps an earlier `available` / `new-upload` marked `stale`.

use crate::library::LibraryRef;
use serde::{Deserialize, Serialize};

/// Who is asked.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Provider {
    Github,
    Nexus,
}

impl Provider {
    pub fn as_str(self) -> &'static str {
        match self {
            Provider::Github => "github",
            Provider::Nexus => "nexus",
        }
    }
}

/// Why this source is the mod's: the player's own mapping, the record of an install, or the author's manifest.
/// That is also the order in which they win.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SourceOrigin {
    Manual,
    Receipt,
    Manifest,
}

/// The source a status was checked against.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceInfo {
    pub provider: Provider,
    /// `owner/repo` for GitHub; `enshrouded/<mod id>/<file chain id>` for Nexus Mods.
    pub destination: String,
    pub origin: SourceOrigin,
    /// True when the check uses the player's account at the provider (Nexus Mods); false for an anonymous read.
    pub authenticated: bool,
    /// The source's complete settings, so editing it preserves its tag pattern, asset and channel. Optional when
    /// reading an older result.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub spec: Option<crate::manifest::UpdatesSpec>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum UpdateState {
    /// A higher version is published.
    Available,
    /// The same version was published again as other bytes (and the installed copy is known to be the earlier ones).
    NewUpload,
    /// The installed version is the newest published one, or ahead of it.
    Current,
    /// The check could not tell: `reason` says why. Never "up to date".
    Unknown,
    /// The mod has no known source (no mapping, no install record, no readable `updates` in its manifest).
    Unsupported,
    /// The provider of the mod's source is switched off in this build.
    Disabled,
}

impl UpdateState {
    pub fn as_str(self) -> &'static str {
        match self {
            UpdateState::Available => "available",
            UpdateState::NewUpload => "new-upload",
            UpdateState::Current => "current",
            UpdateState::Unknown => "unknown",
            UpdateState::Unsupported => "unsupported",
            UpdateState::Disabled => "disabled",
        }
    }
}

/// Whether the candidate would load here, from what its release declares (API range, side). `unknown` when the
/// release says nothing.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Compatibility {
    Compatible,
    Incompatible,
    Unknown,
}

/// Whether the provider's list was read to its end. `incomplete`: a newer version may exist beyond what was read.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Coverage {
    Complete,
    Incomplete,
}

/// Why a status is `unknown`, or why a `stale` one could not be refreshed. The first nine are the dossier's; the
/// last four name cases it leaves to "unknown" without a name.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum UnknownReason {
    /// The release list has more pages than a check reads.
    IncompleteList,
    /// The newest release's `kiln-release.json` is not valid, or does not describe this mod and this release.
    InvalidMetadata,
    /// The newest release has no file with the expected name.
    MissingAsset,
    /// The newest release has several files with the expected name.
    AmbiguousAsset,
    /// Several releases share the highest version.
    AmbiguousRelease,
    /// The installed version, or every published one, is not a semantic version.
    NonSemver,
    /// The newest published version equals the installed one, and Kiln does not know which bytes are installed.
    SameVersionUnbound,
    /// No answer (offline, a failed request, no sign-in where one is needed).
    Network,
    /// The mod's source changed (another provider, repository or file) and the player has not confirmed it.
    SourceConflict,
    /// The provider's request limit is reached; `kiln updates check --refresh` cannot get around it.
    RateLimited,
    /// The repository does not exist (or is private), or it has no release whose tag matches.
    NotFound,
    /// Never checked yet (an offline check with nothing remembered, or more sources than one check covers).
    NotChecked,
    /// The release the installed copy came from is gone from the list.
    ReleaseRemoved,
}

impl UnknownReason {
    pub fn as_str(self) -> &'static str {
        match self {
            UnknownReason::IncompleteList => "incomplete_list",
            UnknownReason::InvalidMetadata => "invalid_metadata",
            UnknownReason::MissingAsset => "missing_asset",
            UnknownReason::AmbiguousAsset => "ambiguous_asset",
            UnknownReason::AmbiguousRelease => "ambiguous_release",
            UnknownReason::NonSemver => "non_semver",
            UnknownReason::SameVersionUnbound => "same_version_unbound",
            UnknownReason::Network => "network",
            UnknownReason::SourceConflict => "source_conflict",
            UnknownReason::RateLimited => "rate_limited",
            UnknownReason::NotFound => "not_found",
            UnknownReason::NotChecked => "not_checked",
            UnknownReason::ReleaseRemoved => "release_removed",
        }
    }

    pub const ALL: [UnknownReason; 13] = [
        UnknownReason::IncompleteList,
        UnknownReason::InvalidMetadata,
        UnknownReason::MissingAsset,
        UnknownReason::AmbiguousAsset,
        UnknownReason::AmbiguousRelease,
        UnknownReason::NonSemver,
        UnknownReason::SameVersionUnbound,
        UnknownReason::Network,
        UnknownReason::SourceConflict,
        UnknownReason::RateLimited,
        UnknownReason::NotFound,
        UnknownReason::NotChecked,
        UnknownReason::ReleaseRemoved,
    ];
}

/// What the candidate is at its provider. Ids are the provider's, kept as opaque text.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CandidateIdentity {
    pub release_id: Option<String>,
    pub tag: Option<String>,
    pub asset_id: Option<String>,
    pub asset_name: Option<String>,
    /// Lower-case hex, when the release states it.
    pub artifact_sha256: Option<String>,
    /// Nexus Mods: the id of the newest version of the file.
    pub file_version_id: Option<String>,
}

/// The result of an update check for one installed mod. Every key is always present in the JSON (`null` when it has
/// no value).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModUpdateStatus {
    pub library_ref: LibraryRef,
    /// The mod's display name.
    pub name: String,
    /// None: no known source (`unsupported`).
    pub source: Option<SourceInfo>,
    pub installed_version: String,
    /// The newest published version found, as its provider spells it.
    pub candidate_version: Option<String>,
    pub candidate_identity: Option<CandidateIdentity>,
    pub state: UpdateState,
    pub compatibility: Compatibility,
    pub coverage: Coverage,
    /// When the provider last answered for this mod (RFC 3339, UTC).
    pub checked_at: Option<String>,
    /// The answer is older than a day, or a later try to refresh it failed. A stale status is never `current`.
    pub stale: bool,
    /// Why the state is `unknown`; on a stale status, why it could not be refreshed.
    pub reason: Option<UnknownReason>,
    /// The page to open for the candidate (a release page, the mod's files page). Text from the provider: show it,
    /// never run it.
    pub page_url: Option<String>,
}

impl ModUpdateStatus {
    /// A status with nothing known yet: `unknown / not_checked`.
    pub fn new(library_ref: LibraryRef, name: &str, source: Option<SourceInfo>) -> ModUpdateStatus {
        ModUpdateStatus {
            installed_version: library_ref.version.clone(),
            library_ref,
            name: name.to_string(),
            source,
            candidate_version: None,
            candidate_identity: None,
            state: UpdateState::Unknown,
            compatibility: Compatibility::Unknown,
            coverage: Coverage::Incomplete,
            checked_at: None,
            stale: false,
            reason: Some(UnknownReason::NotChecked),
            page_url: None,
        }
    }

    pub fn unknown(mut self, reason: UnknownReason) -> ModUpdateStatus {
        self.state = UpdateState::Unknown;
        self.reason = Some(reason);
        self
    }

    pub fn with_state(mut self, state: UpdateState) -> ModUpdateStatus {
        self.state = state;
        self.reason = None;
        self
    }

    /// Marks the status as not confirmed by the provider now, and applies the rule that goes with it: an `available`
    /// or `new-upload` seen earlier stays (the newer version was there), a `current` does not (nobody knows any more),
    /// and `why` says what stopped the refresh.
    pub fn into_stale(mut self, why: UnknownReason) -> ModUpdateStatus {
        self.stale = true;
        match self.state {
            UpdateState::Available | UpdateState::NewUpload => self.reason = Some(why),
            UpdateState::Current => {
                self.state = UpdateState::Unknown;
                self.reason = Some(why);
            }
            // an unknown keeps its own reason: it says more than "could not refresh"
            UpdateState::Unknown | UpdateState::Unsupported | UpdateState::Disabled => {}
        }
        self
    }

    /// The invariant of the type, checked by the coordinator on everything a provider returns.
    pub fn is_consistent(&self) -> bool {
        let reason_ok = match self.state {
            UpdateState::Unknown => self.reason.is_some(),
            UpdateState::Available | UpdateState::NewUpload => self.reason.is_none() || self.stale,
            UpdateState::Current | UpdateState::Unsupported | UpdateState::Disabled => self.reason.is_none(),
        };
        let stale_ok = !(self.stale && self.state == UpdateState::Current);
        let source_ok = (self.state == UpdateState::Unsupported) == self.source.is_none();
        reason_ok && stale_ok && source_ok
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::manifest::ModKind;

    fn status() -> ModUpdateStatus {
        let r = LibraryRef { kind: ModKind::Kiln, id: "m".into(), version: "1.0.0".into(), hash: "kdh1:0".into() };
        let source = SourceInfo { provider: Provider::Github, destination: "example/mod".into(), origin: SourceOrigin::Manifest, authenticated: false, spec: None };
        ModUpdateStatus::new(r, "M", Some(source))
    }

    #[test]
    fn a_stale_status_is_never_current() {
        let s = status().with_state(UpdateState::Current).into_stale(UnknownReason::Network);
        assert_eq!((s.state, s.reason, s.stale), (UpdateState::Unknown, Some(UnknownReason::Network), true));
        let s = status().with_state(UpdateState::Available).into_stale(UnknownReason::RateLimited);
        assert_eq!((s.state, s.reason, s.stale), (UpdateState::Available, Some(UnknownReason::RateLimited), true));
        let s = status().with_state(UpdateState::NewUpload).into_stale(UnknownReason::Network);
        assert_eq!((s.state, s.stale), (UpdateState::NewUpload, true));
        let s = status().unknown(UnknownReason::AmbiguousRelease).into_stale(UnknownReason::Network);
        assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::AmbiguousRelease)));
        assert!(s.is_consistent());
    }

    #[test]
    fn consistency() {
        assert!(status().is_consistent());
        assert!(status().with_state(UpdateState::Current).is_consistent());
        let mut s = status().with_state(UpdateState::Current);
        s.stale = true;
        assert!(!s.is_consistent(), "stale and current");
        let mut s = status();
        s.reason = None;
        assert!(!s.is_consistent(), "unknown without a reason");
        let mut s = status().with_state(UpdateState::Unsupported);
        assert!(!s.is_consistent(), "unsupported with a source");
        s.source = None;
        assert!(s.is_consistent());
    }

    /// The JSON of a status: the names S6 and scripts read. A change here is a contract change.
    #[test]
    fn the_json_shape_is_fixed() {
        let mut s = status().with_state(UpdateState::NewUpload);
        s.candidate_version = Some("1.0.0".into());
        s.candidate_identity = Some(CandidateIdentity { release_id: Some("7".into()), tag: Some("v1.0.0".into()), ..Default::default() });
        s.coverage = Coverage::Complete;
        s.checked_at = Some("2026-11-13T10:00:00Z".into());
        s.page_url = Some("https://github.com/example/mod/releases/tag/v1.0.0".into());
        let v = serde_json::to_value(&s).unwrap();
        assert_eq!(v, serde_json::json!({
            "libraryRef": {"kind": "kiln", "id": "m", "version": "1.0.0", "hash": "kdh1:0"},
            "name": "M",
            "source": {"provider": "github", "destination": "example/mod", "origin": "manifest", "authenticated": false},
            "installedVersion": "1.0.0",
            "candidateVersion": "1.0.0",
            "candidateIdentity": {"releaseId": "7", "tag": "v1.0.0", "assetId": null, "assetName": null, "artifactSha256": null, "fileVersionId": null},
            "state": "new-upload",
            "compatibility": "unknown",
            "coverage": "complete",
            "checkedAt": "2026-11-13T10:00:00Z",
            "stale": false,
            "reason": null,
            "pageUrl": "https://github.com/example/mod/releases/tag/v1.0.0",
        }));
        assert_eq!(serde_json::from_value::<ModUpdateStatus>(v).unwrap(), s);
        for r in UnknownReason::ALL {
            assert_eq!(serde_json::to_value(r).unwrap(), r.as_str());
        }
        for st in [UpdateState::Available, UpdateState::NewUpload, UpdateState::Current, UpdateState::Unknown, UpdateState::Unsupported, UpdateState::Disabled] {
            assert_eq!(serde_json::to_value(st).unwrap(), st.as_str());
        }
    }
}
