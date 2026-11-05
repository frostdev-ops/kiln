# SPDX-License-Identifier: MIT
"""kiln-soundcook, the native helper (tools/soundcook/native): find it, build it on demand, run it."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
BUILD_SH = HERE.parent / "native" / "build.sh"
EXE = "kiln-soundcook.exe" if os.name == "nt" else "kiln-soundcook"


class NativeError(Exception):
    pass


def _out_root():
    return Path(os.environ["KILN_OUT"]) if os.environ.get("KILN_OUT") else REPO / ".out" / "soundcook"


def candidates():
    c = []
    if os.environ.get("KILN_SOUNDCOOK"):
        c.append(Path(os.environ["KILN_SOUNDCOOK"]))
    c.append(_out_root() / "soundcook" / "bin" / EXE)
    c.append(REPO / ".out" / "soundcook" / "soundcook" / "bin" / EXE)
    w = shutil.which("kiln-soundcook")
    if w:
        c.append(Path(w))
    return c


def _stale(exe):
    """True when a source file is newer than the binary (a developer edited the helper)."""
    try:
        t = exe.stat().st_mtime
        srcs = list((HERE.parent / "native" / "src").glob("*")) + [BUILD_SH]
        return any(s.stat().st_mtime > t for s in srcs)
    except OSError:
        return False


def build(log=print):
    if os.name == "nt" or not BUILD_SH.exists():
        raise NativeError("kiln-soundcook is not built and cannot be built here (needs tools/soundcook/native/build.sh and cc)")
    if not (shutil.which(os.environ.get("CC", "cc")) or shutil.which("gcc") or shutil.which("clang")):
        raise NativeError("kiln-soundcook is not built and no C compiler (cc) was found")
    log("soundcook: building kiln-soundcook (tools/soundcook/native/build.sh)")
    env = dict(os.environ, KILN_OUT=str(_out_root()))
    r = subprocess.run(["bash", str(BUILD_SH)], env=env, capture_output=True, text=True)
    if r.returncode != 0:
        raise NativeError("building kiln-soundcook failed:\n" + r.stdout + r.stderr)
    p = Path(r.stdout.strip().splitlines()[-1])
    if not p.exists():
        raise NativeError(f"build.sh reported {p}, which does not exist")
    return p


class Native:
    def __init__(self, path):
        self.path = Path(path)
        self.version = self._json(["version"])

    @classmethod
    def find(cls, explicit=None, allow_build=True, log=print):
        """The helper, or None when it is missing and cannot be built (callers then use the Python fallback)."""
        if explicit:
            return cls(explicit)
        for p in candidates():
            if p.exists():
                if allow_build and p.parent == _out_root() / "soundcook" / "bin" and _stale(p):
                    try:
                        return cls(build(log))
                    except NativeError as e:
                        log(f"soundcook: {e}")
                return cls(p)
        if not allow_build:
            return None
        try:
            return cls(build(log))
        except NativeError as e:
            log(f"soundcook: {e}; using the pure-Python path (48 kHz WAV only, slower)")
            return None

    def _json(self, args):
        r = subprocess.run([str(self.path)] + [str(a) for a in args], capture_output=True, text=True)
        try:
            out = json.loads(r.stdout.strip().splitlines()[-1]) if r.stdout.strip() else {}
        except json.JSONDecodeError:
            out = {}
        if r.returncode != 0 or not out.get("ok"):
            raise NativeError(out.get("error") or (r.stderr.strip() or f"kiln-soundcook {args[0]} failed ({r.returncode})"))
        return out

    def cook(self, src, channels="keep", gain=None, normalize=None, start=0.0, end=0.0, seed=0, quality=10):
        """Source -> (interleaved int16 PCM bytes at 48 kHz, FramelessQOA bytes, stats)."""
        with tempfile.TemporaryDirectory(prefix="soundcook-") as d:
            pcm_p, qoa_p = Path(d) / "a.pcm", Path(d) / "a.qoa"
            args = ["cook", src, "--channels", channels, "--seed", seed, "--quality", quality, "--pcm", pcm_p, "--qoa", qoa_p]
            if gain is not None:
                args += ["--gain", repr(float(gain))]
            if normalize is not None:
                args += ["--normalize", repr(float(normalize))]
            if start:
                args += ["--start", repr(float(start))]
            if end:
                args += ["--end", repr(float(end))]
            stats = self._json(args)
            return pcm_p.read_bytes(), qoa_p.read_bytes(), stats

    def encode(self, pcm_bytes, channels):
        with tempfile.TemporaryDirectory(prefix="soundcook-") as d:
            i, o = Path(d) / "a.pcm", Path(d) / "a.qoa"
            i.write_bytes(pcm_bytes)
            self._json(["encode", i, o, "--channels", channels])
            return o.read_bytes()

    def decode(self, qoa, frames, channels):
        with tempfile.TemporaryDirectory(prefix="soundcook-") as d:
            i, o = Path(d) / "a.qoa", Path(d) / "a.pcm"
            i.write_bytes(qoa)
            self._json(["decode", i, o, "--frames", frames, "--channels", channels])
            return o.read_bytes()

    def probe(self, src):
        return self._json(["probe", src])


if __name__ == "__main__":
    n = Native.find(log=lambda m: print(m, file=sys.stderr))
    print(n.path if n else "none")
