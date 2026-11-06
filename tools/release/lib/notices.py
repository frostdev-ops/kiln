#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""THIRD-PARTY-NOTICES.txt for a Kiln release, from what the build actually uses:

  - Rust crates: the normal (not build or dev) dependency graph of kiln-launcher, kilnctl and kiln-cli for Linux and
    Windows (cargo metadata), with each crate's own licence files;
  - npm: the production dependencies of the launcher and the VS Code extension (from their node_modules);
  - NuGet: the packages the shipped .NET projects reference (from their nuspec);
  - the .NET runtime Kiln ships (its LICENSE.txt and ThirdPartyNotices.txt);
  - vendored code (native/third_party), the fonts, licenses/ (CREDITS.md);
  - optionally the system libraries bundled into the Linux AppImage (--appimage-root: the unpacked AppImage; owners
    and licences from the system package manager, pacman or dpkg).

  notices.py --src <export> --out FILE [--dotnet-root DIR] [--appimage-root DIR] [--nuget-assets FILE]...
"""
import argparse
import glob
import hashlib
import json
import os
import re
import subprocess
import sys

LICENSE_FILE = re.compile(r"(?i)^(licen[cs]e|copying|notice|copyright|unlicense|authors)([-._].*)?(\.(md|txt|rst|mit|apache|bsd))?$")
TARGETS = ["x86_64-unknown-linux-gnu", "x86_64-pc-windows-msvc"]
ROOTS = ["kiln-launcher", "kilnctl", "kiln-cli"]


def read(p):
    with open(p, encoding="utf-8", errors="replace") as f:
        return f.read().replace("\r\n", "\n").strip()


def license_files(d, depth=0):
    out = []
    try:
        names = sorted(os.listdir(d))
    except OSError:
        return out
    for n in names:
        p = os.path.join(d, n)
        if os.path.isfile(p) and LICENSE_FILE.match(n):
            out.append(p)
    return out


def spdx_texts(expr):
    """Standard texts for the SPDX ids in a licence expression, where the system has them."""
    out = []
    for i in re.split(r"[\s/()]+|\bOR\b|\bAND\b|\bWITH\b", expr or ""):
        p = f"/usr/share/licenses/spdx/{i}.txt"
        if i == "MIT":
            out.append(("MIT.txt", MIT))
        elif i and os.path.isfile(p) and (f"{i}.txt", read(p)) not in out:
            out.append((f"{i}.txt", read(p)))
    return out


MIT = """MIT License

Copyright (c) the package's authors (see its source)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."""


class Notices:
    def __init__(self):
        self.sections = []  # (title, [entries]); entry = (name, version, license, url, texts[(file, text)])
        self.texts = {}  # sha -> text (shared licence texts are printed once)

    def add(self, title, entries):
        self.sections.append((title, entries))

    def render(self, header):
        out = [header.rstrip(), ""]
        for title, entries in self.sections:
            if not entries:
                continue
            out += ["=" * 100, title, "=" * 100, ""]
            for name, version, lic, url, texts in entries:
                head = f"{name} {version}".strip()
                out.append(f"--- {head}")
                if lic:
                    out.append(f"License: {lic}")
                if url:
                    out.append(f"Source: {url}")
                for fname, text in texts:
                    sha = hashlib.sha256(text.encode()).hexdigest()[:12]
                    if sha in self.texts:
                        out.append(f"({fname}: the text of [{sha}] above)")
                    else:
                        self.texts[sha] = text
                        out += [f"[{sha}] {fname}:", "", text, ""]
                if not texts:
                    std = spdx_texts(lic)
                    out.append("(no licence file in the package; the licence named above applies"
                               + (", standard text below)" if std else ")"))
                    for fname, text in std:
                        sha = hashlib.sha256(text.encode()).hexdigest()[:12]
                        if sha in self.texts:
                            out.append(f"({fname}: the text of [{sha}] above)")
                        else:
                            self.texts[sha] = text
                            out += [f"[{sha}] {fname}:", "", text, ""]
                out.append("")
        return "\n".join(out).rstrip() + "\n"


def cargo_crates(src):
    pkgs = {}
    for target in TARGETS:
        seen = set()
        meta = json.loads(subprocess.check_output(
            ["cargo", "metadata", "--format-version", "1", "--locked", "--filter-platform", target],
            cwd=src, stderr=subprocess.DEVNULL))
        by_id = {p["id"]: p for p in meta["packages"]}
        nodes = {n["id"]: n for n in meta["resolve"]["nodes"]}
        members = set(meta["workspace_members"])
        stack = [i for i in members if by_id[i]["name"] in ROOTS]
        while stack:
            i = stack.pop()
            if i in seen:
                continue
            seen.add(i)
            for d in nodes[i]["deps"]:
                if any(k["kind"] is None for k in d["dep_kinds"]):
                    stack.append(d["pkg"])
        for i in seen:
            if i not in members:
                pkgs[i] = by_id[i]
    entries = []
    for p in sorted(pkgs.values(), key=lambda p: (p["name"], p["version"])):
        d = os.path.dirname(p["manifest_path"])
        files = license_files(d)
        if p.get("license_file"):
            lf = os.path.join(d, p["license_file"])
            if os.path.isfile(lf) and lf not in files:
                files.append(lf)
        entries.append((p["name"], p["version"], p.get("license") or "see the licence file", p.get("repository") or "",
                        [(os.path.basename(f), read(f)) for f in files]))
    return entries


def npm_closure(pkg_dir):
    root = json.load(open(os.path.join(pkg_dir, "package.json")))
    found = {}

    def resolve(name, frm):
        d = frm
        while True:
            cand = os.path.join(d, "node_modules", name)
            if os.path.isfile(os.path.join(cand, "package.json")):
                return cand
            if os.path.abspath(d) == os.path.abspath(pkg_dir):
                return None
            parent = os.path.dirname(d)
            # walk up node_modules/<pkg>/node_modules/... towards the root
            d = parent if os.path.basename(parent) != "node_modules" else os.path.dirname(parent)
            if not d.startswith(os.path.abspath(pkg_dir)):
                return None

    stack = [(n, os.path.abspath(pkg_dir)) for n in (root.get("dependencies") or {})]
    while stack:
        name, frm = stack.pop()
        p = resolve(name, frm)
        if not p or p in found:
            continue
        pj = json.load(open(os.path.join(p, "package.json")))
        found[p] = pj
        for n in list((pj.get("dependencies") or {})) + list((pj.get("optionalDependencies") or {})):
            stack.append((n, p))
    entries = []
    for p, pj in sorted(found.items(), key=lambda kv: (kv[1].get("name", ""), kv[1].get("version", ""))):
        lic = pj.get("license")
        if isinstance(lic, dict):
            lic = lic.get("type")
        repo = pj.get("repository")
        if isinstance(repo, dict):
            repo = repo.get("url")
        entries.append((pj.get("name", os.path.basename(p)), pj.get("version", ""), lic or "", repo or pj.get("homepage", ""),
                        [(os.path.basename(f), read(f)) for f in license_files(p)]))
    return entries


def nuget_entries(assets_files):
    cache = os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages")
    libs = {}
    for a in assets_files:
        try:
            data = json.load(open(a))
        except (OSError, ValueError):
            continue
        for key, lib in (data.get("libraries") or {}).items():
            if lib.get("type") == "package":
                libs[key] = lib
    entries = []
    for key in sorted(libs):
        name, ver = key.split("/", 1)
        d = os.path.join(cache, name.lower(), ver.lower())
        nuspec = next(iter(glob.glob(os.path.join(d, "*.nuspec"))), None)
        lic, url, texts = "", "", []
        if nuspec:
            x = read(nuspec)
            m = re.search(r"<license type=\"expression\">([^<]+)</license>", x)
            if m:
                lic = m.group(1)
            m = re.search(r"<license type=\"file\">([^<]+)</license>", x)
            if m and os.path.isfile(os.path.join(d, m.group(1))):
                texts.append((m.group(1), read(os.path.join(d, m.group(1)))))
            m = re.search(r"<projectUrl>([^<]+)</projectUrl>", x)
            url = m.group(1) if m else ""
            if not lic:
                m = re.search(r"<licenseUrl>([^<]+)</licenseUrl>", x)
                lic = m.group(1) if m else ""
        for f in license_files(d):
            if os.path.basename(f) not in [t[0] for t in texts]:
                texts.append((os.path.basename(f), read(f)))
        entries.append((name, ver, lic, url, texts))
    return entries


def tree_entries(items):
    """items: (name, version, license, url, [paths])"""
    return [(n, v, l, u, [(os.path.basename(p), read(p)) for p in ps if os.path.isfile(p)]) for n, v, l, u, ps in items]


def appimage_libs(root):
    libs = sorted(set(os.path.realpath(p) for p in glob.glob(os.path.join(root, "usr", "lib", "**", "*.so*"), recursive=True)
                      if os.path.isfile(p)))
    owners = {}
    system = {}  # basename -> path under /usr/lib (one walk, not one search per library)
    for d, _, files in os.walk("/usr/lib"):
        for n in files:
            system.setdefault(n, os.path.join(d, n))
    for lib in libs:
        base = os.path.basename(lib)
        sys_path = system.get(base)
        pkg = None
        if sys_path:
            for cmd in (["pacman", "-Qqo", sys_path], ["dpkg", "-S", sys_path]):
                try:
                    out = subprocess.check_output(cmd, stderr=subprocess.DEVNULL, text=True).strip()
                    pkg = out.split(":")[0].strip() if cmd[0] == "dpkg" else out.splitlines()[0]
                    break
                except (OSError, subprocess.CalledProcessError):
                    continue
        owners.setdefault(pkg or "(unknown package)", []).append(base)
    entries = []
    for pkg, files in sorted(owners.items()):
        ver, lic, url, texts = "", "", "", []
        if not pkg.startswith("("):
            try:
                info = subprocess.check_output(["pacman", "-Qi", pkg], stderr=subprocess.DEVNULL, text=True)
                f = dict((l.split(":", 1)[0].strip(), l.split(":", 1)[1].strip()) for l in info.splitlines() if ":" in l)
                ver, lic, url = f.get("Version", ""), f.get("Licenses", ""), f.get("URL", "")
            except (OSError, subprocess.CalledProcessError):
                pass
            for d in (f"/usr/share/licenses/{pkg}", f"/usr/share/doc/{pkg}"):
                for p in license_files(d):
                    texts.append((os.path.basename(p), read(p)))
            for spdx in re.split(r"[ ,()]+", lic):
                p = f"/usr/share/licenses/spdx/{spdx}.txt"
                if spdx and os.path.isfile(p) and len(texts) < 6:
                    texts.append((f"{spdx}.txt", read(p)))
        entries.append((pkg, ver, lic, url, [("files", "Bundled: " + ", ".join(files))] + texts))
    return entries


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--version", default="")
    ap.add_argument("--dotnet-root")
    ap.add_argument("--appimage-root")
    ap.add_argument("--nuget-assets", action="append", default=[])
    a = ap.parse_args()
    src = os.path.abspath(a.src)
    n = Notices()
    n.add("Rust crates (the launcher, kilnctl and the kiln CLI; Linux and Windows)", cargo_crates(src))
    if os.path.isdir(os.path.join(src, "launcher", "node_modules")):
        n.add("npm packages (the launcher's user interface)", npm_closure(os.path.join(src, "launcher")))
    if os.path.isdir(os.path.join(src, "tools", "vscode", "node_modules")):
        n.add("npm packages (the VS Code extension)", npm_closure(os.path.join(src, "tools", "vscode")))
    n.add("NuGet packages (referenced by the Kiln SDK and the runtime's .NET projects)", nuget_entries(a.nuget_assets))
    tp = os.path.join(src, "native", "third_party")
    n.add("Vendored code (native/third_party, compiled into Kiln's DLLs)", tree_entries(
        [(d, "", "", "", sorted(license_files(os.path.join(tp, d)))) for d in sorted(os.listdir(tp)) if os.path.isdir(os.path.join(tp, d))]))
    fonts = os.path.join(src, "launcher", "src", "assets", "fonts")
    n.add("Fonts (the launcher)", tree_entries(
        [(os.path.basename(p)[:-8], "", "OFL-1.1", "https://github.com/google/fonts", [p]) for p in sorted(glob.glob(os.path.join(fonts, "*-OFL.txt")))]))
    lic = os.path.join(src, "licenses")
    n.add("Credited projects (CREDITS.md; netcoredbg ships in the VS Code extension)", tree_entries(
        [(os.path.basename(p).rsplit(".", 1)[0], "", "", "", [p]) for p in sorted(glob.glob(os.path.join(lic, "*")))]))
    if a.dotnet_root:
        n.add(".NET runtime (kiln/dotnet in the runtime)", tree_entries(
            [(".NET runtime", "", "MIT", "https://github.com/dotnet/runtime",
              [os.path.join(a.dotnet_root, "LICENSE.txt"), os.path.join(a.dotnet_root, "ThirdPartyNotices.txt")])]))
    if a.appimage_root:
        n.add("System libraries bundled into the Linux AppImage (from the build machine's packages; LGPL libraries can be "
              "replaced: extract the AppImage with --appimage-extract and swap the .so files in usr/lib)", appimage_libs(a.appimage_root))
    n.add("Installer", [("NSIS (Nullsoft Scriptable Install System)", "", "zlib/libpng; parts bzip2", "https://nsis.sourceforge.io",
                         [("note", "The Windows setup program is built with NSIS through Tauri's bundler.")])])
    header = f"""Kiln {a.version} third-party notices

Kiln is MIT licensed (LICENSE). It includes or is built with the third-party software below, each under its own
licence. A licence text shared by several packages is printed once, at its first use, and referred to by its [id]
after that. CREDITS.md says what each credited project is used for."""
    text = n.render(header)
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        f.write(text)
    counts = ", ".join(f"{len(e)} {t.split(' (')[0]}" for t, e in n.sections if e)
    print(f"notices: {a.out}: {counts}; {len(n.texts)} licence texts")


if __name__ == "__main__":
    main()
