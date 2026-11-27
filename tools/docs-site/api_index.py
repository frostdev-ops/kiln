#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The agent API index: a compact, generated, greppable list of the whole public mod API.

  python3 -I tools/docs-site/api_index.py [--xml DIR] [--out DIR] [--tree DIR]        write the index
  python3 -I tools/docs-site/api_index.py --check [--xml DIR]                         exit 1 when the committed index is stale

It writes agent/skills/kiln-modding/references/api-index/ (a skill folder an agent greps for a name or a word of a
summary; SKILL.md, step "Find or check an API"):
  INDEX.md       one table: assembly, its `KilnServices` entry, purpose, guide pages, file
  <Entry>.md     one file per assembly, named by its `KilnServices` entry (Kiln.Items -> Items.md, Kiln.Api -> Api.md); each
                 type is a `## Heading` with the first sentence of its summary, each member one line: signature, the mod API
                 that added it, the first sentence of its summary; an enum's values on one line. An assembly over 100 KB is
                 split into parts of about 60 KB at most (<Entry>-1.md, <Entry>-2.md ...) and <Entry>.md is then a map of the
                 parts; Kiln.Blocks always, by family (the folders of its sources: Blocks-items.md, Blocks-world.md ...).
  native.md      the native ABI rows (header, table.row, offset, since, leading comment), from native/core/abi/*.h and
                 crates/sfi/kiln-abi/abi-manifest.json
Inputs, nothing else:
  PublicAPI.Shipped.txt + PublicAPI.Unshipped.txt of every gated managed project (the exact surface: Shipped, plus the
      additions of Unshipped, minus its *REMOVED* lines), without the experimental [KILNX...] members (lab, test and not yet
      proven API: counted in INDEX.md), compiler-made record members and object overrides
  the XML doc files of a Release build of those projects (--xml DIR; without it the generator builds them, see --build-dir)
  managed/api-history.tsv, the "since" ledger (tools/release/api-gate.sh history): the mod API of the release that first
      shipped an entry; a line still in Unshipped.txt carries the upcoming mod API (Kiln.Mods.ApiVersion, managed/Kiln.Api/Mods.cs);
      no mark = the mod API 0.2.1 or older; a member shows a mark only when it differs from its type's
Same inputs, same bytes: everything is sorted, no date, no path, no host.

--xml DIR: where the compiled XML docs are: DIR/<Assembly>.xml (a pack stage, managed/Kiln.Sdk/pack.sh's mdk-stage), or
  DIR/<Assembly>/release/<Assembly>.xml (an --artifacts-path bin folder: tools/check.sh's $KILN_OUT/dotnet/bin), or
  DIR/bin/<Assembly>/release/<Assembly>.xml. KILN_API_XML sets it too. Without either, `dotnet build` of the gated projects
  (Release) runs into --build-dir (default $KILN_OUT/api-index, $KILN_OUT as in tools/check.sh: <tree>/native/build); this
  wave runs that as `tools/slot python3 -I tools/docs-site/api_index.py`.
--check: generates the files in memory (nothing is written) and compares them with --out; prints what differs and exits 1 on
  any difference, a missing file or an extra one. tools/check.sh runs it on the build it has made anyway (stage `index`);
  release.sh runs the generator before its export.
Exit codes: 0 ok, 1 stale (--check), 2 bad input or a failed build.
Tests: python3 -I tools/docs-site/test_api_index.py
"""
import argparse
import collections
import difflib
import json
import os
import re
import subprocess
import sys
import unicodedata
import xml.etree.ElementTree as ET
from pathlib import Path

INDEX_DIR = "agent/skills/kiln-modding/references/api-index"
LEDGER = "managed/api-history.tsv"
MODS_CS = "managed/Kiln.Api/Mods.cs"
MANIFEST = "crates/sfi/kiln-abi/abi-manifest.json"
ABI_DIR = "native/core/abi"
LIMIT = 60_000                 # bytes: the size of a part of a split file (Kiln.Blocks: of each family, at most)
WHOLE_MAX = 100_000            # bytes: an assembly (but Kiln.Blocks) up to this stays one file
MEMBER_SENTENCE = 120          # characters of a member's summary
TYPE_SENTENCE = 170            # characters of a type's summary
ROW_SENTENCE = 110             # characters of a native row's comment
NBSP = " "

# assembly -> (KilnServices entry, purpose, area guides of the skill (references/), pages of the repository's docs/kiln).
# Every gated assembly needs a row (the generator stops on a missing one): a new service says here where it is explained.
OWN = "(Kiln's own)"
def _registries(root=Path(__file__).resolve().parents[2]):
    """Every registry Kiln ships, in ids.lock.json's order (Kiln.Registry's purpose names them all, not the first five)."""
    regs = json.loads((root / "schema" / "registry" / "ids.lock.json").read_text(encoding="utf-8"))["registries"]
    return sorted(regs, key=lambda n: regs[n]["id"])


SERVICES = {
    "Kiln.Api": ("(always)", "The mod base class and context, events, config, mods, capabilities, ids and refs, tasks. Always referenced",
                 ["api-contract.md", "api-events.md"], ["modding/contract.md", "modding/lifecycle.md", "modding/tasks.md"]),
    "Kiln.Abilities": ("Abilities", "Scripted abilities, per-cast spell tweaks, Impact natives, gems",
                       ["api-content.md"], ["services/abilities.md", "services/gems.md", "modding/impact.md"]),
    "Kiln.Assets": ("Assets", "Mod resources, overrides, icons and retextures", ["api-content.md"], ["services/assets.md"]),
    "Kiln.Audio": ("Audio", "Sounds, music, voice-over and narration", ["quests-talents-sounds.md"], ["services/audio.md", "modding/sounds.md"]),
    "Kiln.Blocks": ("Blocks", "Data blocks (items, recipes, props, NPCs, spells, gear rules), stations, bases, crafting, affixes, AI rule helpers",
                    ["blocks.md", "api-gear.md", "gameplay.md"], ["modding/blocks.md", "modding/item-blocks.md", "modding/world-blocks.md", "services/stations.md"]),
    "Kiln.Build": ("Build", "World edits the game saves and replicates: cells, shapes, schematics, block queries and breaking",
                   ["api-world.md"], ["services/build.md", "services/build-blocks.md"]),
    "Kiln.Camera": ("Camera", "Camera overrides as leases: stock camera modes the server holds, visual cameras on Kiln clients, projection",
                    [], ["services/camera.md"]),
    "Kiln.Chat": ("Chat", "Chat lines, commands and selectors", ["api-events.md"], ["services/chat.md"]),
    "Kiln.Client": ("Client", "The local player's side: BuildingInput, the game's crosshair hit, client ticks and posts (SteamGuard is experimental)",
                    ["api-network.md"], ["services/client-building-input.md", "services/client-crosshair.md", "services/steamguard.md"]),
    "Kiln.Combat": ("Combat", "Health, damage, healing, kills, god mode, combat events and attributes", ["api-world.md"], ["services/combat.md"]),
    "Kiln.Content": ("Content", "Registries and data packs: items, recipes, texts, gear stats, crafting groups",
                     ["api-content.md", "api-gear.md"], ["services/content.md"]),
    "Kiln.CreatureControl": ("CreatureControl", "Leases that take control of one creature", ["api-content.md"], ["services/creature-control.md"]),
    "Kiln.Creatures": ("Creatures", "One creature's values, variants, arsenals, villagers and the AI rule helpers Is and Act",
                       ["api-content.md", "api-contract.md"], ["services/creatures.md", "modding/npc-ai.md"]),
    "Kiln.DevTools": (OWN, "Kiln's developer windows and /devtools commands; a mod does not list it", [], ["services/types.md"]),
    "Kiln.Eml": ("Eml", "EML hash and data helpers: archives, values, startup", ["api-hooks.md"],
                 ["services/eml-archive.md", "services/eml-values.md", "services/eml-startup.md"]),
    "Kiln.Example": (OWN, "The service template; a mod does not list it", [], ["services/README.md"]),
    "Kiln.Game": ("Game", "Typed game data: Kiln.Ecs, a struct per game component and event, generated per game build",
                  ["api-world.md", "world-programs.md"], ["typegen.md", "services/types.md", "modding/ecs-programs.md"]),
    "Kiln.Gameplay": ("Gameplay", "Player and world interaction events, protection zones, offers", ["api-world.md", "gameplay.md"],
                      ["services/gameplay.md", "modding/offers.md"]),
    "Kiln.Hooks": ("Hooks", "Hooks (advanced tier): detours, symbols, memory, the game's original code", ["api-hooks.md"], ["services/hooks.md", "hooks.md"]),
    "Kiln.Items": ("Items", "Items in inventories: give, take, move, equip, gear state, rarity and upgrades", ["api-items.md", "api-gear.md"],
                   ["services/items.md", "modding/inventory.md"]),
    "Kiln.Markers": ("Markers", "Map markers, the compass strip and world labels by key; markers a server mod sends to Kiln clients",
                     [], ["services/markers.md"]),
    "Kiln.ModValues": ("ModValues", "Mods' own per-character values: counters, currencies, experience with levels", [],
                       ["services/mod-values.md", "modding/schemas/kiln-mod-values.md"]),
    "Kiln.Network": ("Network", "Network channels, Steam messages, config sync", ["api-network.md"], ["services/net-steam-messages.md"]),
    "Kiln.Permissions": ("Permissions", "Permission nodes and groups", ["api-events.md"], ["services/players.md"]),
    "Kiln.PlayerAnim": ("PlayerAnim", "A mod's states in the player's animation graph", ["api-content.md"], ["services/playeranim.md", "modding/player-states.md"]),
    "Kiln.Players": ("Players", "Who is on the server: players, states, characters, bans, progression", ["api-events.md"],
                     ["services/players.md", "services/player-state.md", "services/progression.md"]),
    "Kiln.Queries": ("Queries", "Entity and path queries, cancelable", ["gameplay.md", "api-world.md"], ["services/queries.md"]),
    "Kiln.Quests": ("Quests", "The quest engine, world progress, knowledge, dialogues, the journal", ["quests-talents-sounds.md"],
                    ["services/quests.md", "services/knowledge.md", "services/world-progress.md", "modding/quests.md"]),
    "Kiln.Registry": ("Registry", f"Kiln's registries ({', '.join(_registries())}) as typed entries over Kiln-owned field names",
                      [], ["modding/registries/README.md"]),
    "Kiln.Replication": (OWN, "Replication lab; a mod does not list it", [], []),
    "Kiln.Rules": ("Rules", "What players may do and what it costs: rules, NoClip, creative mode", ["api-events.md"], ["services/rules.md"]),
    "Kiln.Saves": ("Saves", "Save events and persistent mod data: world, player, entity, item instance, character", ["api-items.md"],
                   ["services/saves.md", "services/character-data.md"]),
    "Kiln.Settings": ("Settings", "The server's game settings, live", ["api-world.md"], ["services/settings.md"]),
    "Kiln.Sigs": ("Sigs", "The generated typed-hook classes; needs Hooks", ["api-hooks.md"], ["services/hooks.md", "hooks.md"]),
    "Kiln.Skills": ("Skills", "The game's skill tree for mods, talent trees, attributes", ["quests-talents-sounds.md"],
                    ["services/skills.md", "services/progression.md"]),
    "Kiln.Spawn": ("Spawn", "Create and destroy entities from templates: creatures, item drops, props", ["api-world.md"], ["services/spawn.md"]),
    "Kiln.Systems": ("Systems", "Wrapping the game's own ECS programs: typed system hooks", ["world-programs.md", "api-world.md"],
                     ["services/systems.md", "modding/ecs-programs.md"]),
    "Kiln.Terrain": ("Terrain", "Terrain reads and edits", ["api-world.md"], ["services/terrain.md"]),
    "Kiln.TestKit": ("(package Kiln.TestKit)", "Offline tests under dotnet test: fake server, fake world, fake services", ["testing.md"],
                     ["modding/unit-tests.md"]),
    "Kiln.Testing": ("Testing", "In-server tests: [KilnTest], Assert, [ClientCheck]", ["testing.md"], ["modding/testing.md"]),
    "Kiln.Time": ("Time", "Time of day and weather", ["api-world.md"], ["services/time.md"]),
    "Kiln.Tools": ("Tools", "Building tools: terrain tool actions, modes, tool materials", ["api-world.md"],
                   ["services/tools.md", "services/tool-actions.md", "services/tool-payment.md"]),
    "Kiln.Ui": ("Ui", "Menus, screens, HUD, key bindings, themes, item icons, tooltips, values in the game's UI", ["api-ui.md"],
                ["services/ui.md", "modding/ui-blocks.md"]),
    "Kiln.Vfx": ("Vfx", "The game's particles, lights and screen effects", ["vfx.md"], ["services/vfx.md", "services/screen.md", "modding/vfx.md"]),
    "Kiln.World": ("World", "Typed ECS reads, tracked writes, teleport, nearby queries, concurrent reads", ["api-world.md"], ["services/world.md"]),
}
# Kiln.Blocks: source folder -> family (file Blocks-<family>.md); a folder not named here is its own family
BLOCKS_FAMILIES = {"": "core", "Kinds": "core", "Bodies": "npc"}
CARRIER_RE = re.compile(r'const Kiln\.Mods\.ApiVersion = "[^"]*" -> string!')
MODS = ("static", "abstract", "virtual", "override", "sealed", "const", "readonly", "new", "extern", "unsafe", "required")
PRIM = {"bool": "System.Boolean", "byte": "System.Byte", "sbyte": "System.SByte", "char": "System.Char",
        "decimal": "System.Decimal", "double": "System.Double", "float": "System.Single", "int": "System.Int32",
        "uint": "System.UInt32", "long": "System.Int64", "ulong": "System.UInt64", "short": "System.Int16",
        "ushort": "System.UInt16", "object": "System.Object", "string": "System.String", "void": "System.Void",
        "nint": "System.IntPtr", "nuint": "System.UIntPtr", "dynamic": "System.Object"}
OPNAMES = {"op_Equality": "==", "op_Inequality": "!=", "op_Addition": "+", "op_Subtraction": "-", "op_Multiply": "*",
           "op_Division": "/", "op_LessThan": "<", "op_GreaterThan": ">", "op_LessThanOrEqual": "<=",
           "op_GreaterThanOrEqual": ">=", "op_UnaryNegation": "-", "op_BitwiseOr": "|", "op_BitwiseAnd": "&",
           "op_LogicalNot": "!", "op_True": "true", "op_False": "false", "op_Modulus": "%", "op_ExclusiveOr": "^",
           "op_OnesComplement": "~", "op_Increment": "++", "op_Decrement": "--", "op_UnaryPlus": "+"}
SYSTEM_NAMESPACES = ("System.Collections.Generic.", "System.Collections.Immutable.", "System.Collections.", "System.Threading.Tasks.",
                     "System.Threading.", "System.Numerics.", "System.IO.", "System.Text.", "System.Diagnostics.CodeAnalysis.",
                     "System.Diagnostics.", "System.Runtime.InteropServices.", "System.Runtime.CompilerServices.", "System.Linq.",
                     "System.Reflection.", "System.Buffers.", "System.")


class Problem(Exception):
    """Something the generator cannot work around: exit 2."""


# ---------------------------------------------------------------- C# names, PublicAPI lines and XML doc ids


def split_top(s, sep=","):
    """Split at top-level separators, honouring <>, (), [], {} and quotes."""
    out, depth, cur, quote, i = [], 0, [], None, 0
    while i < len(s):
        c = s[i]
        if quote:
            cur.append(c)
            if c == "\\" and i + 1 < len(s):
                cur.append(s[i + 1])
                i += 2
                continue
            if c == quote:
                quote = None
        elif c in "\"'":
            quote = c
            cur.append(c)
        elif c in "<([{":
            depth += 1
            cur.append(c)
        elif c in ">)]}":
            depth -= 1
            cur.append(c)
        elif c == sep and depth == 0:
            out.append("".join(cur))
            cur = []
        else:
            cur.append(c)
        i += 1
    if cur or out:
        out.append("".join(cur))
    return [x.strip() for x in out]


def split_dotted(name):
    """Split a dotted name at its top-level dots (not inside <> or [])."""
    parts, depth, cur = [], 0, []
    for c in name:
        if c in "<[(":
            depth += 1
        elif c in ">])":
            depth -= 1
        if c == "." and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(c)
    parts.append("".join(cur))
    return parts


def generic_to_arity(name):
    """Kiln.Foo<T>.Bar<A,B> -> Kiln.Foo`1.Bar`2."""
    out, i = [], 0
    while i < len(name):
        c = name[i]
        if c == "<":
            depth, j = 1, i + 1
            while depth and j < len(name):
                if name[j] == "<":
                    depth += 1
                elif name[j] == ">":
                    depth -= 1
                j += 1
            out.append("`%d" % len(split_top(name[i + 1:j - 1])))
            i = j
        else:
            out.append(c)
            i += 1
    return "".join(out)


def norm_type_api(t):
    """A PublicAPI type text in the form doc ids share: System names, <>, no nullability, T# for a type parameter."""
    t = t.strip()
    for _ in range(2):
        t = re.sub(r"^(params|this|ref readonly|ref|out|in|scoped)\s+", "", t)
    t = t.replace("!", "")
    if t.startswith("("):  # a tuple
        inner = t[1:t.rindex(")")]
        rest = t[t.rindex(")") + 1:]
        parts = [norm_type_api(re.match(r"^(.*?)(\s+[A-Za-z_]\w*)?$", p.strip()).group(1)) for p in split_top(inner)]
        return "System.ValueTuple<" + ",".join(parts) + ">" + rest.replace("?", "")
    arr = ""
    while t.endswith("?") or re.search(r"\[,*\]$", t):
        if t.endswith("?"):
            t = t[:-1]
        else:
            m = re.search(r"\[,*\]$", t)
            arr = m.group(0) + arr
            t = t[:m.start()].rstrip("?")
    if "<" in t:
        head, args = t[:t.index("<")], t[t.index("<") + 1:t.rindex(">")]
        base = head + "<" + ",".join(norm_type_api(a) for a in split_top(args)) + ">" + t[t.rindex(">") + 1:]
    else:
        base = PRIM.get(t, t)
        if "." not in base and not base.startswith("delegate*"):
            base = "T#"
    return base + arr


def norm_type_doc(t):
    t = t.strip().replace("@", "")
    t = re.sub(r"\[[0-9:,]*\]", lambda m: "[" + "," * m.group(0).count(",") + "]", t)
    if t.startswith("System.Nullable{"):
        t = t[len("System.Nullable{"):t.rindex("}")] + t[t.rindex("}") + 1:]
    head = t.split("[")[0]
    if re.fullmatch(r"`+\d+", head):
        return "T#" + t[len(head):]
    if "{" in t:
        h, args = t[:t.index("{")], t[t.index("{") + 1:t.rindex("}")]
        return h + "<" + ",".join(norm_type_doc(a) for a in split_top(args)) + ">" + t[t.rindex("}") + 1:]
    return t


class Entry:
    """One line of a PublicAPI record, understood. kind: type | ctor | method | prop | field | op | marker | forward | other."""

    def __init__(self, raw, kind):
        self.raw = raw
        self.kind = kind
        self.exp = None          # KILNX001 ...
        self.mods = []
        self.type = ""           # the containing type, with generic arity: Kiln.Foo`1 (a type line: its own name)
        self.type_text = ""      # the same as written: Kiln.Foo<T>
        self.name = ""           # the member's name as written (Foo<T> for a generic method)
        self.params = None       # normalized parameter types, None: no parameter list
        self.plist = ""          # the parameter list as written
        self.arity = 0
        self.accessor = None
        self.value = None
        self.ret = ""
        self.ext = None          # the receiver of a C# 14 extension member: Kiln.Act!
        self.state = "shipped"
        self.since = ""
        self.summary = ""
        self.key_name = None     # the name in a doc id, where it differs from `name` (operators, indexers)


OP_RE = re.compile(r"^(?P<type>.*?)\.(?P<kw>(?:implicit |explicit )?operator) ?(?P<sym>[^\s(]+)\((?P<params>.*)\)$")
INDEXER_RE = re.compile(r"^(?P<type>.*)\.this\[(?P<params>.*)\](?:\.(?P<acc>get|set|init))?$")
EXT_RE = re.compile(r"\.extension\(([^()]*)\)\.")
EXT_MARKER_RE = re.compile(r"\.extension\([^()]*\)$")
FORWARD_RE = re.compile(r" \(forwarded, contained in [\w.]+\)$")
EXP_RE = re.compile(r"^\[(KILNX\w*)\]")


def param_types(plist):
    out = []
    for p in split_top(plist):
        if not p:
            continue
        p = re.sub(r"\s*=\s*.*$", "", p).strip() if "=" in p else p
        mm = re.match(r"^(.*\S)\s+@?[A-Za-z_]\w*$", p)
        out.append(norm_type_api(mm.group(1) if mm else p))
    return out


def parse_api_line(raw):
    """A PublicAPI line -> Entry (kind marker/forward for the lines that are no member)."""
    s = raw.strip()
    if s.startswith("~"):
        s = s[1:]
    exp = None
    m = EXP_RE.match(s)
    if m:
        exp, s = m.group(1), s[m.end():]
    s = s.lstrip("~")
    if FORWARD_RE.search(s):
        e = Entry(raw, "forward")
        e.exp = exp
        return e
    mods = []
    while True:
        m = re.match(r"^(%s)\s+" % "|".join(MODS), s)
        if not m:
            break
        mods.append(m.group(1))
        s = s[m.end():]
    ext = None
    if ".extension(" in s:
        if "->" not in s and EXT_MARKER_RE.search(s):
            e = Entry(raw, "marker")
            e.exp = exp
            return e
        m = EXT_RE.search(s)
        if m:
            ext = m.group(1)
            s = s[:m.start()] + "." + s[m.end():]
    if "->" not in s:
        e = Entry(raw, "type")
        e.exp, e.mods = exp, mods
        e.type_text = s
        e.type = generic_to_arity(s)
        return e
    lhs, _, ret = s.rpartition(" -> ") if " -> " in s else (s.split("->")[0], "", s.split("->", 1)[1])
    lhs, ret = lhs.strip(), ret.strip()
    e = Entry(raw, "other")
    e.exp, e.mods, e.ext, e.ret = exp, mods, ext, ret
    if " = " in lhs and "(" not in lhs.split(" = ")[0]:
        lhs, e.value = lhs.split(" = ", 1)
    m = OP_RE.match(lhs)
    if m:
        e.kind, e.type_text, e.type = "op", m.group("type"), generic_to_arity(m.group("type"))
        sym = m.group("sym")
        e.name = "%s %s" % (m.group("kw"), sym)
        e.plist = m.group("params")
        e.params = param_types(e.plist)
        kw = m.group("kw")
        e.key_name = "op:operator " + sym if kw == "operator" else "op:%s operator %s" % (kw.split()[0], norm_type_api(sym))
        return e
    m = INDEXER_RE.match(lhs)
    if m:
        e.kind, e.type_text, e.type = "prop", m.group("type"), generic_to_arity(m.group("type"))
        e.name, e.plist, e.accessor = "this[%s]" % m.group("params"), m.group("params"), m.group("acc")
        e.params = param_types(m.group("params"))
        e.key_name = "Item"
        return e
    name_part, plist, depth = lhs, None, 0
    for i, c in enumerate(lhs):
        if c == "<":
            depth += 1
        elif c == ">":
            depth -= 1
        elif c == "(" and depth == 0:
            name_part, plist = lhs[:i], lhs[i + 1:lhs.rindex(")")]
            break
    if plist is None:
        m = re.match(r"^(.*)\.(get|set|init|add|remove)$", name_part)
        if m:
            name_part, e.accessor = m.group(1), m.group(2)
    parts = split_dotted(name_part)
    member, tparts = parts[-1], parts[:-1]
    e.type_text = ".".join(tparts)
    e.type = generic_to_arity(e.type_text)
    e.name = member
    if plist is not None:
        e.kind = "method"
        e.plist = plist
        e.params = param_types(plist)
        g = re.match(r"^(.*?)<(.*)>$", member)
        if g:
            e.arity = len(split_top(g.group(2)))
        simple = re.sub(r"<.*>$", "", tparts[-1]) if tparts else ""
        if re.sub(r"<.*>$", "", member) == simple:
            e.kind = "ctor"
    else:
        e.kind = "prop" if e.accessor in ("get", "set", "init") else "field"
    if ext is not None and e.params is not None:
        e.params = [norm_type_api(ext)] + e.params
    return e


def entry_key(e):
    """The doc id key of an entry: (kind letter, type, name, 'arity|param,types')."""
    if e.kind == "type":
        return ("T", e.type, "", "")
    ps = "" if e.params is None else ",".join(e.params)
    if e.kind == "ctor":
        name = "#ctor"
    elif e.kind == "method":
        name = re.sub(r"<.*>$", "", e.name)
    else:
        name = e.key_name or e.name
    letter = "M" if e.kind in ("method", "ctor", "op") else "P" if e.kind == "prop" else "F"
    return (letter, e.type, name, "%d|%s" % (e.arity, ps))


def parse_doc_id(did):
    """'M:Kiln.Build.Dig(Kiln.BuildShape)' -> the key of entry_key."""
    k, rest = did.split(":", 1)
    ret = None
    if ")~" in rest:                                   # a conversion operator: ...op_Implicit(Kiln.Foo)~Kiln.Bar
        rest, ret = rest.rsplit(")~", 1)
        rest += ")"
    params = None
    if "(" in rest:
        rest, p = rest[:rest.index("(")], rest[rest.index("(") + 1:rest.rindex(")")]
        params = [norm_type_doc(x) for x in split_top(p, ",")] if p else []
    if k == "T":
        return ("T", rest, "", "")
    parts = rest.split(".")
    member, tname = parts[-1], ".".join(parts[:-1])
    arity = 0
    m = re.match(r"^(.*)``(\d+)$", member)
    if m:
        member, arity = m.group(1), int(m.group(2))
    if member.startswith("op_"):
        if member in ("op_Implicit", "op_Explicit"):
            member = "op:%s operator %s" % ("implicit" if member == "op_Implicit" else "explicit", norm_type_doc(ret or ""))
        else:
            member = "op:operator " + OPNAMES.get(member, member)
    return ({"M": "M", "P": "P", "F": "F", "E": "F"}[k], tname, member, "%d|%s" % (arity, "" if params is None else ",".join(params)))


# ---------------------------------------------------------------- text: XML summaries to one Markdown-safe line


def short_name(cref):
    """A cref (M:Kiln.Build.PlaceOnce(System.String)) -> Build.PlaceOnce; a type: its simple name."""
    kind, _, rest = cref.partition(":")
    rest = rest.split("(")[0].replace("#ctor", "").rstrip(".")
    parts = [re.sub(r"`+\d+$", "", p) for p in rest.split(".") if p]
    parts = [p for p in parts if p]
    if not parts:
        return cref
    return parts[-1] if kind == "T" or len(parts) == 1 else ".".join(parts[-2:])


def render(node):
    """The inline text of an XML doc element: <see cref> as its short name in backticks, <c> as code, whitespace collapsed,
    < and > escaped. Returned as one line."""
    out = []

    def walk(n):
        tag = n.tag
        if tag in ("see", "seealso"):
            if n.get("cref"):
                out.append("`" + short_name(n.get("cref")) + "`")
            elif n.get("langword"):
                out.append("`" + n.get("langword") + "`")
            elif n.get("href") and not "".join(n.itertext()).strip():
                out.append(n.get("href"))
            else:
                out.append(esc("".join(n.itertext())))
        elif tag in ("paramref", "typeparamref"):
            out.append("`" + (n.get("name") or "") + "`")
        elif tag in ("c", "code"):
            out.append("`" + " ".join("".join(n.itertext()).split()).replace("`", "'") + "`")
        else:
            if n.text:
                out.append(esc(n.text))
            for ch in n:
                walk(ch)
                if ch.tail:
                    out.append(esc(ch.tail))
            return
        # the element's own tail is added by the parent's loop
    if node.text:
        out.append(esc(node.text))
    for ch in node:
        walk(ch)
        if ch.tail:
            out.append(esc(ch.tail))
    text = " ".join(ascii_text("".join(out).replace(NBSP, " ")).split())
    text = re.sub(r"[\x00-\x08\x0b-\x1f\x7f]", "", text)
    text = text.replace("` `", " ")
    return text.replace("``", "")


ASCII = {"\u2026": "...", "\u2192": "->", "\u2190": "<-", "\u2212": "-", "\u2013": "-", "\u2014": " - ", "\u2018": "'", "\u2019": "'",
         "\u201c": '"', "\u201d": '"', "\u00b1": "+/-", "\u00b0": " deg", "\u00a7": "section ", "\u00d7": "x", "\u2265": ">=",
         "\u2264": "<=", "\u200b": "", "\u00b7": ".", "\u2022": "-", "\u2248": "~", "\u00bd": "1/2", "\u03c0": "pi"}


def ascii_text(text):
    """The text in plain ASCII: the usual typographic marks spelled out, any other letter without its accent."""
    text = "".join(ASCII.get(c, c) for c in text)
    return unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode("ascii")


def esc(text):
    """Prose text for Markdown: no raw tag (a `<` is escaped), no stray backtick, plain ASCII."""
    return ascii_text(text).replace("`", "'").replace("<", "\\<")


SENT_END = re.compile(r"(?<!\be\.g)(?<!\bi\.e)(?<!\betc)(?<!\bvs)(?<!\bcf)[.!?](?=\s+[A-Z0-9`\"(\[]|$)")


def first_sentence(text, cap):
    text = text.strip()
    s = text
    for m in SENT_END.finditer(text):
        if text[:m.end()].count("`") % 2 == 0:             # not a full stop inside a code span
            s = text[:m.end()]
            break
    if len(s) > cap:
        s = s[:cap - 3]
        if " " in s:
            s = s.rsplit(" ", 1)[0]
        if s.count("`") % 2:
            s = s[:s.rfind("`")].rstrip()
        s = s.rstrip("\\ ,;:") + "..."
    return s


def short(sig):
    """A signature without the noise: Kiln. and System. namespaces, the not-null `!`."""
    for ns in SYSTEM_NAMESPACES:
        sig = sig.replace(ns, "")
    sig = re.sub(r"\bKiln\.(?=[A-Z])", "", sig)
    sig = re.sub(r"(?<=[\w>\]\)])!(?!=)", "", sig)
    return ascii_text(sig).replace("`", "'")


# ---------------------------------------------------------------- XML docs


class Doc:
    def __init__(self, did, summary, inherit, params):
        self.id, self.summary, self.inherit, self.params = did, summary, inherit, params


class Docs:
    """The XML doc file of one assembly: summaries by doc id, by entry key, by (letter, type, name)."""

    def __init__(self, path):
        self.by_id, self.by_key = {}, {}
        self.by_name = collections.defaultdict(list)
        self.ctors = collections.defaultdict(list)     # type -> the docs of its constructors
        if path is None:                       # a project that writes no XML docs (no GenerateDocumentationFile)
            return
        root = ET.parse(path).getroot()
        for m in root.iter("member"):
            did = m.get("name")
            if not did or did.startswith("N:"):
                continue
            summ = m.find("summary")
            inh = m.find("inheritdoc")
            d = Doc(did, render(summ) if summ is not None else "", inh.get("cref") if inh is not None else None,
                    {p.get("name"): render(p) for p in m.findall("param")})
            self.by_id[did] = d
            try:
                key = parse_doc_id(did)
            except (ValueError, KeyError):
                continue
            self.by_key.setdefault(key, d)
            self.by_name[key[:3]].append(d)
            if key[0] == "M" and key[2] == "#ctor":
                self.ctors[key[1]].append(d)

    def param_doc(self, type_key, name):
        """The <param> text of a positional record's property: on the record's own doc, or its constructor's."""
        for d in [self.by_id.get("T:" + type_key)] + self.ctors.get(type_key, []):
            if d is not None and d.params.get(name):
                return d.params[name]
        return ""

    def text(self, d, depth=0):
        """A doc's summary, through inheritdoc cref (the extension members' docs live on the compiler's block type)."""
        if d is None:
            return ""
        if d.summary:
            return d.summary
        if d.inherit and depth < 4:
            return self.text(self.by_id.get(d.inherit), depth + 1)
        return ""

    def find(self, e):
        """The Doc of an entry: the exact key, else (extension members, events) the variants, else the only overload."""
        key = entry_key(e)
        d = self.by_key.get(key)
        if d is None and e.ext is not None:
            letter, tname, name, ap = key
            n = int(ap.split("|", 1)[0])
            ps = ap.split("|", 1)[1]
            recv = norm_type_api(e.ext)
            bare = ",".join(p for p in ps.split(",") if p) if ps else ""
            if ps.startswith(recv):
                bare = ps[len(recv):].lstrip(",")
            for k in ((letter, tname, name, "%d|%s" % (n, bare)), ("M", tname, "get_" + name, "0|"), ("P", tname, name, "0|")):
                d = self.by_key.get(k)
                if d is not None:
                    break
        if d is None and e.kind == "field":
            d = self.by_key.get(("F",) + key[1:])
        if d is None:
            cands = self.by_name.get(key[:3], [])
            if e.kind in ("method", "ctor", "prop") and cands:
                want = key[3].split("|", 1)[1].count(",") + 1 if key[3].split("|", 1)[1] else 0
                same = [c for c in cands if _doc_param_count(c.id) == want]
                if len(same) == 1:
                    d = same[0]
                elif not same and len(cands) == 1:
                    d = cands[0]
        return d


def _doc_param_count(did):
    if "(" not in did:
        return 0
    inner = did[did.index("(") + 1:did.rindex(")")]
    return len(split_top(inner)) if inner else 0


def find_xml(xml_dir, asm):
    for rel in (f"{asm}.xml", f"{asm}/release/{asm}.xml", f"bin/{asm}/release/{asm}.xml", f"{asm}/Release/{asm}.xml"):
        p = Path(xml_dir) / rel
        if p.is_file():
            return p
    return None


# ---------------------------------------------------------------- the tree


def read_lines(path):
    if not path.is_file():
        return []
    return [l.rstrip("\r") for l in path.read_text(encoding="utf-8-sig").split("\n") if l.strip() and l.rstrip("\r") != "#nullable enable"]


class Tree:
    def __init__(self, root):
        self.root = Path(root)

    def projects(self):
        """[(assembly, folder)] of every managed project that has a PublicAPI.Shipped.txt, sorted by assembly."""
        found = {}
        for p in (self.root / "managed").rglob("PublicAPI.Shipped.txt"):
            if "bin" in p.parts or "obj" in p.parts:
                continue
            if p.parent.name in found:
                raise Problem(f"two gated projects are called {p.parent.name}: {found[p.parent.name]} and {p.parent}")
            found[p.parent.name] = p.parent
        return sorted(found.items())

    def record(self, folder):
        """([(line, state)]): Shipped, plus the additions of Unshipped, minus its *REMOVED* lines."""
        have = {l: "shipped" for l in read_lines(folder / "PublicAPI.Shipped.txt")}
        for l in read_lines(folder / "PublicAPI.Unshipped.txt"):
            if l.startswith("*REMOVED*"):
                have.pop(l[len("*REMOVED*"):], None)
            else:
                have.setdefault(l, "unshipped")
        return sorted(have.items())

    def api_version(self):
        m = re.search(r'ApiVersion = "([^"]+)"', (self.root / MODS_CS).read_text(encoding="utf-8"))
        if not m:
            raise Problem(f"no ApiVersion in {MODS_CS}")
        return m.group(1)

    def ledger(self):
        """({(project, entry): mod_api} of the managed rows, {entry: mod_api} of the native rows)."""
        managed, native = {}, {}
        p = self.root / LEDGER
        if not p.is_file():
            raise Problem(f"no {LEDGER}: the since ledger (tools/release/api-gate.sh history --seed)")
        for n, l in enumerate(p.read_text(encoding="utf-8").split("\n")):
            if n == 0 or not l:
                continue
            c = l.split("\t")
            if len(c) != 5:
                raise Problem(f"{LEDGER} line {n + 1}: five tab-separated cells expected")
            if c[0] == "managed":
                managed[(c[1], c[2])] = c[4]
            else:
                native[c[2]] = c[4]
        return managed, native


# ---------------------------------------------------------------- the model of one assembly


class TypeInfo:
    def __init__(self, key, text):
        self.key, self.text = key, text
        self.entry = None          # the type's own line
        self.members = []          # Entry, in the record's order
        self.since = ""
        self.summary = ""
        self.lines = []            # rendered member lines
        self.enum = None           # [(name, mark)]
        self.ext_receivers = []

    @property
    def name(self):
        return short(re.sub(r"^Kiln\.(?=[A-Z])", "", self.text))

    @property
    def shown(self):
        """The name for a heading or a map: a generic name in code, so no Markdown reads `<T>` as a tag."""
        return "`%s`" % self.name if "<" in self.name else self.name

    @property
    def empty(self):
        return self.entry is None and not self.members and not self.lines and self.enum is None


def build_model(asm, folder, tree, docs, ledger, upcoming, experimental):
    """-> {type key: TypeInfo} of one assembly, filtered, with its summaries and since marks."""
    types = collections.OrderedDict()
    entries = []
    for line, state in tree.record(folder):
        e = parse_api_line(line)
        if e.kind in ("marker", "forward"):
            continue
        if e.exp:
            experimental[(e.exp, asm)] += 1
            continue
        e.state = state
        if state == "unshipped":
            e.since = upcoming
        else:
            e.since = ledger.get((asm, line), "")
            if not e.since and not CARRIER_RE.fullmatch(line):
                raise Problem(f"{LEDGER} has no row for {asm}: {line[:120]} (tools/release/api-gate.sh history --check)")
        entries.append(e)
    for e in entries:
        if e.kind == "type":
            types.setdefault(e.type, TypeInfo(e.type, e.type_text)).entry = e
    for e in entries:
        if e.kind != "type":
            types.setdefault(e.type, TypeInfo(e.type, e.type_text)).members.append(e)
    for t in types.values():
        _filter(t)
        if t.entry is not None:
            t.since = t.entry.since
            t.summary = docs.text(docs.find(t.entry))
        _members(t, docs)
    return types


def _param_names(plist):
    return tuple(re.sub(r"^.*\s(@?\w+)(\s*=.*)?$", r"\1", p) for p in split_top(plist or "") if p)


def _filter(t):
    """Leave out what the compiler made and what every object has: record plumbing, object overrides, delegate plumbing, and the
    implementation methods of C# 14 extension members (static AiRulesApi.Flee next to Act.Flee)."""
    ms = t.members
    ctor_names = {_param_names(m.plist) for m in ms if m.kind == "ctor" and m.plist}
    recordish = any(m.name == "<Clone>$" for m in ms) or any(
        m.kind == "method" and m.name == "Deconstruct" and _param_names(m.plist) in ctor_names for m in ms)
    ext_names = {m.name for m in ms if m.ext is not None}
    keep = []
    for m in ms:
        n = re.sub(r"<.*>$", "", m.name)
        if m.kind == "method" and n in ("<Clone>$", "PrintMembers", "BeginInvoke", "EndInvoke"):
            continue
        if m.kind == "prop" and n == "EqualityContract":
            continue
        if m.kind == "method" and "override" in m.mods and n in ("Equals", "GetHashCode", "ToString"):
            continue
        if recordish:
            if m.kind == "method" and n in ("Deconstruct", "GetHashCode", "ToString"):
                continue
            if m.kind == "method" and n == "Equals" and len(m.params or []) == 1:
                continue
            if m.kind == "op" and m.name in ("operator ==", "operator !="):
                continue
        if ext_names and m.ext is None and m.kind == "method" and "static" in m.mods and (
                n in ext_names or (n.startswith("get_") and n[4:] in ext_names)):
            continue
        keep.append(m)
    t.members = keep


def _members(t, docs):
    """Summaries, accessor collapse, enum detection and the rendered lines of a type."""
    type_since = t.since
    enum_like = bool(t.members) and all(
        m.kind == "field" and m.value is not None and not m.mods and short(m.ret) == short(t.text) for m in t.members)
    if enum_like:
        vals = []
        for m in t.members:
            try:
                v = int(m.value, 0)
            except ValueError:
                v = 1 << 62
            vals.append((v, m.name, mark(m.since, type_since)))
        t.enum = [(n, mk) for _, n, mk in sorted(vals)]
        return
    props = collections.OrderedDict()
    rows = []
    for m in t.members:
        s = docs.text(docs.find(m))
        if not s and m.kind == "prop" and not m.params:
            s = docs.param_doc(t.key, m.name)              # a positional record's property: the <param> of the record
        if m.kind == "ctor" and not m.params and not s:
            continue                                        # the implicit parameterless constructor
        if m.kind == "ctor" and s and s == t.summary:
            s = ""                                          # a record's constructor repeats the type's summary
        m.summary = s
        if m.kind == "prop":
            props.setdefault((m.name, m.ext or ""), []).append(m)
        else:
            rows.append(m)
    lines = []                                              # (sort key, signature, since, summary, entry)
    for ms in props.values():
        m = ms[0]
        accs = [x.accessor for x in ms if x.accessor]
        order = [a for a in ("get", "set", "init") if a in accs] + [a for a in accs if a not in ("get", "set", "init")]
        typ = next((x.ret for x in ms if x.accessor == "get"), "")
        name = short(m.ext) + "." + m.name if m.ext else m.name
        text = "%s%s { %s }%s" % ("".join(x + " " for x in m.mods if x != "static" or not m.ext), name,
                                  " ".join(a + ";" for a in order), " -> " + typ if typ else "")
        lines.append(((m.name.lower(), m.name, ""), text, min((x.since for x in ms), key=since_key),
                      next((x.summary for x in ms if x.summary), ""), m))
    for m in rows:
        lines.append((("" if m.kind == "ctor" else m.name.lower(), m.name, m.plist), signature(m), m.since, m.summary, m))
    lines.sort(key=lambda x: (x[4].kind != "ctor", x[0], x[4].raw))
    for _, text, since, summ, m in lines:
        if m.ext is not None and short(m.ext) not in t.ext_receivers:
            t.ext_receivers.append(short(m.ext))
        s = first_sentence(summ, MEMBER_SENTENCE) if summ else ""
        mk = mark(since, type_since)
        t.lines.append("- `%s`%s%s" % (short(text), " `%s`" % mk if mk else "", " — " + s if s else ""))
    t.ext_receivers.sort()


def signature(m):
    mods = [x for x in m.mods if x not in ("new", "const") and not (x == "static" and m.ext is not None)
            and not (x == "virtual" and m.name == "Invoke")]                    # a delegate's Invoke is its signature
    pre = "".join(x + " " for x in mods)
    name = short(m.ext) + "." + m.name if m.ext is not None else m.name
    if m.kind == "ctor":
        return "%s(%s)" % (name, m.plist)
    if m.kind == "op":
        return "%s%s(%s) -> %s" % (pre, m.name, m.plist, m.ret)
    if m.kind == "method":
        return "%s%s(%s) -> %s" % (pre, name, m.plist, m.ret)
    if "const" in m.mods:
        return "const %s = %s -> %s" % (name, m.value, m.ret)
    return "%s%s%s -> %s" % (pre, name, " = " + m.value if m.value is not None else "", m.ret)


def since_key(v):
    return (0 if v.startswith("<=") else 1, [int(x) for x in re.findall(r"\d+", v)])


def mark(since, type_since):
    """The mod API to show on a line: nothing when it is the type's own, nothing for the oldest record (<=0.2.1) of a type that is
    old too."""
    if since == type_since or not since or (since.startswith("<=") and (not type_since or type_since.startswith("<="))):
        return ""
    return since


# ---------------------------------------------------------------- files


def type_block(t):
    """The Markdown of one type: its heading, an enum's values on one line, then one line per member."""
    head = "## %s%s" % (t.shown, "" if t.since.startswith("<=") or not t.since else " `%s`" % t.since)
    if t.summary:
        head += " — " + first_sentence(t.summary, TYPE_SENTENCE)
    out = [head]
    if t.ext_receivers:
        out.append("Extension members (C# 14): call them on the receiver (%s), not on `%s`." % (
            ", ".join("`%s.X`" % r for r in t.ext_receivers), t.name))
    if t.enum is not None:
        out.append("enum: " + ", ".join(n + (" `%s`" % mk if mk else "") for n, mk in t.enum))
    out.extend(t.lines)
    out.append("")
    return "\n".join(out) + "\n"


def stem(asm):
    return asm[len("Kiln."):] if asm.startswith("Kiln.") else asm


def nbytes(text):
    return len(text.encode("utf-8"))


def split_balanced(items, size_of, limit):
    """Items in order, cut into the fewest parts of at most about `limit` bytes, of about equal size; an item is never cut."""
    total = sum(size_of(i) for i in items)
    n = max(1, -(-total // limit))
    parts = [[] for _ in range(n)]
    done = 0
    for it in items:
        sz = size_of(it)
        parts[min(n - 1, int((done + sz / 2) * n / total)) if total else 0].append(it)
        done += sz
    return [p for p in parts if p]


def blocks_family_map(folder):
    """{type simple name: family} of Kiln.Blocks: the folder its source file is in (the project's own files: core)."""
    decl = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|ref|file|new)\s+)*"
                      r"(?:(?:class|struct|enum|interface)\s+|record\s+(?:(?:class|struct)\s+)?|delegate\s+[\w<>\[\],.?\s]+?\s+)(\w+)", re.M)
    out = {}
    for p in sorted(folder.rglob("*.cs")):
        rel = p.relative_to(folder)
        if rel.parts[0] in ("bin", "obj"):
            continue
        top = rel.parts[0] if len(rel.parts) > 1 else ""
        fam = BLOCKS_FAMILIES.get(top, top.lower() or "core")
        for m in decl.finditer(p.read_text(encoding="utf-8", errors="replace")):
            out.setdefault(m.group(1), fam)
    return out


def part_files(sname, what, groups, intro):
    """{file: text} of a split: one file per part (names: groups' keys) and sname.md, the map of them."""
    files = {}
    mp = ["# %s: map" % what, "", intro, ""]
    for name, title, body, listing in groups:
        files[name + ".md"] = "# %s\n\n%s" % (title, body)
        mp.append("- `%s.md` (%s): %s" % (name, kb(nbytes(files[name + ".md"])), listing))
    files[sname + ".md"] = "\n".join(mp) + "\n"
    return files


def assembly_files(asm, folder, types):
    """{file name: text} of one assembly: one file, or <Entry>.md as the map of its parts."""
    ordered = sorted((t for t in types.values() if not t.empty), key=lambda t: (t.name.lower(), t.name))
    sname = stem(asm)
    blocks = {t.key: type_block(t) for t in ordered}
    size = lambda t: nbytes(blocks[t.key])
    total = sum(size(t) for t in ordered)
    title = "%s (%s)" % (sname, asm)
    if total <= WHOLE_MAX and not (asm == "Kiln.Blocks" and total > LIMIT):
        return {sname + ".md": "# %s\n\n%s" % (title, "".join(blocks[t.key] for t in ordered))}
    groups = []                                                  # (file stem, title, body, listing)
    if asm == "Kiln.Blocks":
        fam = blocks_family_map(folder)
        by_family = collections.defaultdict(list)
        for t in ordered:
            tops = [p.split("`")[0] for p in t.key.split(".")[1:]]
            by_family[next((fam[p] for p in tops if p in fam), "core")].append(t)
        for f in sorted(by_family):
            parts = split_balanced(by_family[f], size, LIMIT)
            for i, ts in enumerate(parts):
                suffix = "" if len(parts) == 1 else "-%d" % (i + 1)
                groups.append(("%s-%s%s" % (sname, f, suffix), "%s: %s%s" % (title, f, "" if not suffix else ", part %d of %d" % (i + 1, len(parts))),
                               "".join(blocks[t.key] for t in ts), ", ".join(t.shown for t in ts)))
        how = "by family (the folders of the project's sources)"
    else:
        parts = split_balanced(ordered, size, LIMIT)
        for i, ts in enumerate(parts):
            groups.append(("%s-%d" % (sname, i + 1), "%s: part %d of %d" % (title, i + 1, len(parts)),
                           "".join(blocks[t.key] for t in ts), ", ".join(t.shown for t in ts)))
        how = "by type name"
    return part_files(sname, title, groups,
                      "Too large for one file: the types are split %s. Grep a type or member name to find its file; "
                      "each part starts with its own title." % how)


# ---------------------------------------------------------------- the native ABI


TABLE_RE = re.compile(r"typedef\s+struct\s+(Kiln\w*Api(?:V\d+)?)\s*\{(.*?)\}\s*\1\s*;", re.S)
ROW_RE = re.compile(r"KILN_CALL\s*\*\s*(\w+)\s*\)")
SERVICE_RE = re.compile(r'#define\s+KILN_\w+_SERVICE\s+"([^"]+)"')
SECTION_RE = re.compile(r"^-{2,}[^-]*-{2,}")


def clean_comment(lines):
    text = " ".join(lines)
    text = re.sub(r"/\*+|\*+/", " ", text)
    text = re.sub(r"(^|\s)(//+|\*+)(?=\s|$)", " ", text)
    text = " ".join(text.split())
    return SECTION_RE.sub("", text).strip()


def table_rows(body):
    """[(row, comment)] of a table struct's body: the comment directly above the row (no blank line between), else one after
    its `;` on its last line. A dashed section label is no comment."""
    lines = body.split("\n")
    out, pending, in_block, i = [], [], False, 0
    while i < len(lines):
        raw = lines[i]
        s = raw.strip()
        if in_block:
            pending.append(s)
            in_block = "*/" not in s
            i += 1
        elif s.startswith("/*"):
            pending.append(s)
            in_block = "*/" not in s
            i += 1
        elif s.startswith("//"):
            pending.append(s)
            i += 1
        elif not s:
            pending = []
            i += 1
        else:
            m = ROW_RE.search(raw)
            if m:
                last = i
                while ";" not in lines[last] and last + 1 < len(lines):
                    last += 1
                comment = clean_comment(pending) if pending else ""
                tail = lines[last][lines[last].rfind(";") + 1:] if ";" in lines[last] else ""
                tm = re.search(r"/\*.*?\*/|//.*$", tail)
                if tm and not comment:
                    comment = clean_comment([tm.group(0)])
                out.append((m.group(1), comment))
                i = last
            i += 1
            pending = []
    return out


def native_files(tree, ledger_native, upcoming):
    """-> ({file: text}, tables, rows): native.md, or native.md as the map of native-<n>.md."""
    root = tree.root
    manifest = {}
    if (root / MANIFEST).is_file():
        manifest = json.loads((root / MANIFEST).read_text(encoding="utf-8")).get("headers", {})
    headers = [root / ABI_DIR / "kiln_api.h"] + sorted((root / ABI_DIR / "services").glob("*.h"))
    sections, rows, tables = [], 0, 0                                   # (header, text of its section)
    for h in headers:
        if not h.is_file():
            continue
        rel = str(h.relative_to(root / ABI_DIR))
        text = h.read_text(encoding="utf-8", errors="replace")
        found = list(TABLE_RE.finditer(text))
        if not found:
            continue
        ids = SERVICE_RE.findall(text)
        out = ["## %s%s" % (rel, " — service %s" % ", ".join("`%s`" % i for i in ids) if ids else "")]
        layouts = manifest.get("%s/%s" % (ABI_DIR, rel), {}).get("layouts", {})
        for m in found:
            table = m.group(1)
            lay = layouts.get(table, {})
            out.append("### %s%s" % (table, " — %d bytes" % lay["size"] if "size" in lay else ""))
            tables += 1
            for row, comment in table_rows(m.group(2)):
                since = ledger_native.get("%s:%s:%s" % (rel, table, row), upcoming)
                off = lay.get("fields", {}).get(row)
                c = first_sentence(comment, ROW_SENTENCE) if comment else ""
                out.append("- %s `%s.%s`%s%s%s" % (h.name, table, row, " @%d" % off if off is not None else "",
                                                   "" if since.startswith("<=") else " `%s`" % since, " — " + esc(c) if c else ""))
                rows += 1
        out.append("")
        sections.append((rel, "\n".join(out) + "\n"))
    head = ("The function tables of native/core/abi/kiln_api.h and native/core/abi/services/*.h, one line per row: header, "
            "`Table.row`, @byte offset in the table (crates/sfi/kiln-abi/abi-manifest.json), the mod API that added it (no mark: "
            "0.2.0 or older), the comment directly above the row or after it (no comment: the header says no more). "
            "Pages: docs/kiln/services/NAME.md. Generated: do not edit.")
    title = "Native ABI rows (mod API %s)" % upcoming
    total = sum(nbytes(s) for _, s in sections)
    if total <= WHOLE_MAX:
        return {"native.md": "# %s\n\n%s\n\n%s" % (title, head, "".join(s for _, s in sections))}, tables, rows
    groups = []
    parts = split_balanced(sections, lambda x: nbytes(x[1]), LIMIT)
    for i, ps in enumerate(parts):
        groups.append(("native-%d" % (i + 1), "%s: part %d of %d" % (title, i + 1, len(parts)),
                       "One line per row: header, `Table.row`, @offset, mod API, comment (native.md says more).\n\n" +
                       "".join(s for _, s in ps), ", ".join(r for r, _ in ps)))
    return part_files("native", title, groups, head + " Too large for one file: the headers are split; grep a row name."), tables, rows


# ---------------------------------------------------------------- everything


def kb(n):
    return "%d KB" % -(-n // 1024)


def generate(root, xml_dir, services=None):
    """-> {file name: text} of the whole index. services: the SERVICES table (tests give their own)."""
    services = SERVICES if services is None else services
    tree = Tree(root)
    upcoming = tree.api_version()
    ledger_managed, ledger_native = tree.ledger()
    projects = tree.projects()
    names = {a for a, _ in projects}
    for asm in sorted(names - set(services)):
        raise Problem(f"{asm} has a PublicAPI record but no row in SERVICES (tools/docs-site/api_index.py): add its KilnServices "
                      f"entry, purpose and guide pages")
    for asm in sorted(set(services) - names):
        raise Problem(f"SERVICES names {asm}, which has no PublicAPI record: remove its row")
    files, info = {}, []
    experimental = collections.Counter()
    for asm, folder in projects:
        p = find_xml(xml_dir, asm)
        if p is None and any("GenerateDocumentationFile" in c.read_text(encoding="utf-8") for c in folder.glob("*.csproj")):
            raise Problem(f"no XML doc file of {asm} under {xml_dir} (looked for {asm}.xml, {asm}/release/{asm}.xml)")
        types = build_model(asm, folder, tree, Docs(p), ledger_managed, upcoming, experimental)
        nt = sum(1 for t in types.values() if t.entry is not None)
        nm = sum(len(t.lines) + (len(t.enum) if t.enum else 0) for t in types.values())
        fs = assembly_files(asm, folder, types) if any(not t.empty for t in types.values()) else {}
        files.update(fs)
        info.append((asm, nt, nm, sorted(fs)))
    nfiles, ntab, nrows = native_files(tree, ledger_native, upcoming)
    files.update(nfiles)
    files["INDEX.md"] = index_text(upcoming, info, experimental, ntab, nrows, files, sorted(nfiles), services)
    return {name: text.rstrip("\n") + "\n" for name, text in files.items()}


def index_text(upcoming, info, experimental, ntab, nrows, files, native, services):
    out = ["# Kiln mod API index (mod API %s)" % upcoming, "",
           "Every public type and member of the mod API, one line each, to grep: a name (`BlockAt`), a word of what it does "
           "(`refusal`), a mod API (`0.2.15` finds what that release added). Then open that assembly's file: its name is the "
           "`KilnServices` entry (`Items.md` needs `<KilnServices>Items</KilnServices>`; `Api.md` needs none). A file that was too "
           "large is a map (`Blocks.md`) of its parts (`Blocks-items.md`).", "",
           "- Type: `## Name `mod API` — first sentence of its XML doc`. Member: `- `signature` `mod API` — first sentence`. "
           "A member shows a mod API only when it differs from its type's; no mark at all: mod API 0.2.1 or older. "
           "`enum:` lists a whole enum's values. A property is one line (`{ get; init; }`).",
           "- Names are short: `Kiln.` and `System.` left out, `!` (not null) too. Extension members (`Is.InCombat`, `Act.Flee`) are "
           "listed under the static class that declares them, written on their receiver.",
           "- Left out: experimental API (`[Experimental(\"KILNX0nn\")]`: lab and test members, API not proven in a client yet), "
           "compiler-made record members, overrides of object's members, a parameterless constructor without a doc. "
           "Not here: the game's own types (`Kiln.Ecs`, generated: docs/kiln/typegen.md).",
           "- What each mod API number is: references/versions.md. Prose, rules and examples: the guides of the table. "
           "`docs/kiln/` pages are in the repository.", "",
           "| Assembly | `KilnServices` | Purpose | Skill guide | `docs/kiln/` page | Types | Members | File |",
           "|---|---|---|---|---|---:|---:|---|"]
    for asm, nt, nm, fs in info:
        entry, purpose, guides, pages = services[asm]
        if fs:
            main = stem(asm) + ".md"
            fl = "[%s](%s) (%s)" % (main, main, "map of %d parts" % (len(fs) - 1) if len(fs) > 1 else kb(nbytes(files[main])))
        else:
            fl = "no public API (lab members only)"
        out.append("| %s | %s | %s | %s | %s | %d | %d | %s |" % (
            asm, entry if entry.startswith("(") else "`%s`" % entry, purpose, ", ".join("[%s](../%s)" % (g, g) for g in guides) or "-",
            ", ".join("`%s`" % p for p in pages) or "-", nt, nm, fl))
    out.append("| Native ABI | C plugins | The function tables of native/core/abi: %d tables, %d rows | - | `services/README.md` | %d | %d | "
               "[native.md](native.md) (%s) |" % (ntab, nrows, ntab, nrows,
                                                  "map of %d parts" % (len(native) - 1) if len(native) > 1 else kb(nbytes(files["native.md"]))))
    out.append("")
    if experimental:
        out.append("Left out as experimental: " + "; ".join("%s %s %d" % (i, a, n) for (i, a), n in sorted(experimental.items())) +
                   ". KILNX001 is lab and test API. KILNX002 (Kiln.Client SteamGuard) is real but unproven: a mod opts in "
                   "with `NoWarn` for the id, and it may change in any release (docs/kiln/services/steamguard.md).")
        out.append("")
    return "\n".join(out)


# ---------------------------------------------------------------- the build of the XML docs


def build_xml(root, build_dir):
    """dotnet build (Release) of every gated project, as tools/check.sh does: -> the bin folder with <Asm>/release/<Asm>.xml."""
    build_dir = Path(build_dir)
    build_dir.mkdir(parents=True, exist_ok=True)
    slnx = build_dir / "gated.slnx"
    lines = ["<Solution>"]
    for asm, folder in Tree(root).projects():
        proj = sorted(folder.glob("*.csproj"))
        if len(proj) != 1:
            raise Problem(f"{folder} has a PublicAPI.Shipped.txt but not exactly one .csproj")
        lines.append('  <Project Path="%s" />' % os.path.relpath(proj[0], build_dir))
    lines.append("</Solution>")
    slnx.write_text("\n".join(lines) + "\n", encoding="utf-8")
    cmd = ["dotnet", "build", str(slnx), "-c", "Release", "-m:%s" % os.environ.get("KILN_CHECK_JOBS", "4"), "--artifacts-path",
           str(build_dir / "artifacts"), "-v", "q", "-nologo", "-p:ShouldUnsetParentConfigurationAndPlatform=false"]
    print("api-index: " + " ".join(cmd), file=sys.stderr)
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        errs = sorted({re.sub(r" \[[^\]]*\]$", "", l) for l in r.stdout.splitlines() if ": error " in l})
        raise Problem("the build of the XML docs failed:\n" + "\n".join(errs[:20] or r.stdout.splitlines()[-20:]))
    return build_dir / "artifacts" / "bin"


# ---------------------------------------------------------------- write and check


def write(out, files):
    out.mkdir(parents=True, exist_ok=True)
    for old in out.glob("*.md"):
        if old.name not in files:
            old.unlink()
    for name, text in sorted(files.items()):
        (out / name).write_text(text, encoding="utf-8", newline="\n")


def drift(out, files):
    """[problem lines] between the committed index folder and the generated files (empty: they are equal)."""
    problems = []
    have = {p.name for p in out.glob("*.md")} if out.is_dir() else set()
    for what, names in (("missing", sorted(set(files) - have)), ("stale file", sorted(have - set(files)))):
        for name in names[:5]:
            problems.append(f"{what}: {name}")
        if len(names) > 5:
            problems.append(f"{what}: ... and {len(names) - 5} more")
    shown = 0
    for name in sorted(set(files) & have):
        old = (out / name).read_text(encoding="utf-8")
        if old != files[name]:
            problems.append(f"differs: {name}")
            if shown < 3:
                shown += 1
                for l in list(difflib.unified_diff(old.split("\n"), files[name].split("\n"), "committed/" + name, "generated/" + name,
                                                   lineterm="", n=0))[:14]:
                    problems.append("    " + l[:200])
    return problems


def main(argv=None):
    here = Path(__file__).resolve().parent
    ap = argparse.ArgumentParser(description="The agent API index (references/api-index/): generate it, or --check that it is current.")
    ap.add_argument("--tree", default=str(here.parent.parent), help="the repository (default: this script's)")
    ap.add_argument("--out", help="the index folder (default: <tree>/%s)" % INDEX_DIR)
    ap.add_argument("--xml", default=os.environ.get("KILN_API_XML"), help="the compiled XML docs (see the module doc)")
    ap.add_argument("--build-dir", help="where the XML build goes without --xml (default: $KILN_OUT/api-index)")
    ap.add_argument("--check", action="store_true", help="compare with the committed index; exit 1 when it differs")
    a = ap.parse_args(argv)
    root = Path(a.tree).resolve()
    out = Path(a.out) if a.out else root / INDEX_DIR
    try:
        xml_dir = a.xml
        if not xml_dir:
            kiln_out = Path(os.environ.get("KILN_OUT") or root / "native" / "build")
            xml_dir = build_xml(root, a.build_dir or kiln_out / "api-index")
        files = generate(root, xml_dir)
    except (Problem, ET.ParseError, OSError) as e:
        print(f"api-index: {e}", file=sys.stderr)
        return 2
    total = kb(sum(nbytes(t) for t in files.values()))
    if a.check:
        bad = drift(out, files)
        if bad:
            print("api-index: the committed index is stale (%s):" % out)
            print("\n".join("  " + l for l in bad[:60]))
            print("api-index: regenerate and commit it: python3 -I tools/docs-site/api_index.py --xml %s, then git add -A -- %s "
                  "(new and removed parts too) and commit" % (xml_dir, INDEX_DIR))
            return 1
        print("api-index: %d files, %s, current" % (len(files), total))
        return 0
    write(out, files)
    print("api-index: %d files, %s, in %s" % (len(files), total, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
