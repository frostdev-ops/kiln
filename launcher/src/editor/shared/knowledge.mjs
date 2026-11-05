import { Buffer } from 'buffer';
// Conhecimento do personagem: pares (knowledgeId -> estado), onde o estado é um conjunto de bits.
//
// Duas formas do mesmo conteúdo:
// - bloco 'player_knowledge' do save (.ksd): u32 estado do contêiner, u32 n, n ids u32, n valores u32
// - arquivo K.bin das cópias do guardião: só n ids u32 seguidos de n valores u32 (o n vem do tamanho)
//
// Bits do estado: item -> 1 pegou, 2 usou, 4 inspecionou; receita -> 1 vista, 2 fabricada.
const BITS_ITEM = [[1, 'pegou'], [2, 'usou'], [4, 'inspecionou']];
const BITS_RECEITA = [[1, 'vista'], [2, 'fabricada']];

export function estadoTexto(valor, tipo) {
  const bits = tipo === 'r' ? BITS_RECEITA : BITS_ITEM;
  const nomes = bits.filter(([b]) => valor & b).map(([, n]) => n);
  return nomes.length ? nomes.join('+') : String(valor);
}

function pares(buf, off, n) {
  const out = [];
  for (let i = 0; i < n; i++) {
    out.push({ id: buf.readUInt32LE(off + i * 4), value: buf.readUInt32LE(off + n * 4 + i * 4) });
  }
  return out;
}

const CAB_BLOCO = 8; // u32 estado + u32 n

/** Lê o bloco player_knowledge de um .ksd. */
export function parseBlock(payload) {
  const p = Buffer.from(payload);
  const containerState = p.readUInt32LE(0);
  const n = p.readUInt32LE(4);
  if (p.length !== CAB_BLOCO + 8 * n) throw new Error('bloco player_knowledge com tamanho inesperado');
  return { containerState, entries: pares(p, CAB_BLOCO, n) };
}

/** Monta o bloco player_knowledge de volta. */
export function buildBlock({ containerState, entries }) {
  const n = entries.length;
  const p = Buffer.alloc(CAB_BLOCO + 8 * n);
  p.writeUInt32LE(containerState >>> 0, 0);
  p.writeUInt32LE(n, 4);
  entries.forEach((e, i) => {
    p.writeUInt32LE(e.id >>> 0, CAB_BLOCO + i * 4);
    p.writeUInt32LE(e.value >>> 0, CAB_BLOCO + n * 4 + i * 4);
  });
  return p;
}

/** Lê o K.bin das cópias do guardião. */
export function parseRaw(data) {
  const p = Buffer.from(data);
  if (p.length % 8) throw new Error('K.bin com tamanho inesperado');
  return { containerState: 1, entries: pares(p, 0, p.length / 8) };
}

/** Monta o K.bin de volta. */
export function buildRaw({ entries }) {
  const n = entries.length;
  const p = Buffer.alloc(8 * n);
  entries.forEach((e, i) => {
    p.writeUInt32LE(e.id >>> 0, i * 4);
    p.writeUInt32LE(e.value >>> 0, n * 4 + i * 4);
  });
  return p;
}
