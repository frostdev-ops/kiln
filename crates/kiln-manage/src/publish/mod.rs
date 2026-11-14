//! `kiln publish`: releasing a packed mod zip at a provider (GitHub first), as a transaction that can be planned
//! offline, reviewed, applied, resumed and reconciled. Contract: docs/kiln/modding/publishing.md.
//!
//! | module | what |
//! |---|---|
//! | [`policy`] | the artifact policy: limits, path rules, what a published zip may not hold |
//! | [`artifact`] | the audit of one zip against that policy (feature `zip`) |
//! | [`config`] | `kiln.publish.json` |
//! | [`canon`] | canonical JSON (RFC 8785) for integers and strings |
//! | [`txdir`] | the transaction folder and its frozen files |
//! | [`plan`] | the publish plan, format 1: making one (no network) and loading one for apply (feature `zip`) |

pub mod canon;
pub mod config;
pub mod policy;
pub mod txdir;

#[cfg(feature = "zip")]
pub mod artifact;
#[cfg(feature = "zip")]
pub mod plan;
