# Build records

`sigdb/builds/<side>/<sha256>.json` describes one game executable: which build it is, whether Kiln's hooks may run on
it, and how far each name in the symbol database (`sigdb/symbols/`) has been proven on it. `side` is `server`
(`enshrouded_server.exe`) or `client` (`enshrouded.exe`); the file name is the exe's sha256.

Under the policy `verified-only` the core installs hooks only when the record of the running exe says
`"verified": true` (`native/core/src/config.cpp`). The core reads `verified` and `note` and nothing else; the other
fields are for people and tools. A record is written and kept up to date by `tools/sigport/build_record.py`; nobody
edits levels by hand.

## Record format 2

| Field | Type | Meaning |
|---|---|---|
| `sha256` | string | sha256 of the exe file. |
| `side` | `"server"` \| `"client"` | Which definitions apply: `<side>.json` and the composed `<side>.d` set (`tools/sigport/sides.py`). |
| `record` | `2` | Format version. A record without it is format 1 (only the fields of today, no levels). |
| `codeview` | string | PDB GUID and age from the exe's debug directory, `<GUID>-<age>`. |
| `pdb` | string | PDB file name from the same entry (`enshrouded_server.pdb`, `enshrouded.pdb`). |
| `steam_build` | int | Steam build id of the depot that shipped the exe. The newest record of a side is the one with the highest value. |
| `svn` | int | The game's own build number (`svn` in its log). |
| `verified` | bool | `true` lets `verified-only` install hooks on this exe. Set only after the runtime checks named in `note` passed. |
| `note` | string | What was run on this exe and what it showed, in plain words, with the test counts. |
| `declared` | object | `{"symbols": n, "constants": n, "features": n}`: how many names sigdb declared for this side when the record was last written. |
| `features` | object | `name: bool`, every feature of the offline resolve, on or off. |
| `symbols` | object | `name: "0x<rva>"`, every resolved named symbol (no `@` probes) of the offline resolve. |
| `constants` | object | `name: "0x<value>"`, every resolved constant of the offline resolve. |
| `services` | object | Per service, the level of each declared name (below). |

`features`, `symbols` and `constants` are the format-1 maps, unchanged in name, type and meaning. They are written
again from the exe on every refresh.

### `services`
The key is `core` for `sigdb/symbols/<side>.json`, else the stem of the composed `<side>.d` file that declares the
names (`players` for `server.d/players.json`, `ui` for `client.d/ui.json`).

```json
"players": {
  "level": "lab",
  "date": "2026-11-11",
  "evidence": "one line: which test mods, pass/fail counts, which lab or boot",
  "symbols":   { "players/PlayerJoin": "lab", "@players/PlayerJoin.fields": "lab" },
  "constants": { "players/rec.name": "lab" },
  "features":  { "players.join": "lab" },
  "unresolved": { "players/Old": "players/Old.find[0]: 0 matches" }
}
```

| Field | Meaning |
|---|---|
| `level` | The level the service's last run reached: `static`, `lab` or `seen`. |
| `date` | `YYYY-MM-DD` of the run `level` rests on. For `static`, the day the service entered the record. |
| `evidence` | One line saying what that run was: the test mods with passed/failed counts, the lab or boot. |
| `symbols`, `constants`, `features` | `name: level` for every name the service's file declares. Symbols include `@` probes. |
| `unresolved` | `name: reason` for each name at `unresolved`; present only when there is one. |

A name keeps its own level, so a name added after the service's run sits at `static` under a service at `lab`.

## Levels

| Level | Means |
|---|---|
| `static` | The name resolved in this exe offline (`tools/sigport/sigdb_check.py`'s resolver, the core's rules: one match per strategy, captures agree, a feature's symbols and constants all resolved). Nothing ran. |
| `lab` | It resolved, and in a lab boot of this exe the service that declares it loaded and every test mod of that service finished with no failure. The running core resolved the same value as the offline resolve. It does not claim each symbol was exercised: a test mod covers what its tests call, and a name no test reaches is `lab` because its service loaded and passed, not because it was called. |
| `seen` | A guided check passed in a real game client on this exe. Defined for the format; nothing sets it yet. |
| `unresolved` | sigdb declares the name, but it does not match in this exe. The reason is in the service's `unresolved` map. |

On a client record, `lab` can also come from a real-client boot made before this format existed; the service's
`evidence` then says which boot and what it covered.

## Which names a record lists ("declared")
A name is declared for a side when it is a key under `symbols`, `constants` or `features` in
`sigdb/symbols/<side>.json` or in a composed `<side>.d` file, `@` probes included, or when a pattern hole captures it
(`{name:rel32}` declares a symbol, `{name:d8|d16|d32|s32}` a constant). Every declared name is listed exactly once,
under the file that declares it. Corner cases:

- **Key in one file, captured in another**: the key wins; the name is listed under the file with the key.
- **Captured only**: listed under the first file whose pattern captures it, in the core's load order (`<side>.json`
  first, then the `.d` files sorted by name).
- **The same key in two files** (an error the core logs as "defined twice"): listed under the first file in load
  order, the one whose definition the core uses.
- **Client overlays**: a `client.d/<x>.json` with `"extends"` is composed over its server base first, so its names
  (with the base's, minus `remove`) are listed under `x`. A shared `server.d/<x>.json` without a client twin is listed
  under `x` in the client record too.
- **Features** declare only their own name; the symbols and constants a feature needs are listed where they are
  declared.

## Keeping a record current
```sh
python3 tools/sigport/build_record.py <side> <exe> --steam-build N [--svn N]   # a new record, every name static
```
