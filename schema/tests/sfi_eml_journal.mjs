// SPDX-License-Identifier: MIT
// kiln-eml's startup change journal (schema/sfi/eml-journal/): the per-package record of what a kiln-mode ShroudForge
// pass changed, bound to one generation (format 1, startup-journal.schema.json), and the EML dialect's record of what EML's
// own pass changed, with member paths (format 2, eml-journal-2.schema.json). Schema contract tests over the fixtures and, with EML_JOURNAL_DIR=<folder>,
// over journals kiln-eml's own tests wrote there (EML_JOURNAL_OUT). Not a test of the GPL writer itself.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const here = new URL('.', import.meta.url);
const text = p => readFileSync(new URL(p, here), 'utf8');
const read = p => JSON.parse(text(p));
// Load input first so the red test diagnoses missing contracts, not a dependency.
const valid = read('../sfi/eml-journal/fixtures/valid.json');
const invalid = read('../sfi/eml-journal/fixtures/invalid.json');
const schema = read('../sfi/eml-journal/startup-journal.schema.json');
const schema2 = read('../sfi/eml-journal/eml-journal-2.schema.json');
const commonDir = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'],
  { cwd: fileURLToPath(here), encoding: 'utf8' }).trim();
const candidates = [process.env.AJV_2020,
  fileURLToPath(new URL('../../launcher/node_modules/ajv/dist/2020.js', here)),
  join(dirname(commonDir), 'launcher/node_modules/ajv/dist/2020.js')].filter(Boolean);
const found = candidates.find(existsSync);
assert(found, `AJV 2020 unavailable; tried ${candidates.join(', ')}`);
const Ajv = (await import(pathToFileURL(found).href)).default;
const ajv = new Ajv({ strict: false, validateFormats: false });
// The other sfi schemas in the same AJV, as a combined sweep loads them: the id lives under eml-journal/.
const outcome = read('../sfi/eml-startup/startup-outcome.schema.json');
const state = read('../sfi/sfi-state.schema.json');
const kilnCommon = read('../common.schema.json');
ajv.addSchema(kilnCommon);
ajv.addSchema(state);
ajv.addSchema(outcome);
let assertions = 0;
const files = readdirSync(new URL('../sfi/eml-journal/', here)).filter(n => n.endsWith('.schema.json')).sort();
assert.deepEqual(files, ['eml-journal-2.schema.json', 'startup-journal.schema.json'], 'one schema per format'); ++assertions;
assert.equal(schema.$id, 'eml-journal/startup-journal.schema.json'); ++assertions;
assert.equal(schema2.$id, 'eml-journal/eml-journal-2.schema.json'); ++assertions;
ajv.addSchema(schema);
ajv.addSchema(schema2);
const validate = ajv.getSchema('eml-journal/startup-journal.schema.json');
const validate2 = ajv.getSchema('eml-journal/eml-journal-2.schema.json');
const validators = { 'startup-journal': validate, 'eml-journal-2': validate2 };

// Kept out of the strictly read outcome: the outcome schema has no journal member and its format stays 1.
assert(!Object.keys(outcome.properties).some(k => /journal/i.test(k)), 'the outcome stays as it is'); ++assertions;
assert.equal(outcome.properties.format.const, 1); ++assertions;
// Shared grammar copied from the schemas it agrees with.
for (const [ours, theirs, why] of [
  [schema.$defs.sfId.pattern, state.$defs.sfId.pattern, 'sfId'],
  [schema.$defs.sha256.pattern, kilnCommon.$defs.sha256.pattern, 'sha256'],
  [schema.$defs.package.properties.version.maxLength, outcome.$defs.row.properties.version.maxLength, 'version'],
  [schema.properties.attempt.enum.join(), outcome.properties.attempt.enum.filter(a => a !== 'none').join(), 'attempt'],
  // format 2 shares format 1's grammar and lists
  [schema2.$defs.sfId.pattern, schema.$defs.sfId.pattern, 'format 2 sfId'],
  [schema2.$defs.sha256.pattern, schema.$defs.sha256.pattern, 'format 2 sha256'],
  [schema2.$defs.guid.pattern, schema.$defs.guid.pattern, 'format 2 guid'],
  [JSON.stringify(schema2.$defs.createdList), JSON.stringify(schema.$defs.createdList), 'format 2 createdList'],
  [JSON.stringify(schema2.$defs.contentList), JSON.stringify(schema.$defs.contentList), 'format 2 contentList'],
  [JSON.stringify(schema2.$defs.changes), JSON.stringify(schema.$defs.changes), 'format 2 changes'],
  [schema2.properties.attempt.enum.join(), schema.properties.attempt.enum.join(), 'format 2 attempt']]) {
  assert.equal(ours, theirs, why); ++assertions;
}
// Bounds: packages mirror the plan (the outcome's rows bound); each bucket's lists hold at most the writer's
// PACKAGE_ENTRIES, and the description names TOTAL_ENTRIES (kiln-eml sf_startup/journal.rs).
const writer = text('../../gpl/kiln-eml/kiln-eml/src/sf_startup/journal.rs');
const constant = name => { const m = new RegExp(`pub const ${name}: usize = (\\d+);`).exec(writer); assert(m, name); return Number(m[1]); };
const perPackage = constant('PACKAGE_ENTRIES');
const total = constant('TOTAL_ENTRIES');
assert.equal(schema.properties.packages.maxItems, outcome.properties.packages.maxItems, 'packages: the plan bound'); ++assertions;
for (const list of ['changedList', 'createdList', 'contentList']) {
  assert.equal(schema.$defs[list].maxItems, perPackage, list); ++assertions;
  assert.equal(schema2.$defs[list].maxItems, perPackage, `format 2 ${list}`); ++assertions;
}
assert(schema.description.includes(`at most ${perPackage} entries`) && schema.description.includes(`${total} in all`)); ++assertions;
// format 2's path bounds are the writer's (RESOURCE_PATHS, PATH_BYTES)
const resourcePaths = constant('RESOURCE_PATHS');
const pathBytes = constant('PATH_BYTES');
assert.equal(schema2.$defs.changedList.items.properties.paths.maxItems, resourcePaths); ++assertions;
assert.equal(schema2.$defs.path.maxLength, pathBytes); ++assertions;
assert(schema2.description.includes(`at most ${perPackage} entries`) && schema2.description.includes(`${total} in all`) &&
  schema2.description.includes(`at most ${resourcePaths} paths`) && schema2.description.includes(`at most ${pathBytes} bytes`)); ++assertions;

// Field order: a typed serializer writes these, in this order.
const ORDER = ['format', 'stem', 'generation', 'receiptSha256', 'dialect', 'attempt', 'truncated', 'packages', 'unattributed'];
const PACKAGE = ['id', 'version', 'kind', 'contentSha256', 'settingsSha256', 'truncated', 'changed', 'created', 'contents'];
const CHANGES = ['truncated', 'changed', 'created', 'contents'];
const CHANGED = ['guid', 'typeHash', 'part', 'createdBy'];
const CHANGED2 = [...CHANGED, 'paths'];
const CREATED = ['ordinal', 'guid', 'typeHash', 'part', 'explicit'];
const CONTENT = ['guid', 'size'];
// format 2's optional members, after the required ones and in this order (0.2.0-dev.32: the hosted writer, effect replay)
const OPTIONAL2 = ['writer', 'notApplied'];
const NOT_APPLIED = ['id', 'version', 'kind', 'contentSha256', 'reason'];
assert.deepEqual(Object.keys(schema.properties), ORDER); ++assertions;
assert.deepEqual([...schema.required].sort(), [...ORDER].sort()); ++assertions;
for (const [def, order] of [['package', PACKAGE], ['changes', CHANGES]]) {
  assert.deepEqual(Object.keys(schema.$defs[def].properties), order, def); ++assertions;
  assert.deepEqual([...schema.$defs[def].required].sort(), [...order].sort(), def); ++assertions;
}
for (const [def, order] of [['changedList', CHANGED], ['createdList', CREATED], ['contentList', CONTENT]]) {
  assert.deepEqual(Object.keys(schema.$defs[def].items.properties), order, def); ++assertions;
  assert.deepEqual([...schema.$defs[def].items.required].sort(), [...order].sort(), def); ++assertions;
}
assert.deepEqual(Object.keys(schema2.properties), [...ORDER, ...OPTIONAL2]); ++assertions;
assert.deepEqual([...schema2.required].sort(), [...ORDER].sort(), 'format 2: the optional members are optional'); ++assertions;
assert.deepEqual(Object.keys(schema2.$defs.notApplied.properties), NOT_APPLIED); ++assertions;
assert.deepEqual([...schema2.$defs.notApplied.required].sort(), [...NOT_APPLIED].sort()); ++assertions;
for (const [def, order] of [['package', PACKAGE], ['changes', CHANGES]]) {
  assert.deepEqual(Object.keys(schema2.$defs[def].properties), order, `format 2 ${def}`); ++assertions;
}
assert.deepEqual(Object.keys(schema2.$defs.changedList.items.properties), CHANGED2); ++assertions;
assert.deepEqual([...schema2.$defs.changedList.items.required].sort(), [...CHANGED2].sort()); ++assertions;

// Rules JSON Schema cannot say (the writer keeps them; consumers rely on them).
const keyOf = e => `${e.guid}/${e.typeHash}/${e.part}`;
function rules(j, label) {
  const inOrder = (o, order, where) => assert.deepEqual(Object.keys(o), order, `${label}: ${where} field order`);
  const changedOrder = j.format === 2 ? CHANGED2 : CHANGED;
  inOrder(j, j.format === 2 ? [...ORDER, ...OPTIONAL2.filter(k => k in j)] : ORDER, 'record');
  for (const n of j.notApplied ?? []) inOrder(n, NOT_APPLIED, 'notApplied');
  // a package not applied did not run: it is not among the packages, and is listed once
  const missed = (j.notApplied ?? []).map(n => n.id);
  assert.equal(new Set(missed).size, missed.length, `${label}: notApplied lists a package once`); ++assertions;
  assert(!missed.some(id => j.packages.some(p => p.id === id)), `${label}: a package not applied is not among those that ran`); ++assertions;
  // the hosted writer keeps no receipt: its receipt is its generation
  if (j.writer === 'shroudforge') { assert.equal(j.receiptSha256, j.generation, `${label}: hosted receipt`); ++assertions; }
  const ids = j.packages.map(p => p.id);
  assert.equal(new Set(ids).size, ids.length, `${label}: one entry per package`); ++assertions;
  const buckets = [...j.packages.map(p => [p.id, p]), [null, j.unattributed]];
  const created = new Map(); // key -> [package, ordinal]
  let entries = 0;
  for (const [id, b] of buckets) {
    inOrder(b, id === null ? CHANGES : PACKAGE, id ?? 'unattributed');
    for (const e of b.changed) inOrder(e, changedOrder, 'changed');
    // format 2: a resource's paths sorted (code units), each once, the whole resource alone
    for (const e of b.changed.filter(() => j.format === 2)) {
      assert.deepEqual(e.paths, [...new Set(e.paths)].sort(), `${label}: ${id}: paths sorted, each once`); ++assertions;
      assert(!e.paths.includes('') || e.paths.length === 1, `${label}: ${id}: the whole resource alone`); ++assertions;
    }
    for (const e of b.created) inOrder(e, CREATED, 'created');
    for (const e of b.contents) inOrder(e, CONTENT, 'contents');
    const n = b.changed.length + b.created.length + b.contents.length;
    assert(n <= perPackage, `${label}: ${id}: ${n} entries`); ++assertions;
    entries += n;
    // ordinals 1, 2, ... in creation order (the ones over the bound are the last)
    b.created.forEach((e, i) => assert.equal(e.ordinal, i + 1, `${label}: ${id}: ordinals`)); ++assertions;
    for (const e of b.created) {
      assert(!created.has(keyOf(e)), `${label}: ${keyOf(e)} created twice`); ++assertions;
      created.set(keyOf(e), [id, e.ordinal]);
    }
    const changedKeys = b.changed.map(keyOf);
    assert.equal(new Set(changedKeys).size, changedKeys.length, `${label}: ${id}: a resource listed twice`); ++assertions;
    const contentKeys = b.contents.map(c => c.guid);
    assert.equal(new Set(contentKeys).size, contentKeys.length, `${label}: ${id}: a content listed twice`); ++assertions;
    // a content's GUID begins with its size (the first group is the size, little-endian)
    for (const c of b.contents) { assert.equal(parseInt(c.guid.slice(0, 8), 16), c.size, `${label}: ${c.guid}`); ++assertions; }
  }
  assert(entries <= total, `${label}: ${entries} entries in all`); ++assertions;
  assert.equal(j.truncated, buckets.some(([, b]) => b.truncated), `${label}: truncated exactly when a bucket is`); ++assertions;
  // createdBy names the package (never the writer itself) whose created list holds that ordinal under that key
  for (const [id, b] of buckets) {
    for (const e of b.changed) {
      if (e.createdBy === null) {
        assert(!created.has(keyOf(e)) || created.get(keyOf(e))[0] === null, `${label}: ${id}: a created resource without its creator`); ++assertions;
        continue;
      }
      assert.notEqual(e.createdBy.package, id, `${label}: ${id} lists its own creation as changed`); ++assertions;
      const creator = j.packages.find(p => p.id === e.createdBy.package);
      assert(creator, `${label}: ${id}: createdBy names no package of the journal`); ++assertions;
      const entry = creator.created.find(c => c.ordinal === e.createdBy.ordinal);
      if (entry) { assert.equal(keyOf(entry), keyOf(e), `${label}: createdBy's key`); ++assertions; }
      else { assert(creator.truncated, `${label}: createdBy names a creation its creator does not list`); ++assertions; }
      if (id !== null) {
        assert(ids.indexOf(e.createdBy.package) < ids.indexOf(id), `${label}: created after its writer ran`); ++assertions;
      }
    }
  }
}

// Every valid fixture validates and keeps the rules; the other format, the other schema and an unknown member at any object
// level are refused.
assert.deepEqual(Object.keys(valid), ['startup-journal', 'eml-journal-2']); ++assertions;
for (const [name, label, record] of Object.entries(valid).flatMap(([n, v]) => Object.entries(v).map(([l, r]) => [n, l, r]))) {
  const own = validators[name];
  assert(own(record), `${label}: ${JSON.stringify(own.errors)}`); ++assertions;
  rules(record, label);
  const bad = structuredClone(record); bad.format = record.format === 1 ? 2 : 1;
  assert.equal(own(bad), false, `${label}: format`); ++assertions;
  const other = name === 'startup-journal' ? validate2 : validate;
  assert.equal(other(record), false, `${label}: the other format's schema refuses it`); ++assertions;
  const objects = [];
  const walk = (v, path) => { if (v && typeof v === 'object') { if (!Array.isArray(v)) objects.push(path);
    for (const [k, c] of Object.entries(v)) walk(c, [...path, k]); } };
  walk(record, []);
  for (const path of objects) {
    const nested = structuredClone(record);
    path.reduce((o, k) => o[k], nested).silent_extension = true;
    assert.equal(own(nested), false, `${label}: unknown member at ${path.join('.') || '(root)'}`); ++assertions;
  }
}
// Every invalid fixture (one valid fixture with one rule broken, at most two leaf paths away) is refused.
assert(invalid.length >= 40);
const leaves = (a, b, path = '') => {
  if (JSON.stringify(a) === JSON.stringify(b)) return [];
  if (a && b && typeof a === 'object' && typeof b === 'object' && Array.isArray(a) === Array.isArray(b)) {
    const out = [...new Set([...Object.keys(a), ...Object.keys(b)])].flatMap(k => leaves(a[k], b[k], `${path}/${k}`));
    return out.length ? out : [`${path} (order)`];
  }
  return [path];
};
for (const { schema: name, why, record } of invalid) {
  assert(name in validators, `${why}: schema ${name}`);
  const nearest = Math.min(...Object.values(valid[name]).map(v => leaves(v, record).length));
  assert(nearest >= 1 && nearest <= 2, `${why}: ${nearest} changes from the nearest valid fixture`); ++assertions;
  assert.equal(validators[name](record), false, why); ++assertions;
}
assert(invalid.filter(i => i.schema === 'eml-journal-2').length >= 10, 'format 2 has its own invalid fixtures'); ++assertions;
// format 2's path bound, from the writer's RESOURCE_PATHS (a fixture of that many paths would be large): a resource with
// that many paths is valid, one more is not; Kiln's reader takes the same bound (archive_journal.h kArchiveJournalPaths).
{
  const at = n => {
    const r = JSON.parse(JSON.stringify(valid['eml-journal-2'].sixty_four_paths));
    r.packages[0].changed[0].paths = Array.from({ length: n }, (_, i) => `f${String(i).padStart(5, '0')}`);
    return r;
  };
  assert.equal(validators['eml-journal-2'](at(resourcePaths)), true, `${resourcePaths} paths`); ++assertions;
  assert.equal(validators['eml-journal-2'](at(resourcePaths + 1)), false, `${resourcePaths + 1} paths`); ++assertions;
  const reader = text('../../native/services/content/registry/archive_journal.h');
  const m = /constexpr size_t kArchiveJournalPaths = (\d+);/.exec(reader);
  assert(m && Number(m[1]) === resourcePaths, `Kiln's reader takes ${resourcePaths} paths per resource`); ++assertions;
}
// Coverage: both stems, both attempts, both kinds, a created resource named by another package, an explicit and a
// random creation, a truncated journal, an unattributed write.
const records = Object.values(valid['startup-journal']);
for (const [what, ok] of [
  ['stem enshrouded', records.some(r => r.stem === 'enshrouded')],
  ['stem enshrouded_server', records.some(r => r.stem === 'enshrouded_server')],
  ['attempt first', records.some(r => r.attempt === 'first')],
  ['attempt retry', records.some(r => r.attempt === 'retry')],
  ['kind eml', records.some(r => r.packages.some(p => p.kind === 'eml'))],
  ['createdBy', records.some(r => r.packages.some(p => p.changed.some(c => c.createdBy)))],
  ['explicit and random', records.some(r => r.packages.some(p => p.created.some(c => c.explicit) && p.created.some(c => !c.explicit)))],
  ['truncated', records.some(r => r.truncated)],
  ['unattributed', records.some(r => r.unattributed.changed.length > 0)]]) {
  assert(ok, `a fixture with ${what}`); ++assertions;
}
const records2 = Object.values(valid['eml-journal-2']);
const paths2 = records2.flatMap(r => [...r.packages, r.unattributed].flatMap(p => p.changed.flatMap(c => c.paths)));
for (const [what, ok] of [
  ['format 2: both stems', ['enshrouded', 'enshrouded_server'].every(s => records2.some(r => r.stem === s))],
  ['format 2: both attempts', ['first', 'retry'].every(a => records2.some(r => r.attempt === a))],
  ['format 2: the whole resource', paths2.includes('')],
  ['format 2: a list element', paths2.some(p => /\[[0-9]+\]/.test(p))],
  ['format 2: a list membership', paths2.some(p => p.endsWith('[]'))],
  ['format 2: a nested field', paths2.some(p => /^[^[]+\.[^[]+$/.test(p))],
  ['format 2: createdBy', records2.some(r => r.packages.some(p => p.changed.some(c => c.createdBy)))],
  ['format 2: truncated', records2.some(r => r.truncated)],
  ['format 2: unattributed', records2.some(r => r.unattributed.changed.length > 0)],
  ['format 2: a version not known', records2.some(r => r.packages.some(p => p.version === null))],
  ['format 2: the ShroudForge dialect', records2.some(r => r.dialect === 'shroudforge')],
  ['format 2: a ShroudForge and an EML package in one pass', records2.some(r => ['shroudforge', 'eml'].every(k => r.packages.some(p => p.kind === k)))],
  ['format 2: the hosted writer', records2.some(r => r.writer === 'shroudforge')],
  ['format 2: kiln-eml named as the writer', records2.some(r => r.writer === 'kiln-eml')],
  ['format 2: a package not applied', records2.some(r => (r.notApplied ?? []).length > 0)],
  ['format 2: an empty notApplied', records2.some(r => Array.isArray(r.notApplied) && r.notApplied.length === 0)]]) {
  assert(ok, `a fixture with ${what}`); ++assertions;
}
// kiln-eml's own record of a fixed pass (gpl/kiln-eml/kiln-eml/tests/journal_contract.rs fails unless it is byte for byte
// what the writer makes; Kiln's native reader is tested on it, native/services/content/tests/registry_writers_test.cpp):
// valid, in field order (raw text), the rules kept.
// The EML dialect's (kiln-eml-writer-eml.json, format 2, the same test's second half) likewise.
for (const [name, own] of [['kiln-eml-writer.json', validate], ['kiln-eml-writer-eml.json', validate2], ['kiln-eml-writer-sf2.json', validate2]]) {
  const raw = text(`../sfi/eml-journal/fixtures/${name}`).trimEnd();
  const record = JSON.parse(raw);
  assert(own(record), `${name}: ${JSON.stringify(own.errors)}`); ++assertions;
  rules(record, name);
  let at = 0;
  for (const field of ORDER) { const i = raw.indexOf(`"${field}":`, at); assert(i >= at, `${name}: ${field}`); at = i; }
  ++assertions;
}
// The journals kiln-eml's tests wrote (EML_JOURNAL_OUT), when given: valid, in field order (raw text), the rules kept.
let written = 0;
if (process.env.EML_JOURNAL_DIR) {
  const dir = process.env.EML_JOURNAL_DIR;
  for (const name of readdirSync(dir).filter(n => n.endsWith('.json')).sort()) {
    const raw = readFileSync(join(dir, name), 'utf8');
    const record = JSON.parse(raw);
    const own = record.format === 2 ? validate2 : validate;
    assert(own(record), `${name}: ${JSON.stringify(own.errors)}`); ++assertions;
    rules(record, name);
    let at = 0;
    for (const field of ORDER) { const i = raw.indexOf(`"${field}":`, at); assert(i >= at, `${name}: ${field}`); at = i; }
    ++written;
  }
  assert(written > 0, `no journal in ${dir}`);
}
console.log(`eml startup journal schema and fixtures: ${assertions} assertions passed${written ? `, ${written} written journals` : ''} (not the writer)`);
