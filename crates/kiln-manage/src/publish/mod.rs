//! `kiln publish`: releasing a packed mod zip at a provider (GitHub first), as a transaction that can be planned
//! offline, reviewed, applied, resumed and reconciled. Contract: docs/kiln/modding/publishing.md.
//!
//! | module | what |
//! |---|---|
//! | [`policy`] | the artifact policy: limits, path rules, what a published zip may not hold |
//! | [`artifact`] | the audit of one zip against that policy (feature `zip`) |

pub mod policy;

#[cfg(feature = "zip")]
pub mod artifact;
