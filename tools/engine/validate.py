#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Static check of a ported engine profile against a read-only COPY of its executable (never the installed game, never
run). Reads the file only; prints one line per check and exits non-zero on any failure.

  python3 -I tools/engine/validate.py sigdb/engine/server/26430253.json /path/to/copy/enshrouded_server.exe

ecsReadLayout: every field that code in the exe pins down is read out of that code and compared (ecs_read_checks):
the entity manager inside the world (GetComponent, GetEntityTransform), the entity map's count, slots, occupancy bits
and keys and its id hash (GetEntityTransform, GetComponentByType and the map lookup it calls), the record's layout,
chunk and row and the layout's bits, offsets and strides (GetComponentByType), the record's id, chunk and row again
(the change tracker the profile names), the record's definition pointer and the definition's UUID offset (the
engine's own entity-to-template reader), the world inside the GameServer (two GameServer functions). Each function is
found by its own code, never by an address. The fields no code shows by itself are listed with what confirms them at
run time (RUNTIME_ONLY).
"""
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

# ecsReadLayout fields no function of the exe shows by itself, and what confirms them when Kiln runs.
RUNTIME_ONLY = {
    'entityGeneration': 'no function reads it alone; a recycled id validates stale only through it (in-game check, '
                        'and the profile\'s own runtime evidence)',
    'executionRegistryOwnerOffset': 'every view checks the entity manager\'s owner word against the execution owner '
                                    'plus this offset (ecs-manager-registry-owner-mismatch otherwise)',
    'clientSceneOwnerOffset': 'every client view resolves the Scene list through it, twice (the view refuses a change)',
    'sceneEntriesPointerOffset': 'the view reads the active Scene list (count <= capacity <= the bound) twice',
    'sceneEntriesCountOffset': 'as sceneEntriesPointerOffset',
    'sceneEntriesCapacityOffset': 'as sceneEntriesPointerOffset',
    'sceneEntryStride': 'as sceneEntriesPointerOffset',
    'sceneEntryContextOffset': 'the ready entry\'s context must be nonzero and the same on the second read',
    'sceneEntryUuidOffset': 'the ready entry\'s UUID must be nonzero and the same on the second read',
    'sceneEntryStateOffset': 'exactly one entry may be in the ready state',
    'sceneEntryReadyState': 'as sceneEntryStateOffset',
    'entityDefinitionUuidOffset': 'the code shows the engine copies the definition whole at this offset; which of its '
                                  '16-byte fields is the template UUID the in-game check shows (it logs the UUID)',
    'maximumEntities': 'a bound Kiln applies to the entity map\'s capacity, not a fact of the code',
    'maximumSceneEntries': 'a bound Kiln applies to the Scene list, not a fact of the code',
}
ENTITY_HASH = 0x045d9f3b  # the multiplier of the entity map's id hash, the one Kiln's probe uses (ecs_engine.cpp)
HOLE = re.compile(r'\{([a-z_0-9]+):(d8|d32|rel32)\}')
WIDTH = {'d8': 1, 'd32': 4, 'rel32': 4}


def compile_pattern(pattern):
    """'48 8D BA {map:d32} ?? E8 {find:rel32}' -> (regex, [(name, kind, offset)])."""
    rx, holes, n = b'', [], 0
    for tok in re.findall(r'\{[^}]+\}|\?\?|[0-9A-Fa-f]{2}', pattern):
        m = HOLE.fullmatch(tok)
        if m:
            holes.append((m.group(1), m.group(2), n))
            rx += b'.' * WIDTH[m.group(2)]
            n += WIDTH[m.group(2)]
        elif tok == '??':
            rx += b'.'
            n += 1
        else:
            rx += re.escape(bytes([int(tok, 16)]))
            n += 1
    return re.compile(rx, re.S), holes


def find(text, text_va, pattern, lo=None, hi=None):
    """Every match of `pattern` in .text (RVAs lo..hi): [(rva, {hole: value})]; rel32 holes give the target RVA."""
    rx, holes = compile_pattern(pattern)
    start = 0 if lo is None else max(0, lo - text_va)
    end = len(text) if hi is None else min(len(text), hi - text_va)
    out, at = [], start
    while True:
        m = rx.search(text, at, end)
        if not m:
            return out
        values = {}
        for name, kind, off in holes:
            raw = text[m.start() + off: m.start() + off + WIDTH[kind]]
            if kind == 'rel32':
                values[name] = text_va + m.start() + off + 4 + struct.unpack('<i', raw)[0]
            else:
                values[name] = raw[0] if kind == 'd8' else struct.unpack('<I', raw)[0]
        out.append((text_va + m.start(), values))
        at = m.start() + 1


def ecs_read_checks(ecs, write, text, text_va):
    """The profile's ecsReadLayout (and the change tracker of its ecsWriteLayout) against the code of .text.
    Returns (checks, shown): checks = [(ok, text)], shown = the ecsReadLayout fields the code showed."""
    checks, shown = [], set()

    def ok(cond, line, *fields):
        checks.append((bool(cond), line))
        if cond:
            shown.update(fields)

    def one(name, pattern, lo=None, hi=None):
        hits = find(text, text_va, pattern, lo, hi)
        checks.append((len(hits) == 1, '%s found once by its code (%d match%s)' % (name, len(hits), '' if len(hits) == 1 else 'es')))
        return hits[0] if len(hits) == 1 else None

    # The entity manager inside the world, and its registry owner word at +0 (GetComponent: registry = *storage).
    gc = one('GetComponent', '48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8D BA {storage:d32} 44 89')
    if gc:
        ok(gc[1]['storage'] == ecs['executionEntityManagerOffset'],
           'executionEntityManagerOffset %d = GetComponent\'s world + %d' % (ecs['executionEntityManagerOffset'], gc[1]['storage']),
           'executionEntityManagerOffset')
        reg = find(text, text_va, '48 8B 0F 41 8B D9 48 81 C1 {components:d32} E8', gc[0], gc[0] + 96)
        ok(len(reg) == 1 and ecs['entityManagerOwnerOffset'] == 0,
           'entityManagerOwnerOffset %d: GetComponent reads the registry owner at the entity manager + 0' % ecs['entityManagerOwnerOffset'],
           'entityManagerOwnerOffset')
    get = one('GetEntityTransform', '48 89 5C 24 08 57 48 83 EC 20 48 8D B9 {storage:d32} 89 54 24 38 48 8D 8F')
    get_map = None
    if get:
        ok(get[1]['storage'] == ecs['executionEntityManagerOffset'],
           'executionEntityManagerOffset %d = GetEntityTransform\'s world + %d' % (ecs['executionEntityManagerOffset'], get[1]['storage']),
           'executionEntityManagerOffset')
        maps = find(text, text_va, '48 8D 8F {map:d32} 48 8D 54 24 38 E8 {find:rel32} 48 3B 87 {count:d32} 73 ?? 48 8B 97 {table:d32}',
                    get[0], get[0] + 96)
        ok(len(maps) == 1, 'GetEntityTransform looks the entity up in the entity map')
        if len(maps) == 1:
            get_map = maps[0][1]
            ok(get_map['count'] == ecs['entityManagerCount'],
               'entityManagerCount %d = GetEntityTransform\'s bound check (manager + %d)' % (ecs['entityManagerCount'], get_map['count']),
               'entityManagerCount')
            ok(get_map['table'] == ecs['entityManagerTable'],
               'entityManagerTable %d = GetEntityTransform\'s record slots (manager + %d)' % (ecs['entityManagerTable'], get_map['table']),
               'entityManagerTable')
    # The component address calculation (GetComponentByType) and the map lookup it calls.
    by_type = one('GetComponentByType',
                  '48 89 5C 24 08 48 89 74 24 10 44 89 4C 24 20 57 48 83 EC 20 48 8D BA {map:d32} 48 8B D9 48 8B CF '
                  '48 8D 54 24 48 49 8B F0 E8 {find:rel32} 48 3B 47 {cap:d8} 0F 83 ?? ?? ?? ?? 48 8B 57 {values:d8} '
                  '4C 8D 04 C2 4D 85 C0 0F 84 ?? ?? ?? ?? 4D 8B 00 4D 85 C0 74 ?? 48 85 F6 74 ?? 44 0F B7 0E '
                  '4D 8B 50 {layout:d8} 41 8B C1 48 C1 E8 06 41 0F B6 C9 49 8B 14 C2')
    if by_type:
        v = by_type[1]
        ok(v['map'] + v['cap'] == ecs['entityManagerCount'],
           'entityManagerCount %d = GetComponentByType\'s map %d + capacity %d' % (ecs['entityManagerCount'], v['map'], v['cap']),
           'entityManagerCount')
        ok(v['map'] + v['values'] == ecs['entityManagerTable'],
           'entityManagerTable %d = GetComponentByType\'s map %d + slots %d' % (ecs['entityManagerTable'], v['map'], v['values']),
           'entityManagerTable')
        ok(not get_map or (get_map['map'] == v['map'] and get_map['find'] == v['find']),
           'GetEntityTransform and GetComponentByType use the same entity map and lookup')
        ok(v['layout'] == ecs['entityLayout'], 'entityLayout %d = GetComponentByType\'s record + %d' % (ecs['entityLayout'], v['layout']),
           'entityLayout')
        ok(ecs['componentBits'] == 0,
           'componentBits %d: GetComponentByType tests the bit in the layout\'s words at + 0 ([layout + index / 64 * 8])' % ecs['componentBits'],
           'componentBits')
        fields = find(text, text_va, '43 0F B7 94 4A {strides:d32} 41 8B 40 {row:d8} 43 0F B7 8C 4A {offsets:d32} '
                      '48 0F AF C2 48 03 C8 48 8B C3 49 03 48 {storage:d8}', by_type[0], by_type[0] + 256)
        ok(len(fields) == 1, 'GetComponentByType computes the component address (offset + row * stride + chunk)')
        if len(fields) == 1:
            f = fields[0][1]
            ok(f['strides'] == ecs['componentStrides'], 'componentStrides %d = GetComponentByType\'s layout + %d' % (ecs['componentStrides'], f['strides']), 'componentStrides')
            ok(f['offsets'] == ecs['componentOffsets'], 'componentOffsets %d = GetComponentByType\'s layout + %d' % (ecs['componentOffsets'], f['offsets']), 'componentOffsets')
            ok(f['row'] == ecs['entityRow'], 'entityRow %d = GetComponentByType\'s record + %d' % (ecs['entityRow'], f['row']), 'entityRow')
            ok(f['storage'] == ecs['entityStorage'], 'entityStorage %d = GetComponentByType\'s record + %d' % (ecs['entityStorage'], f['storage']), 'entityStorage')
        look = find(text, text_va, '48 89 5C 24 08 48 89 7C 24 10 83 79 {count32:d8} 00 4C 8B C9 4C 8B 59 {cap:d8} 74 ?? 8B 1A 8B C3 '
                    '49 8B 79 {bits:d8} C1 E8 10 33 C3 69 D0 {k1:d32} 8B C2 C1 E8 10 33 C2 69 C0 {k2:d32}', v['find'], v['find'] + 64)
        ok(len(look) == 1 and look[0][0] == v['find'], 'the entity map lookup GetComponentByType calls (RVA 0x%x) hashes the id' % v['find'])
        if len(look) == 1:
            m = look[0][1]
            ok(m['cap'] == v['cap'], 'the lookup bounds the probe by the same capacity (map + %d)' % m['cap'])
            ok(v['map'] + m['bits'] == ecs['entityManagerOccupancy'],
               'entityManagerOccupancy %d = map %d + the lookup\'s occupancy bits %d' % (ecs['entityManagerOccupancy'], v['map'], m['bits']),
               'entityManagerOccupancy')
            ok(m['k1'] == ENTITY_HASH and m['k2'] == ENTITY_HASH,
               'the lookup\'s id hash multiplies by 0x%08x twice, as Kiln\'s probe does' % ENTITY_HASH)
            keys = find(text, text_va, '4D 8B 51 {keys:d8} 41 39 1C 82', v['find'], v['find'] + 0x70)
            ok(len(keys) == 1 and v['map'] + keys[0][1]['keys'] == ecs['entityManagerKeys'],
               'entityManagerKeys %d = map %d + the lookup\'s 32-bit keys %s' % (
                   ecs['entityManagerKeys'], v['map'], keys[0][1]['keys'] if len(keys) == 1 else '?'), 'entityManagerKeys')
    # The change tracker the profile names: the entity record's chunk and row, and its id passed on.
    tracker = write.get('changeTrackerRva')
    if tracker is not None:
        head = find(text, text_va, '4D 8B 50 {storage:d8} 49 8B F0 41 0F B7 E9 4C 8B F2 45 8B 48 {row:d8}', tracker, tracker + 0x40)
        ident = find(text, text_va, '44 8B 46 {id:d8}', tracker, tracker + 0x140)
        ok(len(head) == 1 and head[0][1]['storage'] == ecs['entityStorage'] and head[0][1]['row'] == ecs['entityRow'],
           'entityStorage %d and entityRow %d = the change tracker\'s record reads' % (ecs['entityStorage'], ecs['entityRow']),
           'entityStorage', 'entityRow')
        ok(len(ident) == 1 and ident[0][1]['id'] == ecs['entityId'],
           'entityId %d = the change tracker passes the record\'s id (record + %s)' % (ecs['entityId'], ident[0][1]['id'] if len(ident) == 1 else '?'),
           'entityId')
    # The engine's own entity-to-template reader: map lookup, the record's definition, 16 bytes of UUID.
    reader = ('48 8D 8F {map:d32} E8 {find:rel32} 48 3B 87 {count:d32} 73 ?? 48 8B 8F {table:d32} '
              '48 8D 04 C1 48 85 C0 74 ?? 48 8B 00 48 85 C0 74 ?? 48 8B 40 {definition:d8} 0F 10 {modrm:d8}')
    defs = find(text, text_va, reader)
    modrm_at = [off for name, _, off in compile_pattern(reader)[1] if name == 'modrm'][0]
    ok(len(defs) >= 1, 'the engine\'s entity-to-template reader found by its code (%d)' % len(defs))
    if defs:
        uuid = ecs.get('entityDefinitionUuidOffset')
        agree = all(d['count'] == ecs['entityManagerCount'] and d['table'] == ecs['entityManagerTable'] and
                    d['definition'] == ecs['entityDefinitionPointer'] and (not by_type or d['find'] == by_type[1]['find'])
                    for _, d in defs)
        ok(agree, 'entityDefinitionPointer %d = the template reader\'s record + %s (map, count, slots and lookup as above)' % (
            ecs['entityDefinitionPointer'], sorted({d['definition'] for _, d in defs})), 'entityDefinitionPointer')
        # movups xmm0, [rax] (modrm 0x00) copies at + 0; [rax + d8] (0x40) at + d8. The definition holds more than one
        # 16-byte field, so this shows the profile's offset is one the engine copies whole, not which one is the UUID.
        offsets = set()
        for rva, d in defs:
            if d['modrm'] == 0x00:
                offsets.add(0)
            elif d['modrm'] == 0x40:
                offsets.add(text[rva + modrm_at + 1 - text_va])
        ok(uuid in offsets,
           'entityDefinitionUuidOffset %s: the engine copies 16 bytes of the definition at + %s (one of them)' % (uuid, sorted(offsets)))
    # The world inside the GameServer: two of its functions read or set it.
    worlds = [find(text, text_va, '48 89 87 {world:d32} 48 89 4C 24 30 4C 8D 87'),
              find(text, text_va, '49 8B 8E {world:d32} E8 ?? ?? ?? ?? 49 8B CE E8 ?? ?? ?? ?? 44 38 BD ?? ?? ?? ?? 74 ?? '
                   '49 8B CE E8')]
    ok(all(len(w) == 1 and w[0][1]['world'] == ecs['mainExecutionOwnerOffset'] for w in worlds),
       'mainExecutionOwnerOffset %d = the world the GameServer stores and reads (%s)' % (
           ecs['mainExecutionOwnerOffset'], [w[0][1]['world'] if len(w) == 1 else '?' for w in worlds]), 'mainExecutionOwnerOffset')
    return checks, shown


def pe(data):
    pe_at = struct.unpack_from('<I', data, 0x3c)[0]
    if data[:2] != b'MZ' or data[pe_at:pe_at + 4] != b'PE\0\0':
        raise SystemExit('not a PE image')
    machine, count, timestamp = struct.unpack_from('<HHI', data, pe_at + 4)
    optional_size = struct.unpack_from('<H', data, pe_at + 20)[0]
    optional = pe_at + 24
    if struct.unpack_from('<H', data, optional)[0] != 0x20b or machine != 0x8664:
        raise SystemExit('not an AMD64 PE32+ image')
    base = struct.unpack_from('<Q', data, optional + 24)[0]
    size_of_image = struct.unpack_from('<I', data, optional + 56)[0]
    sections = {}
    for i in range(count):
        s = optional + optional_size + 40 * i
        name = data[s:s + 8].rstrip(b'\0').decode()
        vsize, va, rsize, raw = struct.unpack_from('<IIII', data, s + 8)
        characteristics = struct.unpack_from('<I', data, s + 36)[0]
        sections[name] = (va, max(vsize, rsize), raw, rsize, characteristics)
    return timestamp, size_of_image, base, sections


def main():
    profile_path, binary_path = Path(sys.argv[1]), Path(sys.argv[2])
    if binary_path.is_symlink() or not binary_path.is_file():
        raise SystemExit('%s: not a regular file (copies only, never a link into a game install)' % binary_path)
    profile = json.loads(profile_path.read_text())
    data = binary_path.read_bytes()
    timestamp, size_of_image, base, sections = pe(data)
    failures = 0

    def ok(cond, text):
        nonlocal failures
        print(('ok   ' if cond else 'FAIL ') + text)
        failures += not cond

    def section_of(rva):
        for name, (va, size, raw, rsize, ch) in sections.items():
            if va <= rva < va + size:
                return name
        return None

    def file_bytes(rva, n):
        for name, (va, size, raw, rsize, ch) in sections.items():
            if va <= rva and rva + n <= va + min(size, rsize):
                return data[raw + rva - va: raw + rva - va + n]
        return None
    pdata = sections.get('.pdata')
    starts = set()
    if pdata:
        va, size, raw, rsize, ch = pdata
        for off in range(raw, raw + min(size, rsize), 12):
            b, e, u = struct.unpack_from('<III', data, off)
            if b:
                starts.add(b)
    image = profile['image']
    ok(hashlib.sha256(data).hexdigest() == image['sha256'], 'sha256 of the file = image.sha256')
    ok(timestamp == image['timestamp'], 'PE TimeDateStamp %d = image.timestamp' % timestamp)
    ok(size_of_image == image['size'], 'PE SizeOfImage %d = image.size (file length %d)' % (size_of_image, len(data)))
    ok(binary_path.name == profile['target'], 'file name = target %s' % profile['target'])
    w = profile['ecsWriteLayout']
    ok(file_bytes(w['changeTrackerRva'], 33) == bytes.fromhex(w['changeTrackerBytes']),
       'changeTrackerRva 0x%x holds changeTrackerBytes' % w['changeTrackerRva'])
    code = [('ecsWriteLayout.threadCallbackRva', w['threadCallbackRva']),
            ('ecsWriteLayout.changeTrackerRva', w['changeTrackerRva']),
            ('resourceWriteLayout.threadCallbackRva', profile['resourceWriteLayout']['threadCallbackRva']),
            ('resourceLayout.resourceServiceIdCallbackRva', profile['resourceLayout']['resourceServiceIdCallbackRva']),
            ('resourceLayout.resourceServiceHandleCallbackRva', profile['resourceLayout']['resourceServiceHandleCallbackRva'])]
    sd = profile.get('systemDependencyLayout', {})
    for key in ('recordBuilderRva', 'accessSetPackRva', 'compatibilityPredicateRva'):
        if key in sd:
            code.append(('systemDependencyLayout.' + key, sd[key]))
    for name, rva in code:
        ok(rva in starts and section_of(rva) == '.text', '%s 0x%x is a .pdata function start in .text' % (name, rva))
    for key in ('threadRegistryLockRva', 'threadRegistryEntriesRva', 'threadRegistryCountRva'):
        ok(section_of(w[key]) == '.data', 'ecsWriteLayout.%s 0x%x lies in .data' % (key, w[key]))
    ok(w['threadRegistryEntriesRva'] + w['threadRegistryCapacity'] * w['threadRegistryEntryStride'] <=
       sections['.data'][0] + sections['.data'][1], 'thread registry rows fit in .data')
    owners = profile['ownerLayout']
    text_va, text_size = sections['.text'][0], sections['.text'][1]
    for key in ('rootVtableRva', 'clientVtableRva', 'controllerVtableRva'):
        rva = owners[key]
        if not rva:
            ok(profile['target'] == 'enshrouded_server.exe' and key == 'rootVtableRva', 'ownerLayout.%s 0 (dedicated server)' % key)
            continue
        first = file_bytes(rva, 8)
        target = struct.unpack('<Q', first)[0] - base if first else -1
        ok(section_of(rva) == '.rdata' and text_va <= target < text_va + text_size,
           'ownerLayout.%s 0x%x is a vtable in .rdata whose first slot points into .text' % (key, rva))
    if owners['rootObjectRva']:
        ok(section_of(owners['rootObjectRva']) == '.data', 'ownerLayout.rootObjectRva 0x%x lies in .data' % owners['rootObjectRva'])
    for name in (w['threadName'], profile['resourceWriteLayout']['threadName']):
        ok(name.encode() + b'\0' in data, 'thread name "%s" is a string in the image' % name)
    ecs = profile.get('ecsReadLayout')
    if ecs:
        text_va, _, text_raw, text_rsize, _ = sections['.text']
        checks, shown = ecs_read_checks(ecs, w, data[text_raw:text_raw + text_rsize], text_va)
        for cond, line in checks:
            ok(cond, line)
        rest = sorted(set(ecs) - shown)
        ok(all(k in RUNTIME_ONLY for k in rest), 'every ecsReadLayout field is shown by code or listed as run-time only '
           '(%d of %d by code)' % (len(shown & set(ecs)), len(ecs)))
        for key in rest:
            print('note ecsReadLayout.%s = %s: %s' % (key, ecs[key], RUNTIME_ONLY.get(key, 'NOT COVERED')))
    print('%s: %d failure(s)' % (profile_path.name, failures))
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
