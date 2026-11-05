#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""soundcook: a mod's audio (WAV/FLAC/Ogg Vorbis/MP3) -> Keen sound resources served by kiln.assets.

    soundcook.py recipe ASSETS/cook.json [--update-manifest] [--check] [--cross-check] [--no-dispatch]
                                         run a mod's cook recipe (the normal way; docs/kiln/modding/sounds.md)
    soundcook.py convert IN [-o OUT.wav] [--channels mono|stereo|keep] [--gain DB | --normalize DBFS]
                                         one source through the pipeline and FramelessQOA and back to a WAV you can
                                         listen to (size, SNR, levels); writes no game data
    soundcook.py info FILE.(sound|cue|vo).bin [--blob F.qoa]   decode a cooked resource (tools/kfc)
    soundcook.py wav FILE.sound.bin [--blob F.qoa] [-o OUT.wav]  decode a cooked sound to 16-bit WAV
    soundcook.py build-native            build kiln-soundcook (tools/soundcook/native/build.sh)

A mod ships its own audio and assets/cook.json, never game data. The cook runs on the player's machine (today this
script, later the launcher) and writes assets/cooked/ (gitignored), which the committed assets/assets.json names.
New sounds need no game install; only steps that copy a vanilla cue or voice-over ("base") or check overrides read
the installed client (read only). Steps of another tool in the same cook.json ("tool": "modelcook") are run by that
tool (tools/modelcook/modelcook.py) unless --no-dispatch.
Common options: --game DIR (default Steam's Enshrouded client; absent = new-only cook with the bundled reflection),
--exe EXE (the game's exe; read only), --native PATH | --no-native (kiln-soundcook helper; default: find or build it).
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "kfc"))

from kfc import sound as S  # noqa: E402
from sndcook import pcm as P, recipe, res as R  # noqa: E402
from sndcook.native import Native, NativeError, build  # noqa: E402

DEFAULT_GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
DISPATCH = {"modelcook": HERE.parent / "modelcook" / "modelcook.py", "animcook": HERE.parent / "animcook" / "animcook.py"}


def _log(m):
    print(m, file=sys.stderr)


def _native(a):
    if a.no_native:
        return None
    return Native.find(a.native, log=_log)


def _game(a):
    if a.no_game:
        return None
    g = recipe.Game.open(a.game, Path(a.exe).expanduser() if a.exe else None)
    if g is None and Path(a.game).expanduser() != DEFAULT_GAME:
        raise recipe.RecipeError(f"--game {a.game}: no readable game there")
    return g


def _dispatch(a, doc, extra):
    rc = 0
    for tool in recipe.other_tools(doc):
        script = DISPATCH.get(tool)
        if script is None:
            print(f"soundcook: steps of tool {tool!r}: no runner known here", file=sys.stderr)
            rc = 1
            continue
        cmd = [sys.executable, str(script), "--game", str(a.game)] + (["--exe", a.exe] if a.exe else []) + \
              ["recipe", a.recipe, "--no-dispatch"] + extra
        print(f"soundcook: {tool} steps -> {' '.join(cmd[1:])}", file=sys.stderr)
        rc = subprocess.run(cmd).returncode or rc
    return rc


def cmd_recipe(a):
    doc = recipe.load(a.recipe)
    if a.check:
        need = recipe.needs_cook(a.recipe)
        rc = 0
        if not a.no_dispatch and recipe.other_tools(doc):
            rc = _dispatch(a, doc, ["--check"])
        print("sound cook needed" if need else "sounds up to date")
        return 1 if need or rc else 0
    rc = 0
    if recipe.own_steps(doc):
        r = recipe.run(a.recipe, _game(a), _native(a), update_manifest=a.update_manifest, cross_check=a.cross_check,
                       log=_log)
        for s in r["steps"]:
            for o in s["outputs"]:
                bad = {k: v for k, v in o["checks"].items() if not v}
                extra = f" skipped: {o['skipped']}" if o.get("skipped") else ""
                print(f"step {s['step']} {o['kind']} {o['name']}: {o['guid']} {o['mode']} "
                      f"{'ok' if o['ok'] else 'FAILED ' + json.dumps(bad)}{extra}")
                for w in o.get("warnings", []):
                    print(f"  warning: {w}")
        m = r["manifest"]
        if m["missing"] or m["mismatched"] or m["stale"]:
            print("assets.json does not match the cooked files (run with --update-manifest):")
            for e in m["missing"] + m["mismatched"]:
                print("  " + json.dumps(e))
            for e in m["stale"]:
                print("  stale: " + json.dumps(e))
        print(f"{'ok' if r['ok'] else 'FAILED'}: {len(r['outputs'])} file(s); report {r['report_file']}")
        rc = 0 if r["ok"] else 1
    if not a.no_dispatch and recipe.other_tools(doc):
        rc = _dispatch(a, doc, ["--update-manifest"] if a.update_manifest else []) or rc
    return rc


def cmd_convert(a):
    nat = _native(a)
    seed = P.fnv1a64("sound:" + Path(a.input).stem)
    if nat is not None:
        pcm_b, qoa, st = nat.cook(a.input, a.channels, a.gain, a.normalize, seed=seed)
        pcm = P.from_bytes(pcm_b)
    else:
        pcm, st = P.cook(a.input, a.channels, a.gain, a.normalize, seed=seed)
        qoa = S.encode_qoa(P.split(pcm, st["channels"]))
    ch, n = st["channels"], st["frames"]
    back = S.decode_qoa(qoa, n, ch)
    ref = P.split(pcm, ch)
    snr = min(S.snr_db(x, y) for x, y in zip(ref, back))
    out = Path(a.o) if a.o else Path(a.input).with_suffix(".cooked.wav")
    S.write_wav(out, R.RATE, back)
    src = st["source"]
    print(f"{a.input}: {src['format']} {src['rate']} Hz {src['channels']} ch {src['frames']} frames -> 48000 Hz {ch} ch "
          f"{n} frames ({n / R.RATE:.2f} s, {'inline' if n <= R.INLINE_MAX_FRAMES else 'streamed'}), "
          f"{len(qoa)} bytes FramelessQOA; peak {st['peak_dbfs']:.1f} dBFS, active RMS {st['rms_active_dbfs']:.1f} dBFS, "
          f"clipped {st['clipped']}, SNR {snr:.1f} dB; decoded: {out}")
    return 0


def _types(a):
    g = None if a.no_game else recipe.Game.open(a.game, Path(a.exe).expanduser() if a.exe else None)
    return R.choose_types(g.types if g else None)[0]


def _kind(name):
    for suf, tn in ((".sound.bin", R.SOUND), (".cue.bin", R.CUE), (".vo.bin", R.VO)):
        if name.endswith(suf):
            return tn
    raise SystemExit(f"{name}: expected *.sound.bin, *.cue.bin or *.vo.bin")


def _blob_for(path, blob):
    if blob:
        return Path(blob).read_bytes()
    q = Path(str(path)[:-len(".sound.bin")] + ".qoa")
    return q.read_bytes() if q.exists() else None


def cmd_info(a):
    t = _types(a)
    tn = _kind(a.file)
    v = R.decode(t, tn, Path(a.file).read_bytes())
    if tn == R.SOUND and isinstance(v.get("data"), dict) and v["data"].get("bytes"):
        v["data"] = {"bytes": f"<{len(v['data']['bytes']) // 2} bytes inline>"}
    print(json.dumps(v, indent=1))
    return 0


def cmd_wav(a):
    t = _types(a)
    if _kind(a.file) != R.SOUND:
        raise SystemExit("wav: a *.sound.bin file")
    v = R.decode(t, R.SOUND, Path(a.file).read_bytes())
    data = bytes.fromhex(v["data"]["bytes"]) if isinstance(v["data"], dict) and v["data"].get("bytes") else _blob_for(a.file, a.blob)
    if data is None:
        raise SystemExit("streamed sound: pass --blob F.qoa (or keep it next to the .sound.bin)")
    ch = S.CHANNELS[v["channelConfiguration"]]
    pcm = S.decode_qoa(data, v["frameCount"], ch)
    out = Path(a.o) if a.o else Path(str(a.file)[:-len(".sound.bin")] + ".wav")
    S.write_wav(out, v["framesPerSecond"], pcm)
    print(f"{out} ({ch} ch, {v['frameCount'] / v['framesPerSecond']:.2f} s, {v['debugName']})")
    return 0


def cmd_build_native(a):
    print(build(_log))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(prog="soundcook.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(DEFAULT_GAME))
    ap.add_argument("--exe")
    ap.add_argument("--no-game", action="store_true", help="ignore any installed game (new-only cook)")
    ap.add_argument("--native", help="path of kiln-soundcook")
    ap.add_argument("--no-native", action="store_true", help="pure-Python path (48 kHz WAV only)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("recipe")
    p.add_argument("recipe")
    p.add_argument("--update-manifest", action="store_true", help="add/replace the cooked entries in assets.json")
    p.add_argument("--check", action="store_true", help="only say whether a cook is needed (no game access)")
    p.add_argument("--cross-check", action="store_true", help="also encode every sound with the Python reference and compare")
    p.add_argument("--no-dispatch", action="store_true", help="leave other tools' steps alone")
    p = sub.add_parser("convert")
    p.add_argument("input")
    p.add_argument("-o")
    p.add_argument("--channels", default="keep", choices=("mono", "stereo", "keep"))
    p.add_argument("--gain", type=float)
    p.add_argument("--normalize", type=float)
    p = sub.add_parser("info")
    p.add_argument("file")
    p.add_argument("--blob")
    p = sub.add_parser("wav")
    p.add_argument("file")
    p.add_argument("--blob")
    p.add_argument("-o")
    sub.add_parser("build-native")
    a = ap.parse_args(argv)
    try:
        return {"recipe": cmd_recipe, "convert": cmd_convert, "info": cmd_info, "wav": cmd_wav,
                "build-native": cmd_build_native}[a.cmd](a)
    except (recipe.RecipeError, NativeError, P.PcmError) as e:
        print(f"soundcook: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
