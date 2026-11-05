// SPDX-License-Identifier: MIT
// UTF-8 byte offsets (what kiln lint reports) <-> UTF-16 string indices (what VS Code positions use).

/** Bytes a code point takes in UTF-8. */
function utf8Len(cp: number): number {
  return cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4;
}

/**
 * Maps byte offsets of one text to string indices. Offsets are looked up in ascending order fastest, but any order
 * works (it restarts from the beginning when asked for an earlier offset). An offset inside a multi-byte sequence maps
 * to the start of that character; offsets past the end map to text.length.
 */
export class ByteMapper {
  private idx = 0;
  private byte = 0;

  constructor(private readonly text: string) {}

  index(byteOffset: number): number {
    if (byteOffset < this.byte) {
      this.idx = 0;
      this.byte = 0;
    }
    const t = this.text;
    while (this.idx < t.length) {
      const cp = t.codePointAt(this.idx)!;
      const n = utf8Len(cp);
      if (this.byte + n > byteOffset) break;
      this.byte += n;
      this.idx += cp > 0xffff ? 2 : 1;
    }
    return this.idx;
  }
}

export function byteToIndex(text: string, byteOffset: number): number {
  return new ByteMapper(text).index(byteOffset);
}

export function indexToByte(text: string, index: number): number {
  let b = 0;
  for (let i = 0; i < index && i < text.length; ) {
    const cp = text.codePointAt(i)!;
    b += utf8Len(cp);
    i += cp > 0xffff ? 2 : 1;
  }
  return b;
}

/** 1-based line and column in code points (kiln lint's `line`/`col`) -> string index. */
export function lineColToIndex(text: string, line: number, col: number): number {
  let i = 0;
  for (let l = 1; l < line; l++) {
    const nl = text.indexOf("\n", i);
    if (nl < 0) return text.length;
    i = nl + 1;
  }
  for (let c = 1; c < col && i < text.length && text[i] !== "\n"; c++) {
    const cp = text.codePointAt(i)!;
    i += cp > 0xffff ? 2 : 1;
  }
  return i;
}
