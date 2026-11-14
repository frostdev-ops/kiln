# Publishing a mod: `kiln publish`

`kiln publish` releases a packed mod zip as a **GitHub release**: the zip, its `.sha256` and a `kiln-release.json`
that tells Kiln's update check which mod, version and file the release holds. It works in two separate steps, a plan
you read and an apply you confirm, and it keeps a journal so that a crash or a lost connection never uploads
something twice.

> Tested against recorded-style answers only. No request of this command has been sent to GitHub by its authors;
> the first real release should be made to a repository you can throw away (see "The first real run").

What it does not do: create your repository or your mod page, publish to Nexus Mods, post a changelog or a comment
anywhere, replace or delete anything at GitHub, move a tag.

## 1. The config: `kiln.publish.json`

Next to your project ([schema](schemas/kiln-publish.md)):

```json
{
  "format": 1,
  "provider": "github",
  "github": {
    "repository": "you/my-mod",
    "tag": "v{version}",
    "commit": "0123456789abcdef0123456789abcdef01234567"
  },
  "notes": "docs/release-notes.md"
}
```

- `tag`: `{version}` is the version in your manifest. In a repository of several mods use `mods/my-mod/v{version}`.
- `commit`: the full id of the commit the release is made from (`git rev-parse HEAD`), never a branch name.
- `notes`: a UTF-8 text file; it becomes the text of the release.
- The file never holds a token.

## 2. The token

Create a fine-grained personal access token at GitHub for that one repository with **Contents: read and write**.
Store it in this PC's credential store (it is read from standard input, never from an argument):

```sh
kiln publish --store-token github     # paste the token, Enter
kiln publish --forget-token github    # removes it again
```

Where there is no credential store (a CI job, a PC without `secret-tool`), set the environment variable
`KILN_PUBLISH_GITHUB_TOKEN`. It is used only when the store has no entry; no other variable is read. The token goes
to `api.github.com` and `uploads.github.com` and nowhere else (a download that GitHub redirects to its storage goes
there without it), and is never written to a plan, a journal or a log.

## 3. Plan (sends nothing)

```sh
kiln publish --project . --to github --plan release-plan.json      # packs in Release first
kiln publish --artifact bin/Release/my-mod-1.2.0.zip --to github --plan release-plan.json
```

The zip is audited (below), copied into a transaction folder under Kiln's data folder together with the config and
the notes, and frozen there. `release-plan.json` says what will be created: repository, tag, commit, the three
assets with their SHA-256. Read it. From here on the project's zip is not read again; if you change the zip, the
notes or `kiln.publish.json`, make a new plan (`--detached-plan` makes a plan that can be applied without the project
at hand).

## 4. Apply (creates a draft)

```sh
kiln publish --apply release-plan.json --confirm
```

Creates a **draft** release at the planned tag and commit and uploads the three assets. Nothing is public yet. Look
at the draft at GitHub.

If it stops (no connection, a refused token, a rate limit): what is listed as confirmed exists, nothing was rolled
back, and `kiln publish --resume <transaction> --confirm` goes on from there. Before a step is sent again Kiln asks
GitHub what exists; it adopts its own draft or asset (recognised by a marker at the end of the release text, and by
name, size and digest) and never creates a second one.

When GitHub lists an asset of the planned name and size but gives no digest for it, Kiln downloads that one asset
and compares its SHA-256 with the plan's: the same bytes are adopted, other bytes are a conflict. The download asks
`api.github.com` with your token for at most the planned number of bytes and follows at most one redirect, only to
GitHub's storage (`release-assets.githubusercontent.com` or `objects.githubusercontent.com`), which gets no token.
Nothing is ever uploaded over the asset or deleted.

If it says a step needs you to look ("manual reconciliation"): Kiln could not tell whether a request arrived, or
something that it did not make is in the way (an asset of the same name with other content, a half-finished upload),
or an asset without a digest could not be downloaded to compare. Follow the line it prints, fix it at GitHub by hand
if needed, then bind the step:
`kiln publish --resolve-step <step> --remote-id <id> --transaction <transaction> --confirm`. The id is checked
against the plan, not trusted: for an asset GitHub lists without a digest, binding downloads it again the same way
and accepts it only with the planned SHA-256.

## 5. Publish the draft

```sh
kiln publish --publish-draft <transaction> --confirm
```

A separate, confirmed step: it makes that one draft public, only if it still carries the transaction's marker.

## What a published zip may hold

One mod: a folder named after the mod id with `kiln.mod.json` (or `native.json`) in it; a zip with the manifest at
its top is rearranged into that form when it is frozen. At most 512 MiB, 4096 files, 256 MiB per file.

Refused, each with the file's name and a reason code (`--json` shows it):

| Reason | What |
|---|---|
| `game-data` | the game's archives (`.kfc`, `.kfc_resources`, by name or by content) and the game's own files |
| `cooked-output` | anything under `assets/cooked/`, a folder named `cooked`, or the `out` folder of a cook recipe: cooked files are made from the player's own game on the player's PC |
| `save-data`, `runtime-state` | saves, databases, `worlds/`, `characters/`, `savegames/`, `logs/`, `state/`, `kiln/data/` |
| `secret-file`, `private-key` | `.env*`, key and certificate stores, files named `credentials`, `secrets`, `token`; a private-key header inside any file |
| `runtime-dll` | `Kiln.*.dll`, `System.*.dll`, the .NET host: never part of a mod |
| `undeclared-dll` | a DLL that is not the mod's own and has no `packageFiles` entry |
| `undeclared-default-config`, `secret-in-config` | a file under `config/defaults/` without a `packageFiles` entry, or with a value for a key that names a password, token, secret, API key or credential |
| `vcs-or-build-folder` | `.git`, `bin/`, `obj/`, `.pdb` |
| `encrypted`, `link`, `path-*`, `duplicate-name`, `case-collision`, `ratio`, `member-too-large`, ... | a zip that is not a plain archive of files |

Default configs must be UTF-8 JSON or JSONC so every value, including duplicate keys, can be checked. Other formats
are refused until a parser can audit them. An unreadable cook recipe is also refused because its outputs cannot
be classified. Plans made under an older artifact policy must be made again.

`packageFiles` in `kiln.publish.json` declares the two things that need a declaration; it cannot allow anything
from the list above:

```json
"packageFiles": [
  { "path": "config/defaults/my-mod.json", "sha256": "<sha256 of the file>", "purpose": "default-config" },
  { "path": "lib/Some.Library.dll", "sha256": "<sha256 of the file>", "purpose": "dependency", "license": "MIT" }
]
```

Passing the audit does not make a zip yours to publish: a file name is no proof of who owns its content. That
judgement, the mod page, screenshots and the Nexus Mods page stay with you.

## The first real run

1. Make an empty repository you can delete, with one commit; put its commit id into `kiln.publish.json`.
2. Create a token for that repository only; `kiln publish --store-token github`.
3. Plan, read the plan, apply. At GitHub: one draft for the tag, three assets; download the zip and compare its
   SHA-256 with the plan's.
4. `--publish-draft`; the release is public and the tag points at your commit.
5. Delete the repository and revoke the token.
