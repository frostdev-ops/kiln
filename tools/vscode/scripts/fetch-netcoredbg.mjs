// SPDX-License-Identifier: MIT
// Fetches the pinned netcoredbg win64 release into the build dir, checks its sha256, and stages it for the .vsix:
//   node scripts/fetch-netcoredbg.mjs            -> $KILN_OUT/netcoredbg/<version>/, then dist/netcoredbg/
//   node scripts/fetch-netcoredbg.mjs --check-zip <file>   only checks a file against the pin (exit 1 when it differs)
// Both places are ignored by git: never commit these binaries. Their licences are in licenses/netcoredbg.txt, which goes
// along into the .vsix. To move to another release: change the pin, test real attaches, update that file.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const VERSION = "3.2.0-1092";
const ASSET = "netcoredbg-win64.zip";
const SHA256 = "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a";
const URL = `https://github.com/Samsung/netcoredbg/releases/download/${VERSION}/${ASSET}`;
/** What the release must unpack to, so an unexpected file never reaches the .vsix. */
const FILES = [
  "ManagedPart.dll",
  "Microsoft.CodeAnalysis.CSharp.Scripting.dll",
  "Microsoft.CodeAnalysis.CSharp.dll",
  "Microsoft.CodeAnalysis.Scripting.dll",
  "Microsoft.CodeAnalysis.dll",
  "dbgshim.dll",
  "netcoredbg.exe",
];

const ext = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sha256 = (file) => createHash("sha256").update(fs.readFileSync(file)).digest("hex");

function checkZip(file) {
  const got = sha256(file);
  if (got !== SHA256) throw new Error(`${file}: sha256 ${got}, expected ${SHA256}`);
}

if (process.argv[2] === "--check-zip") {
  try {
    checkZip(process.argv[3]);
    console.log(`${process.argv[3]}: matches netcoredbg ${VERSION}`);
  } catch (e) {
    console.error(e.message);
    process.exit(1);
  }
  process.exit(0);
}

const out = path.join(process.env.KILN_OUT || path.resolve(ext, "..", "..", ".out", "vscode"), "netcoredbg", VERSION);
fs.mkdirSync(out, { recursive: true });
const zip = path.join(out, ASSET);

if (fs.existsSync(zip)) {
  try {
    checkZip(zip);
  } catch (e) {
    console.log(`${e.message}: fetching it again`);
    fs.rmSync(zip);
  }
}
if (!fs.existsSync(zip)) {
  console.log(`netcoredbg: ${URL}`);
  const res = await fetch(URL, { signal: AbortSignal.timeout(300_000) });
  if (!res.ok) throw new Error(`${URL}: HTTP ${res.status}`);
  const part = `${zip}.part`;
  fs.writeFileSync(part, Buffer.from(await res.arrayBuffer()));
  try {
    checkZip(part); // before anything reads it
  } catch (e) {
    fs.rmSync(part);
    throw e;
  }
  fs.renameSync(part, zip);
}

// unpack into a fresh folder; nothing in it is run here
const unpacked = path.join(out, "unpacked");
fs.rmSync(unpacked, { recursive: true, force: true });
fs.mkdirSync(unpacked);
if (process.platform === "win32") execFileSync("tar", ["-xf", zip, "-C", unpacked], { stdio: "inherit" });
else execFileSync("unzip", ["-q", zip, "-d", unpacked], { stdio: "inherit" });
const from = path.join(unpacked, "netcoredbg");
const found = fs.readdirSync(from).sort();
if (JSON.stringify(found) !== JSON.stringify([...FILES].sort())) throw new Error(`unexpected files in ${ASSET}: ${found.join(", ")}`);

// dist/ is ignored by git and already the extension's build output
const licence = path.resolve(ext, "..", "..", "licenses", "netcoredbg.txt");
const stage = path.join(ext, "dist", "netcoredbg");
fs.rmSync(stage, { recursive: true, force: true });
fs.mkdirSync(stage, { recursive: true });
for (const f of found) fs.copyFileSync(path.join(from, f), path.join(stage, f));
fs.copyFileSync(licence, path.join(stage, "LICENSES.txt"));
console.log(`netcoredbg ${VERSION}: sha256 ok, ${found.length} files staged in ${stage}`);
