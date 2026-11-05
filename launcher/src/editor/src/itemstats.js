// O que o jogo mostra no tooltip de um item, a partir de `data/item-stats.json`.
import stats from '../data/item-stats.json';
import { campoDeNome, localeAtual } from './i18n.js';

const { nomes, itens, balanceamento, tiposDano } = stats;

export const BALANCEAMENTO = balanceamento;

const nomeDoTexto = (id) => {
  const par = nomes[id];
  if (!par) return `#${id}`;
  return (campoDeNome() === 'pt' ? par[0] : par[1]) || par[1] || par[0] || `#${id}`;
};

/**
 * Valor de uma linha que escala com o nível do item — `WeaponPowerCreep`, 60 linhas, todas de
 * joia. A conta não foi medida, e devolver null faz a linha aparecer sem número: num editor, um
 * número errado é pior que número nenhum, porque é por ele que se decide o que editar.
 */
export function valorEscalado(funcao, fator, base, nivel) {
  void funcao; void fator; void base; void nivel;
  return null;
}

const semZeros = (n, casas) => Number(n.toFixed(casas)).toLocaleString(localeAtual(), { maximumFractionDigits: casas });

/**
 * Tempo vem em MILISSEGUNDOS, e o jogo troca de unidade pelo tamanho. Medido nas Botas de
 * Andarilho do Vento, que trazem -500 e 90000 e aparecem no jogo como "-0.5s" e "1:30 m".
 */
function tempo(ms, temSinal) {
  const sinal = temSinal && ms >= 0 ? '+' : '';
  if (Math.abs(ms) < 60000) return `${sinal}${semZeros(ms / 1000, 1)}s`;
  const neg = ms < 0 ? '-' : sinal;
  const total = Math.round(Math.abs(ms) / 1000);
  return `${neg}${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')} m`;
}

function formatar(valor, formato, temSinal) {
  const sinal = temSinal && valor >= 0 ? '+' : '';
  switch (formato) {
    case '%': return `${sinal}${valor}%`;
    case 's': case 'ms': return tempo(valor, temSinal);
    case 'f': return `${sinal}${semZeros(Number(valor), 2)}`;
    default: return `${sinal}${valor}`;
  }
}

// O jogo consome esta linha no cabeçalho do tooltip e não a repete entre os atributos. O valor
// gravado é o nível NOMINAL do item (nos 604 que a têm é sempre o `minLevel`), então mostrá-la
// faria um machado de nível 11 anunciar 40.
const NIVEL_DO_ITEM = 2298204525;

function linha(l, nivel) {
  const valor = l.e ? valorEscalado(l.e[0], l.e[1], l.v, nivel) : l.v;
  return {
    nome: nomeDoTexto(l.t),
    valor: valor === null ? null : formatar(valor, l.f, l.s),
    variaComNivel: Boolean(l.e),
  };
}

export function atributosDe(itemId, nivel) {
  const reg = itens[itemId];
  return (reg?.a || []).filter((l) => l.t !== NIVEL_DO_ITEM).map((l) => linha(l, nivel));
}

/**
 * Medido contra o jogo nas Adagas do Orgulho, nível 50: 10 × 1,0 × 0,65 × (1 + 0,15×50) = 55,25,
 * e a melhoria de 10% aparece lá como "+5.5 Dano de Perfuração / 10% de dano base".
 *
 * Nos arcos a distribuição de dano é ZERO: o arco não tem dano próprio, ele vem da flecha. Para
 * eles isto é só a parte da arma, e o jogo mostra um pouco menos (21,5 contra 21,6).
 */
export function danoBase(itemId, nivel) {
  const reg = itens[itemId];
  if (!reg?.d || nivel === undefined) return null;
  const { genericWeaponSeed: seed, powerCreepWeapon: creep } = balanceamento;
  if (seed === undefined || creep === undefined) return null;
  return seed * (reg.d.mod ?? 1) * (reg.d.vel ?? 1) * (1 + creep * nivel);
}

export function danoDe(itemId, nivel) {
  const reg = itens[itemId];
  const base = danoBase(itemId, nivel);
  if (base === null) return null;
  const tipos = Object.entries(reg.d.dist || {})
    .filter(([k, v]) => v > 0 && v <= 1 && tiposDano[k])
    .map(([k, v]) => ({ nome: nomeDoTexto(tiposDano[k]), valor: Math.round(base * v) }));
  return tipos.length ? tipos : null;
}

const baseArmadura = (nivel) => {
  const { offset, gradient } = balanceamento.armorSetup || {};
  return offset === undefined ? null : offset + gradient * nivel;
};

/**
 * O rótulo de cada linha é o NÍVEL DO ITEM naquele passo, não o índice: as botas de nível 255 com
 * 5 melhorias mostram 251 a 255, e um anel de nível 4 mostra 0 a 4.
 */
export function melhoriasDe(itemId, nivel, upgrades = 0) {
  const reg = itens[itemId];
  if (!reg?.p) return [];
  const base = (nivel ?? 0) - upgrades;
  const dist = reg.r;
  return reg.p.map((m, i) => {
    if (!m) return null;
    const nivelLinha = base + 1 + i;
    const bruta = baseArmadura(nivelLinha);
    const anterior = baseArmadura(nivelLinha - 1);

    // a coluna "Armadura" do jogo: o que este nível acrescenta à peça
    let ganho = null;
    if (dist && bruta !== null) {
      const sobe = (fr) => Math.round(bruta * fr) - Math.round(anterior * fr);
      ganho = { fisica: sobe(dist.physical || 0), magica: sobe(dist.magical || 0) };
    }

    // a de armadura vale no nível DESTA linha; a de dano, no nível do ITEM — as Adagas do Orgulho
    // mostram +5.5 nas linhas 48 e 50, o mesmo valor nas duas
    let armadura = null;
    if (m.ra && bruta !== null) {
      const fr = Object.values(m.ra)[0] || 0;
      armadura = { valor: Math.round(bruta * fr), nome: m.rn ? nomeDoTexto(m.rn) : '' };
    }
    let dano = null;
    if (m.rd) {
      const baseDano = danoBase(itemId, nivel);
      if (baseDano !== null) {
        const fr = Object.values(m.rd)[0] || 0;
        const chave = Object.keys(m.rd)[0];
        dano = {
          valor: Math.round(baseDano * fr * 10) / 10,
          nome: nomeDoTexto(m.rn ?? tiposDano[chave] ?? ''),
        };
      }
    }

    return {
      // as posições vazias são descartadas no fim, então quem conta melhorias (a raridade limita
      // quantas o item aceita) tem de usar isto, não o índice da lista devolvida
      pos: i,
      nivel: nivelLinha,
      aplicada: i < upgrades,
      linhas: (m.v || []).map((l) => linha(l, nivel)),
      no: m.no ?? null,
      ganho,
      armadura,
      dano,
    };
  }).filter(Boolean);
}

/**
 * `baseArmor = offset + gradient × nível` vem do comentário que o próprio estúdio compilou no
 * jogo: em `keen::BalancingTable`, o campo `armorSetup` carrega o texto "Base Armor = offset +
 * ( gradient * item level )", com 23 e 15 na mesma tabela.
 *
 * Conferido nas Botas de Andarilho do Vento em 255: (23 + 15×255) × 0,06 = 230,88, e o jogo
 * escreve 231 — o que também prova que não há corte no nível 55.
 */
export function armaduraDe(itemId, nivel) {
  const reg = itens[itemId];
  if (!reg?.r || nivel === undefined) return null;
  const { offset, gradient } = balanceamento.armorSetup || {};
  if (offset === undefined || gradient === undefined) return null;
  const base = offset + gradient * nivel;
  return {
    base: Math.round(base),
    fisica: Math.round(base * (reg.r.physical || 0)),
    magica: Math.round(base * (reg.r.magical || 0)),
  };
}

export const temEstatisticas = (itemId) => {
  const reg = itens[itemId];
  return Boolean(reg && (reg.a || reg.p || reg.r || reg.d));
};

// Kiln: a mod item uses its base item's attribute lines (gamedata.js mergeModded)
export function copiarEstatisticas(de, para) {
  if (stats.itens?.[de] && !stats.itens[para]) stats.itens[para] = stats.itens[de];
}
