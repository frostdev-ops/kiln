# SPDX-License-Identifier: MIT
"""Mods' hook modules for the harnesses: build them with kiln-vfxcook shader build, link them with shader link, as the game's cook would.

Needs glslangValidator for building (skipped without it); linking needs only kiln-vfxcook, which tools/vfxcook/native/build.sh builds.
"""
import json
import shutil
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[5]
HAVE_GLSLANG = shutil.which("glslangValidator") is not None


def tool():
    cached = getattr(tool, "path", None)
    if cached:
        return cached
    out = subprocess.run([str(ROOT / "tools/vfxcook/native/build.sh")], capture_output=True, text=True)
    if out.returncode:
        raise RuntimeError("kiln-vfxcook did not build:\n" + out.stderr[-4000:])
    tool.path = out.stdout.strip().splitlines()[-1]
    return tool.path


def link(hooks_spv):
    """The linked module for a hook module file, and the link report. Raises with the problem when it is refused."""
    with tempfile.TemporaryDirectory() as d:
        out = Path(d) / "linked.spv"
        p = subprocess.run([tool(), "shader", "link", str(hooks_spv), "--out", str(out)], capture_output=True, text=True)
        report = json.loads(p.stdout)
        if p.returncode:
            raise ValueError(report.get("problem", p.stderr))
        return out.read_bytes(), report


def build(source, name="hook"):
    """HLSL hook source -> (hook module bytes, build report)."""
    with tempfile.TemporaryDirectory() as d:
        src = Path(d) / "shaders"
        src.mkdir()
        (src / f"{name}.hlsl").write_text(source)
        p = subprocess.run([tool(), "shader", "build", "--mod", d], capture_output=True, text=True)
        report = json.loads(p.stdout)[0]
        spv = src / "build" / f"{name}.spv"
        return (spv.read_bytes() if spv.exists() else None), report


def build_and_link(source):
    code, report = build(source)
    if not report["ok"]:
        raise ValueError(report.get("problem"))
    with tempfile.TemporaryDirectory() as d:
        f = Path(d) / "h.spv"
        f.write_bytes(code)
        return link(f)
