#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for the static NuGet feed (tools/release/lib/feed.py): the service index and its absolute URLs, the flat
container (lower-case ids, normalised versions, the nupkg's bytes and its nuspec), the registration pages, several
source folders in one feed, deterministic output, and `check` (missing, changed and extra files).

  python3 -I tools/release/test_feed.py
Scratch files go to TMPDIR. A real client against the feed (dotnet new install, restore and build of a kiln-mod
project) is not run here: it needs packed SDK packages and a web server (see the api-gate report of dev.25).
"""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest
import zipfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("feed", HERE / "lib" / "feed.py")
feed = importlib.util.module_from_spec(spec)
spec.loader.exec_module(feed)

BASE = "http://127.0.0.1:27111/"


def nupkg(folder, pid, version, deps=(), types=("Dependency",)):
    """A minimal nupkg: <id>.nuspec at the root (in NuGet's namespace) and one file."""
    d = "".join(f'<dependency id="{i}" version="{v}" />' for i, v in deps)
    t = "".join(f'<packageType name="{n}" />' for n in types)
    nuspec = (f'<?xml version="1.0" encoding="utf-8"?>\n<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
              f"<metadata><id>{pid}</id><version>{version}</version><authors>Kiln contributors</authors>"
              f"<description>{pid} test</description><packageTypes>{t}</packageTypes>"
              f'<dependencies><group targetFramework="net10.0">{d}</group></dependencies></metadata></package>')
    path = os.path.join(folder, f"{pid}.{version}.nupkg")
    with zipfile.ZipFile(path, "w") as z:
        z.writestr(f"{pid}.nuspec", nuspec)
        z.writestr("lib/net10.0/x.txt", f"{pid} {version}")
        z.writestr("[Content_Types].xml", "<Types/>")
    return path


def run(*argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = feed.main(list(argv))
    return code, out.getvalue() + err.getvalue()


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="feed-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.sdk = self.tmp / "sdk"
        self.sdk.mkdir()
        self.out = self.sdk / "feed"
        nupkg(self.sdk, "Kiln.Api", "0.2.0-dev.9925")
        nupkg(self.sdk, "Kiln.Sdk", "0.2.0-dev.9925", deps=[("Kiln.Api", "0.2.0-dev.9925")], types=["MSBuildSdk"])
        nupkg(self.sdk, "Kiln.Templates", "0.2.0-dev.9925", types=["Template"])
        (self.sdk / "kiln").write_bytes(b"\x7fELF")

    def build(self, *sources, base=BASE):
        code, out = run("build", "--out", str(self.out), "--base-url", base, *(sources or [str(self.sdk)]))
        self.assertEqual(0, code, out)
        return out

    def json(self, rel):
        return json.loads((self.out / rel).read_text(encoding="utf-8"))


class Build(Base):
    def test_the_index_names_absolute_resources_under_the_base_url(self):
        self.build()
        index = self.json("index.json")
        self.assertEqual("3.0.0", index["version"])
        types = {r["@type"]: r["@id"] for r in index["resources"]}
        self.assertEqual(BASE + "flatcontainer/", types["PackageBaseAddress/3.0.0"])
        self.assertEqual(BASE + "registration/", types["RegistrationsBaseUrl/3.6.0"])
        self.assertTrue(all(r["@id"].startswith(BASE) for r in index["resources"]))

    def test_the_flat_container_is_lower_case_with_the_nupkg_and_nuspec(self):
        self.build()
        for pid in ("Kiln.Api", "Kiln.Sdk", "Kiln.Templates"):
            lid = pid.lower()
            self.assertEqual({"versions": ["0.2.0-dev.9925"]}, self.json(f"flatcontainer/{lid}/index.json"))
            pkg = self.out / f"flatcontainer/{lid}/0.2.0-dev.9925/{lid}.0.2.0-dev.9925.nupkg"
            self.assertEqual((self.sdk / f"{pid}.0.2.0-dev.9925.nupkg").read_bytes(), pkg.read_bytes())
            with zipfile.ZipFile(pkg) as z:
                self.assertEqual(z.read(f"{pid}.nuspec"), (pkg.parent / f"{lid}.nuspec").read_bytes())
        self.assertFalse((self.out / "flatcontainer" / "kiln").exists(), "only nupkgs")

    def test_versions_are_normalised_and_sorted_by_semver(self):
        for v in ("0.2.0-dev.10", "0.2.0-dev.9", "0.2.0", "01.0.0.0+abc", "0.2.0-DEV.11"):
            nupkg(self.sdk, "Kiln.Api", v)
        self.build()
        self.assertEqual(["0.2.0-dev.9", "0.2.0-dev.10", "0.2.0-dev.11", "0.2.0-dev.9925", "0.2.0", "1.0.0"],
                         self.json("flatcontainer/kiln.api/index.json")["versions"])
        self.assertTrue((self.out / "flatcontainer/kiln.api/1.0.0/kiln.api.1.0.0.nupkg").is_file())
        self.assertTrue((self.out / "flatcontainer/kiln.api/0.2.0-dev.11/kiln.api.0.2.0-dev.11.nupkg").is_file())

    def test_three_and_four_part_nuget_versions_sort_together(self):
        for v in ("0.2.0.2", "0.2.0.1", "0.2.0", "0.2.0.1-dev.1"):
            nupkg(self.sdk, "Kiln.Api", v)
        self.build()
        expected = ["0.2.0-dev.9925", "0.2.0", "0.2.0.1-dev.1", "0.2.0.1", "0.2.0.2"]
        self.assertEqual(expected, self.json("flatcontainer/kiln.api/index.json")["versions"])
        page = self.json("registration/kiln.api/index.json")["items"][0]
        self.assertEqual((expected[0], expected[-1]), (page["lower"], page["upper"]))
        self.assertEqual(expected, [e["catalogEntry"]["version"] for e in page["items"]])
        self.assertEqual(0, run("check", str(self.out), "--nupkgs", str(self.sdk))[0])

    def test_registration_pages_name_every_version_and_its_dependencies(self):
        self.build()
        reg = self.json("registration/kiln.sdk/index.json")
        page = reg["items"][0]
        self.assertEqual((1, 1), (reg["count"], page["count"]))
        self.assertEqual(("0.2.0-dev.9925", "0.2.0-dev.9925"), (page["lower"], page["upper"]))
        entry = page["items"][0]["catalogEntry"]
        self.assertEqual(("Kiln.Sdk", "0.2.0-dev.9925"), (entry["id"], entry["version"]))
        self.assertEqual(BASE + "flatcontainer/kiln.sdk/0.2.0-dev.9925/kiln.sdk.0.2.0-dev.9925.nupkg", entry["packageContent"])
        self.assertEqual([{"targetFramework": "net10.0", "dependencies": [{"id": "Kiln.Api", "range": "0.2.0-dev.9925"}]}],
                         entry["dependencyGroups"])
        leaf = self.json("registration/kiln.sdk/0.2.0-dev.9925.json")
        self.assertEqual(BASE + "registration/kiln.sdk/index.json", leaf["registration"])

    def test_search_is_one_static_answer_with_every_package(self):
        # dotnet new install asks the search service for a package's metadata (owners, verified) before it installs
        self.build()
        types = {r["@type"]: r["@id"] for r in self.json("index.json")["resources"]}
        self.assertEqual(BASE + "query", types["SearchQueryService/3.5.0"])
        res = self.json("query")
        self.assertEqual(3, res["totalHits"])
        hit = {h["id"]: h for h in res["data"]}["Kiln.Templates"]
        self.assertEqual(("0.2.0-dev.9925", [{"name": "Template"}]), (hit["version"], hit["packageTypes"]))
        self.assertEqual([BASE + "registration/kiln.templates/0.2.0-dev.9925.json"], [v["@id"] for v in hit["versions"]])
        self.assertEqual(BASE + "registration/kiln.templates/index.json", hit["registration"])

    def test_several_folders_make_one_feed(self):
        old = self.tmp / "old"
        old.mkdir()
        nupkg(old, "Kiln.Api", "0.2.0-dev.24")
        shutil.copy(self.sdk / "Kiln.Api.0.2.0-dev.9925.nupkg", old)  # the same package in both: fine
        self.build(str(self.sdk), str(old))
        self.assertEqual(["0.2.0-dev.24", "0.2.0-dev.9925"], self.json("flatcontainer/kiln.api/index.json")["versions"])

    def test_one_version_with_different_bytes_is_refused(self):
        other = self.tmp / "other"
        other.mkdir()
        p = nupkg(other, "Kiln.Api", "0.2.0-dev.9925", deps=[("X", "1.0")])
        code, out = run("build", "--out", str(self.out), "--base-url", BASE, str(self.sdk), p)
        self.assertEqual(2, code)
        self.assertIn("twice with different bytes", out)

    def test_the_same_input_gives_the_same_bytes(self):
        self.build()
        first = {p: p.read_bytes() for p in self.out.rglob("*") if p.is_file()}
        self.build()
        self.assertEqual(first, {p: p.read_bytes() for p in self.out.rglob("*") if p.is_file()})
        self.assertEqual(17, len(first))

    def test_a_relative_or_missing_base_url_is_refused(self):
        for bad in ("", "feed/", "ftp://x/", "https://x/?q=1"):
            code, out = run("build", "--out", str(self.out), "--base-url", bad, str(self.sdk))
            self.assertEqual(2, code, bad)
            self.assertIn("absolute http(s) URL", out)
        self.build(base="https://frostdev.io/kiln/downloads/0.2.0-dev.9925/sdk/feed")  # the slash is added
        self.assertEqual("https://frostdev.io/kiln/downloads/0.2.0-dev.9925/sdk/feed/flatcontainer/",
                         self.json("index.json")["resources"][0]["@id"])

    def test_a_folder_that_is_not_a_feed_is_not_replaced(self):
        self.out.mkdir()
        (self.out / "keep.txt").write_text("mine")
        code, out = run("build", "--out", str(self.out), "--base-url", BASE, str(self.sdk))
        self.assertEqual(2, code)
        self.assertTrue((self.out / "keep.txt").is_file())


class Check(Base):
    def test_a_fresh_feed_passes(self):
        self.build()
        code, out = run("check", str(self.out), "--nupkgs", str(self.sdk), "--base-url", BASE)
        self.assertEqual(0, code, out)
        self.assertIn("3 packages, 17 files match", out)

    def test_a_missing_or_changed_file_fails_and_is_named(self):
        self.build()
        (self.out / "flatcontainer/kiln.sdk/index.json").unlink()
        pkg = self.out / "flatcontainer/kiln.api/0.2.0-dev.9925/kiln.api.0.2.0-dev.9925.nupkg"
        pkg.write_bytes(pkg.read_bytes() + b"x")
        code, out = run("check", str(self.out), "--nupkgs", str(self.sdk))
        self.assertEqual(1, code)
        self.assertIn("flatcontainer/kiln.sdk/index.json: missing", out)
        self.assertIn("kiln.api.0.2.0-dev.9925.nupkg: not the bytes of", out)

    def test_a_nupkg_newer_than_the_feed_fails(self):
        self.build()
        nupkg(self.sdk, "Kiln.Api", "0.2.0-dev.9926")
        code, out = run("check", str(self.out), "--nupkgs", str(self.sdk))
        self.assertEqual(1, code)
        self.assertIn("flatcontainer/kiln.api/index.json: differs", out)
        self.assertIn("kiln.api.0.2.0-dev.9926.nupkg: missing", out)

    def test_an_extra_file_or_another_base_url_fails(self):
        self.build()
        (self.out / "flatcontainer/stray.json").write_text("{}")
        code, out = run("check", str(self.out), "--nupkgs", str(self.sdk), "--base-url", "https://example.invalid/feed/")
        self.assertEqual(1, code)
        self.assertIn("flatcontainer/stray.json: not from a nupkg", out)
        self.assertIn("expected https://example.invalid/feed/", out)

    def test_no_index_fails(self):
        code, out = run("check", str(self.out), "--nupkgs", str(self.sdk))
        self.assertEqual(1, code)
        self.assertIn("index.json", out)


if __name__ == "__main__":
    unittest.main()
