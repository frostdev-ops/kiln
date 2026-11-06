// Equipment: the character's level and XP, the six attributes the tree gives, and what they wear: armour, the
// appearance pieces over it, and the gear (rings, shield, hook, glider, backpack), with the inspector beside them.
// The slot order is the bit order of the save's equipment mask (upstream src/Character.jsx): never reorder SLOTS.
import React, { useState } from 'react';
import { items, itemName, ATRIBUTOS, atributosDaArvore } from '../src/gamedata.js';
import { t } from '../src/i18n.js';
import { Inspector } from './Inspector.jsx';
import { Slot, Stepper } from './parts.jsx';

// index = bit in the equipment mask: 0-4 appearance, 5-9 armour, then ring, ring, shield, hook, glider, backpack
const SLOTS = ['Cabeça (visual)', 'Tronco (visual)', 'Braços (visual)', 'Pernas (visual)', 'Pés (visual)',
  'Cabeça', 'Tronco', 'Braços', 'Pernas', 'Pés', 'Anel', 'Anel', 'Protetor', 'Gancho', 'Planador', 'Mochila'];
const GROUPS = [
  ['Armadura', [5, 6, 7, 8, 9]],
  ['Aparência', [0, 1, 2, 3, 4]],
  ['Acessórios', [10, 11, 12, 13, 14, 15]],
];
// with 10 per attribute, four of six match the game exactly on a level-47 character; the rest come from nodes that
// follow the Flame's level and from gear, neither of which is in the character save
const BASE_ATTR = 10;
const PART = /\s*\((visual)\)$/;

export default function Equipment({ model, setModel }) {
  const [sel, setSel] = useState(5);
  const [warn, setWarn] = useState(null);
  // the save's list is dense: piece k sits in the k-th lit bit of the mask
  const lit = [];
  for (let i = 0; i < 32; i++) if (model.equipMask & (1 << i)) lit.push(i);
  const bySlot = new Map(model.equipment.map((e) => [lit[e.slot] ?? e.slot, e]));
  const entry = bySlot.get(sel);
  const update = (e) => setModel({ ...model, equipment: model.equipment.map((x) => (x.slot === e.slot ? e : x)) });
  const remove = (e) => {
    // taking the worn backpack off deletes its container and everything in it
    if (items[e.itemId]?.slot === 'Backpack' && model.extra.length) {
      setWarn(t('Não vou tirar a {item}: isso apagaria o contêiner dela e os {n} espaços guardados. Esvazie-o no jogo primeiro.', { item: itemName(e.itemId), n: model.extra.length }));
      return;
    }
    setWarn(null);
    setModel({ ...model, equipment: model.equipment.filter((x) => x.slot !== e.slot) });
  };
  const { daArvore, porChama } = atributosDaArvore(model.skills);
  const flame = Object.values(porChama).some((n) => n > 0);

  return (
    <div className="ke-split">
      <div className="ke-main">
        <section className="ke-facts">
          <label className="ke-fact"><span className="k-label">{t('Nível')}</span>
            <Stepper value={model.level} min={1} max={50} label={t('Nível')} onChange={(v) => setModel({ ...model, level: v })} /></label>
          <label className="ke-fact"><span className="k-label">{t('XP no nível')}</span>
            <Stepper value={model.xp} min={0} max={1e9} big={1000} label={t('XP no nível')} onChange={(v) => setModel({ ...model, xp: v })} /></label>
          <dl className="ke-attrs" title={t('Atributos calculados pelos nós da árvore (editáveis na aba Habilidades).') + (flame ? `\n${t('O jogo soma ainda os nós que dependem do nível da Chama e os bônus do equipamento; nenhum dos dois está no save do personagem.')}` : '')}>
            {ATRIBUTOS.map((a) => <div key={a}><dt className="k-label">{t(a)}</dt><dd className="k-num">{BASE_ATTR + daArvore[a]}</dd></div>)}
          </dl>
        </section>
        <div className="ke-gear">
          {GROUPS.map(([title, idx]) => (
            <section key={title} className="ke-group">
              <header className="ke-group-head"><h2 className="k-card-title">{t(title)}</h2></header>
              <ul className="ke-gear-list">
                {idx.map((i) => {
                  const e = bySlot.get(i);
                  return (
                    <li key={i} className={'ke-gear-row' + (sel === i ? ' on' : '')} onClick={() => setSel(i)}>
                      <Slot entry={e} size={48} selected={sel === i} onClick={() => setSel(i)} title={e ? itemName(e.itemId) : t(SLOTS[i])} />
                      <span className="ke-gear-text">
                        <span className="k-label">{t(SLOTS[i].replace(PART, ''))}</span>
                        <span className={'ke-gear-name' + (e ? '' : ' none')}>{e ? itemName(e.itemId) : t('Vazio')}</span>
                      </span>
                    </li>
                  );
                })}
              </ul>
            </section>
          ))}
        </div>
      </div>
      <Inspector entry={entry} caption={t(SLOTS[sel] ?? 'Espaço {n}', { n: sel })} warn={warn} onChange={update} onRemove={entry ? () => remove(entry) : null}
        empty={<div className="ke-empty"><span className="ke-empty-word">{t('Vazio')}</span><span className="ke-note">{t('Espaço vazio. Vista a peça no jogo; aqui dá para editar ou tirar as peças vestidas.')}</span></div>} />
    </div>
  );
}
