#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A static NuGet v3 feed over the SDK's nupkgs (a release's sdk/feed/), so `dotnet new install`, a mod's nuget.config
and the MSBuild SDK resolver (<Project Sdk="Kiln.Sdk/<version>">) can use one URL once the folder is served by a plain
web server. Standard library only.

  feed.py build --out DIR [--base-url URL] SOURCE...   SOURCE: a nupkg or a folder of them (its top level); several
                                                       folders make one feed over several releases
  feed.py check DIR --nupkgs FOLDER [--base-url URL]   the feed holds exactly FOLDER's nupkgs, byte for byte

URL: where DIR will be served, absolute (a v3 index names its resources by absolute URL); default KILN_FEED_URL. The
folder (every name lower case, versions normalised as NuGet does: no build metadata, no 4th part when 0):
  index.json                                  the service index: PackageBaseAddress/3.0.0, RegistrationsBaseUrl/*
  flatcontainer/<id>/index.json               {"versions": [...]}: what restore and the SDK resolver list
  flatcontainer/<id>/<ver>/<id>.<ver>.nupkg   the package (a copy)
  flatcontainer/<id>/<ver>/<id>.nuspec        its nuspec
  registration/<id>/index.json                the registration index, one inline page (package metadata)
  registration/<id>/<ver>.json                a registration leaf
  query                                       SearchQueryService: one static answer listing every package (a web
                                              server serves the file whatever the query string)
What .NET SDK 10.0.1xx asked for (dev.25, a served folder; templates installed, a kiln-mod project restored and built):
  dotnet new install Kiln.Templates@V: index.json, registration/kiln.templates/index.json, query (refused without a
    search service: "The source does not have a Search service"), flatcontainer/kiln.templates/index.json + nupkg;
    over plain http only with --force (it skips insecure sources, allowInsecureConnections or not)
  restore + build (<Project Sdk="Kiln.Sdk/V">: the SDK resolver, then Kiln.Api): flatcontainer/<id>/index.json + nupkg
  Never asked for: the .nuspec files and the registration leaves (kept: they are part of the protocol).
Deterministic: the same nupkgs and URL give the same bytes (no dates unless SOURCE_DATE_EPOCH is set).
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import time
import xml.etree.ElementTree as ET
import zipfile

DEFAULT_URL = os.environ.get("KILN_FEED_URL") or ""


class Fail(Exception):
    pass


def normalize(version):
    """NuGet's normalised version text: no +metadata, numeric parts without leading zeros, a 4th part only when it is
    not 0, the pre-release part as it is; lower case (flat container paths are lower case)."""
    v = version.strip().split("+", 1)[0]
    core, dash, pre = v.partition("-")
    parts = core.split(".")
    if not 1 <= len(parts) <= 4 or not all(p.isdigit() for p in parts):
        raise Fail(f"not a NuGet version: {version!r}")
    nums = [int(p) for p in parts] + [0] * (3 - len(parts))
    if len(nums) == 4 and nums[3] == 0:
        nums = nums[:3]
    if dash and not re.fullmatch(r"[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*", pre):
        raise Fail(f"not a NuGet version: {version!r}")
    return (".".join(str(n) for n in nums) + (dash + pre if dash else "")).lower()


def sort_key(version):
    """SemVer 2 precedence of a normalised version."""
    core, _, pre = version.partition("-")
    nums = tuple(int(p) for p in core.split("."))
    if not pre:
        return nums + ((1,),)
    ids = tuple((0, int(x), "") if x.isdigit() else (1, 0, x) for x in pre.split("."))
    return nums + ((0,) + ids,)


def local(tag):
    return tag.rsplit("}", 1)[-1]


def read_nupkg(path):
    """(id, version text, normalised version, nuspec bytes, metadata dict)."""
    try:
        with zipfile.ZipFile(path) as z:
            specs = [n for n in z.namelist() if "/" not in n and n.lower().endswith(".nuspec")]
            if len(specs) != 1:
                raise Fail(f"{path}: {len(specs)} nuspec files at the root, expected one")
            spec = z.read(specs[0])
    except zipfile.BadZipFile as e:
        raise Fail(f"{path}: not a nupkg ({e})") from None
    root = ET.fromstring(spec)
    md = next((c for c in root if local(c.tag) == "metadata"), None)
    if md is None:
        raise Fail(f"{path}: the nuspec has no <metadata>")
    fields = {local(c.tag): (c.text or "").strip() for c in md}
    pid, ver = fields.get("id"), fields.get("version")
    if not pid or not ver:
        raise Fail(f"{path}: the nuspec has no id or version")
    if not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]*", pid):
        raise Fail(f"{path}: unsafe package id {pid!r}")
    types = []
    pts = next((c for c in md if local(c.tag) == "packageTypes"), None)
    if pts is not None:
        types = [x.get("name") for x in pts if local(x.tag) == "packageType" and x.get("name")]
    groups = []
    deps = next((c for c in md if local(c.tag) == "dependencies"), None)
    if deps is not None:
        loose = [d for d in deps if local(d.tag) == "dependency"]
        if loose:
            groups.append(("", loose))
        for g in deps:
            if local(g.tag) == "group":
                groups.append((g.get("targetFramework", ""), [d for d in g if local(d.tag) == "dependency"]))
    meta = {
        "authors": fields.get("authors", ""),
        "description": fields.get("description", ""),
        "projectUrl": fields.get("projectUrl", ""),
        "licenseUrl": fields.get("licenseUrl", ""),
        "tags": fields.get("tags", ""),
        "title": fields.get("title", ""),
        "summary": fields.get("summary", ""),
        "packageTypes": types,
        "dependencyGroups": [
            {**({"targetFramework": tf} if tf else {}),
             "dependencies": [{"id": d.get("id"), "range": d.get("version") or ""} for d in ds if d.get("id")]}
            for tf, ds in groups
        ],
    }
    return pid, ver, normalize(ver), spec, meta


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def collect(sources):
    """{(lower id, normalised version): (path, id, version, nuspec, meta)}"""
    out = {}
    for s in sources:
        if os.path.isdir(s):
            paths = sorted(os.path.join(s, n) for n in os.listdir(s)
                           if n.lower().endswith(".nupkg") and not n.lower().endswith(".symbols.nupkg")
                           and os.path.isfile(os.path.join(s, n)))
        elif os.path.isfile(s):
            paths = [s]
        else:
            raise Fail(f"no such nupkg or folder: {s}")
        for p in paths:
            pid, ver, norm, spec, meta = read_nupkg(p)
            key = (pid.lower(), norm)
            if key in out and sha256(out[key][0]) != sha256(p):
                raise Fail(f"{pid} {norm} twice with different bytes: {out[key][0]} and {p}")
            out.setdefault(key, (p, pid, ver, spec, meta))
    if not out:
        raise Fail("no nupkgs in " + ", ".join(sources))
    return out


def base_url(url):
    if not re.fullmatch(r"https?://[^/\s?#]+(/[^\s?#]*)?", url or ""):
        raise Fail(f"the feed's base URL must be an absolute http(s) URL without query: {url!r} (--base-url or KILN_FEED_URL)")
    return url if url.endswith("/") else url + "/"


def index_doc(base):
    reg = base + "registration/"
    return {
        "version": "3.0.0",
        "resources": [
            {"@id": base + "flatcontainer/", "@type": "PackageBaseAddress/3.0.0",
             "comment": "Package versions and contents: <id>/index.json, <id>/<version>/<id>.<version>.nupkg"},
            {"@id": reg, "@type": "RegistrationsBaseUrl", "comment": "Package metadata"},
            {"@id": reg, "@type": "RegistrationsBaseUrl/3.0.0-beta", "comment": "Package metadata"},
            {"@id": reg, "@type": "RegistrationsBaseUrl/3.0.0-rc", "comment": "Package metadata"},
            {"@id": reg, "@type": "RegistrationsBaseUrl/3.4.0", "comment": "Package metadata (gzip not used)"},
            {"@id": reg, "@type": "RegistrationsBaseUrl/3.6.0", "comment": "Package metadata (SemVer 2.0.0)"},
        ] + [
            # one static answer, whatever the query: every package (a plain web server ignores the query string)
            {"@id": base + "query", "@type": t, "comment": "Search: every package of this feed, whatever is asked"}
            for t in ("SearchQueryService", "SearchQueryService/3.0.0-beta", "SearchQueryService/3.0.0-rc",
                      "SearchQueryService/3.5.0")
        ],
    }


def published():
    epoch = os.environ.get("SOURCE_DATE_EPOCH")
    if epoch and epoch.isdigit():
        return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(int(epoch)))
    return None


def plan(pkgs, base):
    """{relative path: bytes or ("copy", source path)} for the whole feed."""
    files = {"index.json": dump(index_doc(base))}
    by_id = {}
    for (lid, norm), entry in pkgs.items():
        by_id.setdefault(lid, []).append((norm, entry))
    when = published()
    search = []
    for lid, entries in sorted(by_id.items()):
        entries.sort(key=lambda e: sort_key(e[0]))
        versions = [n for n, _ in entries]
        files[f"flatcontainer/{lid}/index.json"] = dump({"versions": versions})
        reg_index = f"{base}registration/{lid}/index.json"
        leaves = []
        for norm, (path, pid, ver, spec, meta) in entries:
            files[f"flatcontainer/{lid}/{norm}/{lid}.{norm}.nupkg"] = ("copy", path)
            files[f"flatcontainer/{lid}/{norm}/{lid}.nuspec"] = spec
            content = f"{base}flatcontainer/{lid}/{norm}/{lid}.{norm}.nupkg"
            leaf_url = f"{base}registration/{lid}/{norm}.json"
            entry = {"@id": leaf_url + "#catalog", "@type": "PackageDetails", "id": pid, "version": norm,
                     "listed": True, "packageContent": content}
            entry.update({k: v for k, v in meta.items() if v and k != "packageTypes"})
            if when:
                entry["published"] = when
            leaf = {"@id": leaf_url, "@type": "Package", "catalogEntry": entry, "packageContent": content,
                    "registration": reg_index, "listed": True}
            if when:
                leaf["published"] = when
            files[f"registration/{lid}/{norm}.json"] = dump(leaf)
            leaves.append({k: leaf[k] for k in ("@id", "@type", "catalogEntry", "packageContent", "registration")})
        page = {"@id": reg_index + "#page", "@type": "catalog:CatalogPage", "count": len(leaves),
                "lower": versions[0], "upper": versions[-1], "parent": reg_index, "items": leaves}
        files[f"registration/{lid}/index.json"] = dump({"@id": reg_index, "@type": ["catalog:CatalogRoot", "PackageRegistration"],
                                                        "count": 1, "items": [page]})
        newest_norm, (_, pid, _, _, meta) = entries[-1]
        hit = {"@id": reg_index, "@type": "Package", "registration": reg_index, "id": pid, "version": newest_norm,
               "description": meta["description"], "summary": meta["summary"], "title": meta["title"] or pid,
               "authors": [a.strip() for a in meta["authors"].split(",") if a.strip()],
               "tags": meta["tags"].split(), "totalDownloads": 0, "verified": False,
               "packageTypes": [{"name": n} for n in meta["packageTypes"]],
               "versions": [{"version": n, "downloads": 0, "@id": f"{base}registration/{lid}/{n}.json"} for n in versions]}
        if meta["projectUrl"]:
            hit["projectUrl"] = meta["projectUrl"]
        search.append(hit)
    files["query"] = dump({"totalHits": len(search), "data": search})
    return files


def dump(doc):
    return (json.dumps(doc, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def cmd_build(a):
    base = base_url(a.base_url)
    pkgs = collect(a.sources)
    files = plan(pkgs, base)
    out = os.path.abspath(a.out)
    if os.path.exists(out):
        if not os.path.isdir(out):
            raise Fail(f"{out} is not a folder")
        if os.listdir(out) and not os.path.isfile(os.path.join(out, "index.json")):
            raise Fail(f"{out} is not empty and not a feed: refusing to replace it")
        shutil.rmtree(out)
    for rel, data in sorted(files.items()):
        p = os.path.join(out, *rel.split("/"))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        if isinstance(data, tuple):
            shutil.copyfile(data[1], p)
        else:
            with open(p, "wb") as f:
                f.write(data)
    ids = sorted({pid for (_, _), (_, pid, _, _, _) in pkgs.items()})
    print(f"feed: {len(pkgs)} packages ({', '.join(ids)}), {len(files)} files in {out}; served at {base}index.json")
    return 0


def cmd_check(a):
    feed = os.path.abspath(a.dir)
    problems = []
    try:
        with open(os.path.join(feed, "index.json"), encoding="utf-8") as f:
            index = json.load(f)
    except (OSError, ValueError) as e:
        print(f"feed: {feed}/index.json: {e}", file=sys.stderr)
        return 1
    res = {r.get("@type"): r.get("@id") for r in index.get("resources", []) if isinstance(r, dict)}
    flat = res.get("PackageBaseAddress/3.0.0")
    if index.get("version") != "3.0.0" or not flat:
        problems.append("index.json: not a v3 index with PackageBaseAddress/3.0.0")
    base = flat[: -len("flatcontainer/")] if flat and flat.endswith("/flatcontainer/") else None
    if flat and base is None:
        problems.append(f"index.json: PackageBaseAddress {flat} does not end in /flatcontainer/")
    if a.base_url and base and base != base_url(a.base_url):
        problems.append(f"index.json: served at {base}, expected {base_url(a.base_url)}")
    pkgs = collect([a.nupkgs])
    if base:
        want = plan(pkgs, base)
        have = set()
        for d, _, names in os.walk(feed):
            for n in names:
                have.add(os.path.relpath(os.path.join(d, n), feed).replace(os.sep, "/"))
        for rel, data in sorted(want.items()):
            p = os.path.join(feed, *rel.split("/"))
            if rel not in have:
                problems.append(f"{rel}: missing")
            elif isinstance(data, tuple):
                if sha256(p) != sha256(data[1]):
                    problems.append(f"{rel}: not the bytes of {data[1]}")
            else:
                with open(p, "rb") as f:
                    same = f.read() == data
                if not same:
                    problems.append(f"{rel}: differs from what the nupkgs give")
        for rel in sorted(have - set(want)):
            problems.append(f"{rel}: not from a nupkg in {a.nupkgs}")
    for p in problems:
        print(f"feed: {p}", file=sys.stderr)
    if problems:
        return 1
    print(f"feed: {len(pkgs)} packages, {len(want)} files match {a.nupkgs} (served at {base})")
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("build")
    s.add_argument("--out", required=True)
    s.add_argument("--base-url", default=DEFAULT_URL)
    s.add_argument("sources", nargs="+")
    s.set_defaults(fn=cmd_build)
    s = sub.add_parser("check")
    s.add_argument("dir")
    s.add_argument("--nupkgs", required=True)
    s.add_argument("--base-url", default="")
    s.set_defaults(fn=cmd_check)
    a = p.parse_args(argv)
    try:
        return a.fn(a)
    except (Fail, ET.ParseError) as e:
        print(f"feed: {e}", file=sys.stderr)
        return 2 if isinstance(e, Fail) else 1


if __name__ == "__main__":
    sys.exit(main())
