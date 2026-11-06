import items from '../data/items.json';
import skills from '../data/skills.json';
import extras from '../data/item-extras.json';
import { campoDeNome, localeAtual, t } from './i18n.js';
import { copiarEstatisticas } from './itemstats.js';

export { items, skills };

// O jogo traz cada nome em português e em inglês, os dois da localização oficial dele, e
// `data/items.json` guarda os dois por item. Então não traduzimos nome de item à mão: lemos o campo
// do idioma escolhido. O outro idioma serve de reserva para as entradas sem nome.
// Exportado porque quem já tem o objeto em mãos (uma lista de catálogo, o nó sob o cursor) não
// deve voltar à tabela pelo id só para ler o nome — e nunca deve ler `.pt` direto: foi assim que
// a árvore de habilidades e o catálogo continuaram em português com a interface em inglês.
export const nomeDe = (o, reserva) => {
  if (!o) return reserva;
  const campo = campoDeNome();
  return o[campo] || o[campo === 'pt' ? 'en' : 'pt'] || reserva;
};

// Kiln: an id with no entry (an item of a mod that is gone) is named "Unknown item (0x…)"
export const nomeDesconhecido = (id) => t('Item desconhecido (0x{h})', { h: ((Number(id) || 0) >>> 0).toString(16).padStart(8, '0') });
export const itemName = (id) => nomeDe(items[id], items[id]?.debug || nomeDesconhecido(id));

// Kiln: items added by Kiln mods (the launcher's overlay, <art>/modded/items.json; docs/kiln/launcher/gameart.md).
// Each new item copies its base item's entry (category, slot, stack, levels) and gets its own name, rarity and icon;
// an icon override changes only the icon of a vanilla item. Calling it again first undoes the previous merge.
const ORIGINAIS = {};   // vanilla entries an override replaced, by id
let MESCLADOS = [];     // ids the last merge added or changed
let porDebug = null;
export function mergeModded(overlay, versao = '') {
  for (const id of MESCLADOS) {
    if (ORIGINAIS[id]) items[id] = ORIGINAIS[id]; else delete items[id];
    delete extras[id];
  }
  MESCLADOS = [];
  if (!overlay || !Array.isArray(overlay.items)) return 0;
  porDebug ??= new Map(Object.entries(items).map(([id, it]) => [it.debug, id]));
  const v = versao ? `?v=${encodeURIComponent(versao)}` : '';
  const icone = (rel) => (rel ? (rel.startsWith('modded/') ? rel + v : rel) : null);
  for (const m of overlay.items) {
    const id = String(m.id >>> 0);
    if (items[id] && !items[id].modded) continue; // never shadow a vanilla item
    const baseId = m.base ? porDebug.get(m.base) : undefined;
    const b = baseId !== undefined ? items[baseId] : undefined;
    items[id] = {
      ...(b || { category: 'Materials', slot: 'Invalid', rarity: 'Common', maxStack: 1, minLevel: 0, maxLevel: 0, class: 'None', blueprint: false }),
      debug: m.debug,
      en: m.names?.en || m.debug,
      pt: m.names?.pt || m.names?.en || m.debug,
      rarity: m.rarity || b?.rarity || 'Common',
      maxStack: m.maxStack ?? b?.maxStack ?? 1,
      icon: icone(m.icon),
      mod: m.modId || null,
      modded: true,
    };
    if (baseId !== undefined && extras[baseId]) extras[id] = extras[baseId];
    if (baseId !== undefined) copiarEstatisticas(baseId, id);
    MESCLADOS.push(id);
  }
  for (const o of overlay.icons || []) {
    const id = String(o.id >>> 0);
    if (!items[id] || items[id].modded) continue;
    ORIGINAIS[id] = items[id];
    items[id] = { ...items[id], icon: icone(o.icon) };
    MESCLADOS.push(id);
  }
  return overlay.items.length;
}

// A descrição do nó é guardada em `desc` e `descEn`, e não em `pt`/`en` como o nome.
export const descricaoDe = (no) => (campoDeNome() === 'pt' ? no?.desc : no?.descEn) || no?.desc || '';
export const skillName = (id) => nomeDe(skills.nodes[id], `#${id}`);

// O conhecimento quase sempre é um item ou a receita de um item, e aí o nome sai do `items.json`
// no idioma escolhido. Só as entradas sem item por trás levam o nome gravado, que existe num
// idioma só.
export const nomeConhecimento = (info, id) => {
  if (info?.i !== undefined) return itemName(info.i);
  return info?.n ?? `#${id}`;
};

export const ATRIBUTOS = ['Constituição', 'Espírito', 'Resistência', 'Força', 'Destreza', 'Inteligência'];

// O que cada nó de atributo dá está escrito na descrição dele, não no nome: há 65 nós de tipo
// `Attribute` e seis deles têm nome próprio (Barbárie, Pele Grossa, Passo Gracioso…). Contar pelo
// nome perdia esses seis.
//
// Duas redações aparecem para o caso simples — "Aumenta o atributo X em N" e "Aumenta o seu X
// atributo por N" — e uma terceira para os que dependem da Chama: "Ganhe um ponto de X a cada
// dois níveis da Chama". Esses últimos o editor não sabe calcular, porque o nível da Chama não
// está no save do personagem; ficam contados à parte.
const DIRETO = [
  /aumenta o atributo ([^\s]+) em (\d+)/i,
  /aumenta o seu ([^\s]+) atributo por (\d+)/i,
];
const POR_CHAMA = /(?:ponto|um) de ([^\s]+?)(?:\s|,|\.)[^.]*n[íi]ve(?:is|l) da Chama/i;

const casa = (texto, lista) => {
  for (const re of lista) {
    const m = texto.match(re);
    if (m) return m;
  }
  return null;
};

const atributoDe = (palavra) => ATRIBUTOS.find((a) => a.toLowerCase() === (palavra || '').toLowerCase()) ?? null;

/**
 * `porChama` conta os nós que dependem do nível da Chama, que o editor não conhece. O total do
 * jogo inclui ainda os bônus do equipamento, que também não entram aqui.
 */
export function atributosDaArvore(doSave) {
  const daArvore = Object.fromEntries(ATRIBUTOS.map((a) => [a, 0]));
  const porChama = Object.fromEntries(ATRIBUTOS.map((a) => [a, 0]));
  for (const s of doSave || []) {
    const n = skills.nodes[s.id];
    if (!n || n.type !== 'Attribute' || !(s.level > 0)) continue;
    // em português de propósito: as expressões abaixo são as do texto português
    const texto = (n.desc || '').replace(/\n/g, ' ');
    const direto = casa(texto, DIRETO);
    if (direto) {
      const a = atributoDe(direto[1]);
      if (a) daArvore[a] += Number(direto[2]) * s.level;
      continue;
    }
    const chama = texto.match(POR_CHAMA);
    const a = chama && atributoDe(chama[1]);
    if (a) porChama[a] += 1;
  }
  return { daArvore, porChama };
}

// os valores são chaves de tradução: quem mostra passa por t()
export const CATEGORY_PT = {
  Weapons: 'Arma', Equipment: 'Equipamento', Materials: 'Material', Consumables: 'Consumível',
  Ammunition: 'Munição', Tools: 'Ferramenta', BuildTools: 'Construção', Blueprints: 'Projeto',
  Collectibles: 'Coleção', Vanity: 'Visual', Gems: 'Gema',
};

const HIDDEN = new Set(['Customization', 'ColorPalette']); // aparência, não vai para o inventário
const itemList = Object.entries(items).filter(([, it]) => !HIDDEN.has(it.category)).map(([id, it]) => ({
  id: Number(id), ...it,
  search: `${it.pt || ''} ${it.en || ''} ${it.debug}`.toLowerCase(),
}));

export function searchItems(q, limit = 60) {
  const t = q.trim().toLowerCase();
  if (t.length < 2) return [];
  return itemList.filter((it) => it.search.includes(t)).slice(0, limit);
}

const skillList = Object.entries(skills.nodes)
  .filter(([, n]) => n.type !== 'Root')
  .map(([id, n]) => ({ id: Number(id), ...n, search: `${n.pt || ''} ${n.en || ''}`.toLowerCase() }));

export function searchSkills(q) {
  const t = q.trim().toLowerCase();
  // a ordem alfabética é a do idioma escolhido, senão a lista em inglês sai ordenada por português
  return skillList.filter((n) => !t || n.search.includes(t))
    .sort((a, b) => nomeDe(a, '').localeCompare(nomeDe(b, ''), localeAtual()));
}

// Entrada nova para o inventário.
//
// O jogo grava campos extras conforme o item, e as flags da entrada dizem quais estão ali. Criar
// um item com os campos errados dá um item quebrado no jogo: era o que acontecia com as joias,
// que caíam na regra final e saíam sem nível (flags 0x00 em vez de 0x01).
//
// `data/item-extras.json` traz a forma observada em saves reais, item por item, e tem a palavra
// final quando o item está lá. Para o resto valem as regras por categoria, conferidas contra os
// mesmos saves. O arquivo foi extraído de saves reais; regerá-lo não faz parte deste repositório,
// como o resto de `data/` (ver a seção de dados do jogo no README).
const RARIDADE = { Common: 1, Uncommon: 2, Rare: 3, Epic: 4, Legendary: 5 };
const SEM_EXTRAS = new Set(['Glider', 'HookShot', 'Invalid']); // planador, arpéu e chaves

export function newEntry(itemId, slot, count, template, nivelPersonagem) {
  if (template) {
    const { slot: _s, count: _c, ...rest } = template;
    return { ...rest, slot, count: 1, itemId };
  }
  const it = items[itemId];
  const base = { slot, itemId, count: Math.max(1, Math.min(count, it.maxStack || 1)) };
  if (it.maxStack > 1) return base;
  // O nível gravado no item é o que o jogo usa para escalar o efeito (as joias, por exemplo, têm
  // uiValues com isScaled e function WeaponPowerCreep). Itens que caem no jogo vêm no nível da
  // zona, então o padrão é o nível do personagem, preso à faixa que o item aceita; sem ele,
  // sobra o mínimo do item.
  const faixa = (n) => Math.min(Math.max(n, it.minLevel || 1), it.maxLevel || 105);
  const level = faixa(nivelPersonagem || it.minLevel || 1);
  const rarity = RARIDADE[it.rarity] || 1;

  // a forma vista em save real, quando existe para este item
  const visto = extras[itemId];
  if (visto) {
    const e = { ...base, count: 1 };
    if (visto.flags & 0x01) e.level = level;
    if (visto.flags & 0x02) { e.maxDurability = visto.maxDurability ?? 300; e.durability = e.maxDurability; }
    if (visto.flags & 0x04) { e.maxLiquid = visto.maxLiquid ?? 1280; e.liquid = 0; }
    // Kiln: the item's own rarity (the catalog shows it), not the one instance the table was read from
    if (visto.flags & 0x10) { e.maxUpgrades = rarity; e.upgrades = 0; }
    if (visto.flags & 0x20) e.socket = '0000000000';
    return e;
  }

  if (it.category === 'Weapons') {
    return { ...base, count: 1, level, durability: 300, maxDurability: 300, maxUpgrades: rarity, upgrades: 0, socket: '0000000000' };
  }
  // joia de arma: só o nível, como nas 35 observadas
  if (it.category === 'WeaponGem') return { ...base, count: 1, level };
  // munição de pilha 1 é a "eterna": nível e melhorias, sem durabilidade
  if (it.category === 'Ammunition') return { ...base, count: 1, level, maxUpgrades: rarity, upgrades: 0 };
  // consumível de pilha 1 é recipiente de líquido, como a Bolsa d'Água
  if (it.category === 'Consumables') return { ...base, count: 1, maxLiquid: 1280, liquid: 0 };
  // planador, arpéu e chaves não guardam nada; machado e picareta (slot Tool) guardam
  if (it.category === 'Tools') {
    if (SEM_EXTRAS.has(it.slot)) return { ...base, count: 1 };
    return { ...base, count: 1, level, durability: 200, maxDurability: 200, maxUpgrades: rarity, upgrades: 0 };
  }
  // em BuildTools convivem ferramentas (com durabilidade) e coisas de colocar (sem nada): fora da
  // tabela vale o caso comum, que é não guardar extras
  if (it.category === 'BuildTools') return { ...base, count: 1 };
  if (it.category === 'Equipment' && it.slot === 'Shield') {
    return { ...base, count: 1, level, durability: 100, maxDurability: 100, maxUpgrades: rarity, upgrades: 0 };
  }
  if (it.category === 'Equipment' && it.slot !== 'Backpack') return { ...base, count: 1, level, maxUpgrades: rarity, upgrades: 0 };
  return { ...base, count: 1 };
}
