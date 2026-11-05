# SPDX-License-Identifier: MIT
"""tools/kfc sound codec tests on synthetic signals (no game files): FramelessQOA layout, decoder, reference encoder.

    python3 -m unittest discover -s tools/kfc/tests -v
"""
import math
import random
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from kfc import sound as S  # noqa: E402


def tone(n, freq=440.0, amp=8000, rate=48000, phase=0.0):
    return [int(round(amp * math.sin(2 * math.pi * freq * i / rate + phase))) for i in range(n)]


class TablesTest(unittest.TestCase):
    def test_tables_match_the_exe(self):
        # The client's int32 table at file offset 0x16b41c0 (client 26430625) starts and ends like this.
        self.assertEqual(S.DEQUANT[0], [1, -1, 3, -3, 5, -5, 7, -7])
        self.assertEqual(S.DEQUANT[1], [5, -5, 18, -18, 32, -32, 49, -49])
        self.assertEqual(S.DEQUANT[15], [1536, -1536, 5120, -5120, 9216, -9216, 14336, -14336])
        self.assertEqual(S.SCALEFACTORS, [1, 7, 21, 45, 84, 138, 211, 304, 421, 562, 731, 928, 1157, 1419, 1715, 2048])


class LayoutTest(unittest.TestCase):
    def test_size(self):
        self.assertEqual(S.qoa_size(102502, 1), 41008)     # ZH_NPC_Bard_Farewell_03_v2
        self.assertEqual(S.qoa_size(1117344, 2), 893888)   # amb_loop_wind_howling
        self.assertEqual(S.qoa_size(4297720, 4), 6876352)  # amb_city_soundscape_01 (quad)

    def test_known_slice(self):
        # One slice, scale factor 0, all residual indices 0 (+1 each): from the start state the output ramps up.
        # The start weights {0, 0, -8192, 16384} predict 2*h3 - h2; residual 1 >> 4 == 0 leaves them unchanged.
        out = S.decode_qoa(struct.pack("<Q", 0), 20, 1)[0]
        h, ref = [0, 0], []
        for _ in range(20):
            v = 2 * h[1] - h[0] + 1
            h = [h[1], v]
            ref.append(v)
        self.assertEqual(out, ref)
        self.assertEqual(out[:4], [1, 3, 6, 10])

    def test_little_endian_words_and_short_last_slice(self):
        pcm = [tone(25)]
        data = S.encode_qoa(pcm)
        self.assertEqual(len(data), 16)
        last = struct.unpack_from("<Q", data, 8)[0]
        self.assertEqual(last & ((1 << (3 * 15)) - 1), 0)  # 5 samples used: 15 residuals of padding, zero here


class RoundTripTest(unittest.TestCase):
    def test_mono_tone(self):
        pcm = [tone(4800)]
        back = S.decode_qoa(S.encode_qoa(pcm), 4800, 1)
        self.assertGreater(S.snr_db(pcm[0], back[0]), 30)

    def test_stereo_interleave(self):
        n = 2000
        left, right = tone(n, 440), [0] * n
        back = S.decode_qoa(S.encode_qoa([left, right]), n, 2)
        self.assertGreater(S.snr_db(left, back[0]), 30)
        self.assertLess(max(abs(x) for x in back[1]), 4)  # the silent channel stays silent

    def test_noise_and_clipping(self):
        rnd = random.Random(7)
        pcm = [[max(-32768, min(32767, int(rnd.gauss(0, 12000)))) for _ in range(3000)]]
        back = S.decode_qoa(S.encode_qoa(pcm), 3000, 1)
        self.assertTrue(all(-32768 <= x <= 32767 for x in back[0]))
        self.assertGreater(S.snr_db(pcm[0], back[0]), 8)

    def test_reencode_is_stable(self):
        # Decoded QOA re-encodes to the same slices (what the game's own data shows: docs/re/audio.md).
        pcm = [tone(3000, 300), tone(3000, 500, phase=1.0)]
        first = S.encode_qoa(pcm)
        dec = S.decode_qoa(first, 3000, 2)
        second = S.encode_qoa(dec)
        self.assertEqual(S.decode_qoa(second, 3000, 2), dec)
        self.assertEqual(first[:-16], second[:-16])

    def test_wav_io(self):
        pcm = [tone(480), tone(480, 880)]
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "t.wav"
            S.write_wav(p, 48000, pcm)
            rate, back = S.read_wav(p)
        self.assertEqual(rate, 48000)
        self.assertEqual(back, pcm)


if __name__ == "__main__":
    unittest.main()
