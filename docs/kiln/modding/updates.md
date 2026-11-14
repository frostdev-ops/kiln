# Update checks for mods

Kiln can tell a player that a newer version of an installed mod is published. It tells, with a link to the page; it
never downloads or installs a version. Two things run a check: the launcher (above the mod list, and once a day when
the player switches that on; [../launcher/catalog.md](../launcher/catalog.md), "Update notices") and
`kiln updates check` ([cli.md](cli.md)). Nothing in the game makes a request.

## Making your mod checkable
Publish releases on GitHub and name the repository in the manifest ([manifest.md](manifest.md), "updates"):

```jsonc
"updates": { "provider": "github", "repository": "you/your-mod", "asset": "your-mod-{version}.zip" }
```
or in the project: `<KilnUpdatesRepository>you/your-mod</KilnUpdatesRepository>`.
The sample `mods/samples/Hello` has the key (with a made-up repository).

- Tag each release `v<version>` (or set `tagPattern`; in a repository of several mods give each its own prefix, such
  as `mods/your-mod/v{version}`). The version in the tag is the manifest's `version`.
- Attach the mod's zip. `kiln publish` ([publishing.md](publishing.md)) does all of this and adds
  `kiln-release.json`, which tells the check the zip's name, size and SHA-256, the side and the API version the
  release needs. With it Kiln can say whether the update loads on the player's Kiln, and can tell "you have this
  exact file" from "the same version was uploaded again".
- A mod on Nexus Mods needs nothing: an install from the launcher's Catalog is recorded and checked with the
  player's sign-in. (`"provider": "nexus"` in a manifest names the file to follow; it is checked for installs the
  Catalog made.)

## How the newest release is chosen (GitHub)
1. The release list is read: 100 per page, at most 5 pages. Drafts never count.
2. A release counts when its **whole tag** matches the pattern and the part at `{version}` is a semantic version.
   No wildcards, no "ends with".
3. On channel `stable`, releases marked pre-release on GitHub and versions with a pre-release part (`1.2.0-rc.1`) do
   not count; channel `prerelease` takes both.
4. The highest version wins by semver precedence (`1.10` over `1.9`, `dev.10` over `dev.9`; build metadata after `+`
   does not order versions). Two releases of the same precedence: nobody wins (`ambiguous_release`); the publication
   date breaks no tie.
5. Only the winner's `kiln-release.json` is read (at most 256 KiB). When it is there it must be valid, hold exactly one
   record of the mod, name the tag's version and a file of the release with the stated size; otherwise the release
   is not selectable (`invalid_metadata`), and no older release is taken instead. Without one, the result is a version
   and a link, with compatibility unknown.
6. The zip is the one file named by `asset` (with the version as the tag spells it) or by `kiln-release.json`: none
   is `missing_asset`, several are `ambiguous_asset`.

## What a check says
One status per installed mod (`ModUpdateStatus`; `kiln updates check --json` prints them):

| State | Meaning |
|---|---|
| `available` | A higher version is published. |
| `new-upload` | The same version is published as another file than the one installed. Said only when Kiln recorded which file was installed (below). |
| `current` | The installed version is the newest published one (with the same file), or ahead of it. |
| `unknown` | The check cannot tell; `reason` says why. Never "up to date". |
| `unsupported` | The mod has no known source. |
| `disabled` | The provider of its source is switched off in this build. |

`stale: true` marks a status the provider did not confirm within the last day, or whose last refresh failed. An update
seen earlier stays (`available`, stale); an earlier `current` becomes `unknown`.

| `reason` | Meaning |
|---|---|
| `incomplete_list` | The repository has more than 500 releases: the newest cannot be claimed. |
| `invalid_metadata` | The newest release's `kiln-release.json` is not valid, or not about this mod and release. |
| `missing_asset` | The newest release has no file with the expected name. |
| `ambiguous_asset` | It has several. |
| `ambiguous_release` | Several releases share the highest version. |
| `non_semver` | The installed version, or the tags that fit the pattern, are not semantic versions: they are not ordered by their text. |
| `same_version_unbound` | The newest published version is the installed one, and Kiln does not know which file is installed (a folder copied in by hand, or a release that states no digest). |
| `network` | No answer: offline, a failed request, or no sign-in where one is needed. |
| `source_conflict` | The mod's source changed (another repository or file) and the player has not confirmed it. |
| `rate_limited` | The provider's request limit is used up (anonymous GitHub: 60 requests an hour per connection). Nothing is asked until it resets; `--refresh` and "Check for updates" do not get around it. |
| `not_found` | The repository does not exist or is private, or no release fits the tag pattern and channel. |
| `not_checked` | Nothing is remembered yet (an offline check before the first online one), the check covered 500 sources already, or a Nexus Mods source of a mod the Catalog did not install. |
| `release_removed` | The release the installed copy is recorded to come from is gone from the list. |

`compatibility` is `compatible` or `incompatible` only from what `kiln-release.json` declares (the API range against
this Kiln's mod API, the side against the installed copy's); otherwise `unknown`. `coverage` is `incomplete` when the
list was not read to its end.

## Which file is installed
When a player adds a mod as an archive (zip, 7z, rar), Kiln records the archive's SHA-256 for every mod it held
(`<data>/updates/receipts.json`). A release's `kiln-release.json` states its zip's SHA-256. Equal version and equal
digest: `current`. Equal version and another digest: `new-upload`. No record (a folder, a mod added before
0.2.0-dev.28) or no digest in the release: `same_version_unbound`. Kiln never guesses from dates or file names.

## What a check sends, and what it keeps
- To GitHub: `GET api.github.com/repos/<owner>/<repo>/releases` and, for the newest release only, its
  `kiln-release.json` through the asset endpoint (one redirect, to GitHub's own two asset hosts, is followed; any
  other is refused). No account, token or cookie; the User-Agent is `kiln/<version>`. No request names more than one
  repository, and none carries the installed version, a hash, a path or anything about the player or the PC. GitHub
  still sees the connection's address and which repositories were asked about.
- To Nexus Mods: only the launcher's signed-in check, for mods installed from its Catalog. `kiln updates check` makes
  no request to Nexus Mods and shows what the launcher last saw.
- On disk: `<data>/updates/cache.json` (the last answer per source, its ETag, waits after failures and request
  limits; at most 10 MiB, rebuilt when unreadable). A source answered within 30 minutes is not asked again by a manual
  check (within a day by the daily one); `--refresh` asks anyway. After a failed request the next try waits 5 minutes,
  doubling up to a day. A request has 10 seconds to connect and 10 to answer.

## Not there yet
- Kiln does not install an update: get it from its page and add it like any mod.
- GitHub only for manifests; other hosts and feed URLs are not read.
- Not verified against the real GitHub: the provider is tested against recorded-style answers only.
