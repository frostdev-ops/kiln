// Contêiner SV2C para rodar no navegador/WebView (versão Tauri). Mesmo formato de electron/core/sv2c.cjs,
// mas só descompacta os blocos pedidos e reaproveita os bytes originais dos blocos não alterados.
// zstd é fornecido de fora (comandos Rust), xxh3 vem do hash-wasm.
import { Buffer } from 'buffer';
import { createXXHash3 } from 'hash-wasm';

let xxh3 = null;
export async function init() {
  if (!xxh3) xxh3 = await createXXHash3();
}
function hash32(buf) {
  xxh3.init();
  xxh3.update(buf);
  return parseInt(xxh3.digest('hex').slice(8), 16) >>> 0;
}

// decompress(bytes) -> Promise<Uint8Array>
export async function parse(input, decompress, wanted = ['character_data', 'player_character']) {
  const data = Buffer.from(input);
  if (data.toString('latin1', 0, 4) !== 'SV2C') throw new Error('Arquivo não é um save SV2C');
  const header = Buffer.from(data.subarray(0, 0x30));
  const dirOff = data.readUInt32LE(0x28) + 40;
  const count = data.readUInt32LE(0x2c);
  const blocks = [];
  for (let i = 0; i < count; i++) {
    const e = dirOff + 20 * i;
    const nameRel = data.readUInt32LE(e), nameLen = data.readUInt32LE(e + 4);
    const off = data.readUInt32LE(e + 8), size = data.readUInt32LE(e + 12), h = data.readUInt32LE(e + 16);
    const name = data.toString('utf8', e + nameRel, e + nameRel + nameLen);
    const raw = Buffer.from(data.subarray(off, off + size));
    if (raw.toString('latin1', 0, 4) !== 'S2BH') throw new Error(`Bloco ${name} sem assinatura S2BH`);
    if (hash32(raw) !== h) throw new Error(`Hash do bloco ${name} não confere`);
    const compressed = raw[9] === 1;
    const block = { name, raw, head: Buffer.from(raw.subarray(0, 14)), compressed, payload: null, size };
    if (wanted.includes(name)) block.payload = compressed ? Buffer.from(await decompress(raw.subarray(14))) : Buffer.from(raw.subarray(14));
    blocks.push(block);
  }
  return { header, blocks };
}

// blocos com .changed = true são recompactados; os demais usam os bytes originais
export async function build({ header, blocks }, compress) {
  const parts = [Buffer.from(header)];
  let pos = header.length;
  const entries = [];
  for (const b of blocks) {
    let raw = b.raw;
    if (b.changed) {
      const body = b.compressed ? Buffer.from(await compress(b.payload)) : b.payload;
      raw = Buffer.concat([b.head, body]);
    }
    entries.push({ off: pos, size: raw.length, hash: hash32(raw), name: Buffer.from(b.name, 'utf8') });
    parts.push(raw);
    pos += raw.length;
  }
  const pad = (8 - (pos % 8)) % 8;
  parts.push(Buffer.alloc(pad));
  pos += pad;
  const dirOff = pos;
  const dir = Buffer.alloc(20 * entries.length);
  let namePos = 0;
  entries.forEach((e, i) => {
    dir.writeUInt32LE(20 * (entries.length - i) + namePos, 20 * i);
    dir.writeUInt32LE(e.name.length, 20 * i + 4);
    dir.writeUInt32LE(e.off, 20 * i + 8);
    dir.writeUInt32LE(e.size, 20 * i + 12);
    dir.writeUInt32LE(e.hash, 20 * i + 16);
    namePos += e.name.length;
  });
  parts.push(dir, ...entries.map((e) => e.name));
  const out = Buffer.concat(parts);
  out.writeUInt32LE(out.length, 0x08);
  out.writeUInt32LE(dirOff - 40, 0x28);
  out.writeUInt32LE(entries.length, 0x2c);
  return out;
}

export function characterName(save) {
  const cd = save.blocks.find((b) => b.name === 'character_data');
  if (!cd?.payload || cd.payload.length < 9) return '?';
  const len = cd.payload.readUInt32LE(5);
  return cd.payload.toString('utf8', 9, 9 + Math.min(len, 64));
}
