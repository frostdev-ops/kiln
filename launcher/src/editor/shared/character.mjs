import { Buffer } from 'buffer';
// Bloco 'player_character' do save do personagem.
//
// 0x00..0x3a  aparência e escalas (preservado como está)
// 0x3a u32 nível, 0x3e u32 XP no nível atual, 0x42 u32 número de nós de habilidade
// nós: (u32 id, u8 nível) * n
// trecho opaco (buffs de comida ativos etc.) até o equipamento
// u32 máscara dos espaços de equipamento ocupados (4 bytes antes da lista)
// equipamento: lista densa, uma entrada por bit aceso da máscara, em ordem crescente;
//   o campo slot da entrada é o índice nessa lista (0, 1, 2...), não o espaço do boneco
// u16 espaços de equipamento livres (logo depois da lista)
// trecho opaco que termina em [05][qtd principal][04][qtd extra]
// entradas do inventário principal, depois as do contêiner extra, depois o resto (opaco)
//
// Medido em três saves do mesmo personagem (16, 15 e 14 peças): tirar a peça de índice k apaga
// o k-ésimo bit aceso da máscara e soma 1 aos espaços livres. A máscara tem 2 bits acesos a mais
// que a lista (as armas, guardadas em outra parte do bloco), e livres + peças deu 27 nos três.
//
// Entrada: u8 slot, u32 itemId, u16 quantidade, u8 flags e então, na ordem dos bits:
//   0x01 u8 nível | 0x02 u32 durabilidade, u32 durabilidade máx | 0x04 u32 líquido, u32 líquido máx
//   0x08 4 bytes (aparência/tingimento, preservado) | 0x10 u8 melhorias máx, u8 melhorias aplicadas
//   0x20 5 bytes (engaste, preservado)
const KNOWN_FLAGS = 0x01 | 0x02 | 0x04 | 0x08 | 0x10 | 0x20;
const EQUIP_MAX = 24;       // teto da lista densa (o personagem medido tem 16)
const MIN_EQUIP = 4;        // menos que isso é coincidência, não a lista de equipamento
const EQUIP_EXTRA_BITS = 4; // bits acesos fora da lista (as armas); medido = 2

/** Quantidade de bits acesos. */
function popcount(v) {
  let n = 0;
  for (let i = 0; i < 32; i++) if (v & (1 << i)) n++;
  return n;
}

function readEntry(p, o, isItem) {
  if (o + 8 > p.length) return null;
  const itemId = p.readUInt32LE(o + 1);
  if (!isItem(itemId)) return null;
  const e = { slot: p[o], itemId, count: p.readUInt16LE(o + 5), flags: p[o + 7] };
  if (e.flags & ~KNOWN_FLAGS) return null;
  let q = o + 8;
  const need = (e.flags & 1 ? 1 : 0) + (e.flags & 2 ? 8 : 0) + (e.flags & 4 ? 8 : 0) + (e.flags & 8 ? 4 : 0) + (e.flags & 0x10 ? 2 : 0) + (e.flags & 0x20 ? 5 : 0);
  if (q + need > p.length) return null;
  if (e.flags & 0x01) { e.level = p[q]; q += 1; }
  if (e.flags & 0x02) { e.durability = p.readUInt32LE(q); e.maxDurability = p.readUInt32LE(q + 4); q += 8; }
  if (e.flags & 0x04) { e.liquid = p.readUInt32LE(q); e.maxLiquid = p.readUInt32LE(q + 4); q += 8; }
  if (e.flags & 0x08) { e.appearance = p.subarray(q, q + 4).toString('hex'); q += 4; }
  if (e.flags & 0x10) { e.maxUpgrades = p[q]; e.upgrades = p[q + 1]; q += 2; }
  if (e.flags & 0x20) { e.socket = p.subarray(q, q + 5).toString('hex'); q += 5; }
  e._end = q;
  return e;
}

function writeEntry(e) {
  let flags = 0;
  if (e.level !== undefined) flags |= 0x01;
  if (e.durability !== undefined) flags |= 0x02;
  if (e.liquid !== undefined) flags |= 0x04;
  if (e.appearance !== undefined) flags |= 0x08;
  if (e.maxUpgrades !== undefined) flags |= 0x10;
  if (e.socket !== undefined) flags |= 0x20;
  const parts = [];
  const h = Buffer.alloc(8);
  h[0] = e.slot; h.writeUInt32LE(e.itemId >>> 0, 1); h.writeUInt16LE(e.count, 5); h[7] = flags;
  parts.push(h);
  if (flags & 0x01) parts.push(Buffer.from([e.level]));
  if (flags & 0x02) { const b = Buffer.alloc(8); b.writeUInt32LE(e.durability, 0); b.writeUInt32LE(e.maxDurability, 4); parts.push(b); }
  if (flags & 0x04) { const b = Buffer.alloc(8); b.writeUInt32LE(e.liquid, 0); b.writeUInt32LE(e.maxLiquid, 4); parts.push(b); }
  if (flags & 0x08) parts.push(Buffer.from(e.appearance, 'hex'));
  if (flags & 0x10) parts.push(Buffer.from([e.maxUpgrades, e.upgrades]));
  if (flags & 0x20) parts.push(Buffer.from(e.socket, 'hex'));
  return Buffer.concat(parts);
}

function parseCharacter(p, isItem, isSkill, ehEquipavel = null) {
  const level = p.readUInt32LE(0x3a), xp = p.readUInt32LE(0x3e), nSkills = p.readUInt32LE(0x42);
  const skills = [];
  let o = 0x46;
  for (let i = 0; i < nSkills; i++, o += 5) skills.push({ id: p.readUInt32LE(o), level: p[o + 4] });
  if (skills.some((s) => !isSkill(s.id))) throw new Error('Lista de habilidades não reconhecida');
  const skillsEnd = o;

  // Trecho entre equipamento e inventário: termina em 05 <qtd> 04 <qtd extra>. O tamanho desse
  // trecho varia muito de save para save (medido: de 4 a mais de 64 bytes), então procuramos até
  // o fim do bloco e confiamos na conferência de que todas as pilhas depois do cabeçalho são lidas.
  const findInventory = (from) => {
    for (let s = from + 4; s < p.length; s++) {
      // 05 <qtd principal> <grupos do contêiner extra> <qtd extra>; sem mochila vestida os grupos são 0
      if (p[s - 4] !== 0x05 || p[s - 2] > 8) continue;
      const mc = p[s - 3], ec = p[s - 1];
      let q = s, ok = true;
      for (let k = 0; k < mc + ec && ok; k++) {
        const e = readEntry(p, q, isItem);
        if (e) q = e._end; else ok = false;
      }
      if (ok) return { invStart: s, mainCount: mc, extraCount: ec };
    }
    return null;
  };

  // Equipamento: lista densa com os índices em ordem crescente (0, 1, 2…), precedida pela máscara
  // e seguida pelos espaços livres. A busca é por tentativa, então só aceitamos um candidato que
  // feche TODOS os confereres abaixo e que seja único: adivinhar aqui grava um save corrompido.
  // Kiln: a new character wears fewer than MIN_EQUIP pieces; only when nothing else fits, accept a shorter list (the
  // inventory header and the mask must still close, and saving reads the new block back before writing)
  const buscar = (minimo) => {
  const candidatos = [];
  for (let s = skillsEnd + 4; s < p.length - 8; s++) {
    const list = []; let q = s;
    while (true) {
      const e = readEntry(p, q, isItem);
      if (!e || e.slot >= EQUIP_MAX || e.slot !== list.length) break;
      list.push(e); q = e._end;
    }
    if (list.length < minimo) continue;
    // só peças vestíveis: a mochila também é uma lista densa começando no 0, e sem isso o
    // editor pode confundir as duas e gravar um save corrompido
    if (ehEquipavel && !list.every((e) => ehEquipavel(e.itemId))) continue;
    const found = findInventory(q);
    if (!found) continue;
    const mask = p.readUInt32LE(s - 4);
    const free = p.readUInt16LE(q);
    // a máscara precisa ter um bit aceso para cada peça (mais os das armas, fora desta lista)
    const bits = popcount(mask);
    if (bits < list.length || bits > list.length + EQUIP_EXTRA_BITS) continue;
    candidatos.push({ equipStart: s, equipment: list, inv: found, mask, free, equipEnd: q });
  }
  return candidatos;
  };
  let candidatos = buscar(MIN_EQUIP);
  if (!candidatos.length) candidatos = buscar(1);
  if (!candidatos.length) throw new Error('Equipamento/inventário não encontrado no save');
  // o equipamento vem antes do inventário no bloco, então o primeiro candidato é o certo
  const { equipStart, equipment, inv, mask, free, equipEnd } = candidatos[0];
  const { invStart, mainCount, extraCount } = inv;
  const main = [], extra = [];
  let q = invStart;
  for (let k = 0; k < mainCount + extraCount; k++) {
    const e = readEntry(p, q, isItem);
    (k < mainCount ? main : extra).push(e);
    q = e._end;
  }
  const clean = (l) => l.map(({ _end, ...e }) => e);
  return {
    level, xp,
    skills,
    equipment: clean(equipment),
    main: clean(main),
    extra: clean(extra),
    _equipCount: equipment.length,
    _equipMask: mask,
    _equipFree: free,
    _raw: {
      head: Buffer.from(p.subarray(0, 0x3a)),
      mid: Buffer.from(p.subarray(skillsEnd, equipStart)), // os 4 últimos bytes são a máscara
      between: Buffer.from(p.subarray(equipEnd, invStart)), // os 2 primeiros são os espaços livres
      tail: Buffer.from(p.subarray(q)),
    },
  };
}

/** Posições dos bits acesos, em ordem crescente. */
function bitsAcesos(mask) {
  const out = [];
  for (let i = 0; i < 32; i++) if (mask & (1 << i)) out.push(i);
  return out;
}

/** Máscara e espaços livres depois de tirar as peças cujos índices estão em `tirados`. */
function ajustaEquipamento(mask, free, tirados) {
  const bits = bitsAcesos(mask);
  let nova = mask;
  for (const k of tirados) {
    if (bits[k] === undefined) throw new Error(`Não sei qual espaço de equipamento corresponde à peça ${k}`);
    nova &= ~(1 << bits[k]);
  }
  return { mask: nova >>> 0, free: free + tirados.length };
}

function buildCharacter(c) {
  const r = c._raw;
  const top = Buffer.alloc(12);
  top.writeUInt32LE(c.level, 0); top.writeUInt32LE(c.xp, 4); top.writeUInt32LE(c.skills.length, 8);
  const skills = Buffer.alloc(5 * c.skills.length);
  c.skills.forEach((s, i) => { skills.writeUInt32LE(s.id >>> 0, 5 * i); skills[5 * i + 4] = s.level; });
  if (c.equipment.length > c._equipCount) throw new Error('Não é possível adicionar peças de equipamento: não sei em que espaço do boneco elas entram');
  if (c.main.length > 255 || c.extra.length > 255) throw new Error('Itens demais em um contêiner');

  // peças tiradas: os índices da lista original que não vieram de volta. A lista é densa,
  // então as que ficam são renumeradas de 0 em diante, e a máscara e os espaços livres acompanham.
  const mid = Buffer.from(r.mid);
  const between = Buffer.from(r.between);
  const ficam = [...c.equipment].sort((a, b) => a.slot - b.slot);
  const tirados = [];
  for (let k = 0; k < c._equipCount; k++) if (!ficam.some((e) => e.slot === k)) tirados.push(k);
  if (tirados.length) {
    if (mid.length < 4 || between.length < 2) throw new Error('Não achei a máscara de equipamento neste save');
    const { mask, free } = ajustaEquipamento(c._equipMask, c._equipFree, tirados);
    mid.writeUInt32LE(mask, mid.length - 4);
    between.writeUInt16LE(free, 0);
  }
  const equip = ficam.map((e, i) => writeEntry({ ...e, slot: i }));

  between[between.length - 3] = c.main.length;
  between[between.length - 1] = c.extra.length;
  const main = [...c.main].sort((a, b) => a.slot - b.slot);
  return Buffer.concat([
    r.head, top, skills, mid,
    ...equip, between,
    ...main.map(writeEntry), ...c.extra.map(writeEntry),
    r.tail,
  ]);
}

export { parseCharacter, buildCharacter, EQUIP_MAX };
