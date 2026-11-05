import React from 'react';
import { atributosDe, melhoriasDe, temEstatisticas, armaduraDe, danoDe } from './itemstats.js';
import { skillName, skills } from './gamedata.js';
import { t, localeAtual } from './i18n.js';

function Linha({ valor, nome }) {
  return (
    <div className="st-linha">
      <span className="st-valor">{valor ?? '—'}</span>
      <span className="st-nome">{nome}</span>
    </div>
  );
}

export default function ItemStats({ entry }) {
  if (!temEstatisticas(entry.itemId)) return null;
  const nivel = entry.level;
  const atributos = atributosDe(entry.itemId, nivel);
  // O vetor do jogo tem sempre cinco posições, mas quantas o item aceita é a RARIDADE: um Comum
  // não pode anunciar as cinco melhorias de um Lendário.
  const teto = entry.maxUpgrades ?? 0;
  const melhorias = melhoriasDe(entry.itemId, nivel, entry.upgrades ?? 0).filter((m) => m.pos < teto);
  if (!atributos.length && !melhorias.length) return null;

  const temVariavel = [...atributos, ...melhorias.flatMap((m) => m.linhas)].some((l) => l.valor === null);

  const armadura = armaduraDe(entry.itemId, nivel);
  const dano = danoDe(entry.itemId, nivel);
  const num = (v) => v.toLocaleString(localeAtual(), { maximumFractionDigits: 1 });

  return (
    <div className="ed-stats">
      {armadura && (
        <>
          <Linha valor={String(armadura.fisica)} nome={t('Armadura Física')} />
          <Linha valor={String(armadura.magica)} nome={t('Armadura Mágica')} />
        </>
      )}
      {dano && dano.map((d, i) => <Linha key={`d${i}`} valor={String(d.valor)} nome={d.nome} />)}
      {atributos.map((l, i) => <Linha key={i} {...l} />)}

      {melhorias.length > 0 && (
        <>
          <div className="st-titulo caps small">{t('Melhorias')}</div>
          {melhorias.map((m) => (
            <div key={m.nivel} className={`st-melhoria${m.aplicada ? ' aplicada' : ''}`}>
              <span className="st-nivel caps">{m.aplicada ? '✓ ' : ''}{t('nível {n}', { n: m.nivel })}</span>
              {/* a armadura que este nível acrescenta à peça, como a coluna do jogo */}
              <span className="st-ganho">{m.ganho ? `+${m.ganho.fisica}/+${m.ganho.magica}` : ''}</span>
              <span className="st-efeitos">
                {m.armadura && <Linha valor={`+${m.armadura.valor}`} nome={m.armadura.nome} />}
                {m.dano && <Linha valor={`+${m.dano.valor}`} nome={m.dano.nome} />}
                {m.linhas.map((l, i) => <Linha key={i} {...l} />)}
                {m.no !== null && skills.nodes[m.no] && (
                  <span className="hint">{t('aumenta o nível de {no} na árvore', { no: skillName(m.no) })}</span>
                )}
              </span>
            </div>
          ))}
        </>
      )}

      {temVariavel && (
        <div className="hint warn">{t('As linhas sem número variam com o nível do item por uma conta que ainda não foi medida; o editor não mostra um valor que não pode garantir.')}</div>
      )}
    </div>
  );
}
