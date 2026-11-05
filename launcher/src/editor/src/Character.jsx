import React, { useState } from 'react';
import { skills, items, itemName, ATRIBUTOS, atributosDaArvore } from './gamedata.js';
import { tex, ttex } from './ui.js';
import ItemEditor, { FancyPanel } from './ItemEditor.jsx';
import { SlotView, Stepper, Separator } from './components.jsx';
import { t } from './i18n.js';

// O índice é a posição do bit na máscara, por isso a ordem do vetor não pode mudar — o que muda
// é onde cada um aparece na tela.
//
// Os cinco PRIMEIROS espaços são os de aparência e os cinco seguintes a armadura de verdade.
// Medido num personagem com as cinco peças vestidas: elas caem nos bits 5 a 9, e a partir do 10
// vêm anel, anel, protetor, gancho, planador e mochila, todos conferindo com o campo `slot` do
// próprio item. A armadura fica na coluna da esquerda porque é a que se usa.
export const EQUIP_SLOTS = [
  { tex: 'equipmentslot_head', name: 'Cabeça (visual)', x: 192, y: 150 },
  { tex: 'equipmentslot_upperbody', name: 'Tronco (visual)', x: 192, y: 300 },
  { tex: 'equipmentslot_arms', name: 'Braços (visual)', x: 192, y: 450 },
  { tex: 'equipmentslot_legs', name: 'Pernas (visual)', x: 192, y: 600 },
  { tex: 'equipmentslot_feet', name: 'Pés (visual)', x: 192, y: 750 },
  { tex: 'equipmentslot_head', name: 'Cabeça', x: 42, y: 150 },
  { tex: 'equipmentslot_upperbody', name: 'Tronco', x: 42, y: 300 },
  { tex: 'equipmentslot_arms', name: 'Braços', x: 42, y: 450 },
  { tex: 'equipmentslot_legs', name: 'Pernas', x: 42, y: 600 },
  { tex: 'equipmentslot_feet', name: 'Pés', x: 42, y: 750 },
  { tex: 'equipmentslot_ring', name: 'Anel', x: 1110, y: 150 },
  { tex: 'equipmentslot_ring', name: 'Anel', x: 1110, y: 300 },
  { tex: 'equipmentslot_shield', name: 'Protetor', x: 1110, y: 450 },
  { tex: 'equipmentslot_hookshot', name: 'Gancho', x: 1110, y: 600 },
  { tex: 'equipmentslot_glider', name: 'Planador', x: 1110, y: 750 },
  { tex: 'equipmentslot_backpack', name: 'Mochila', x: 1110, y: 900 },
];
const SLOT = 118;

export default function Character({ model, setModel }) {
  const [sel, setSel] = useState(0);
  // a lista do save é densa: a peça de índice k ocupa o k-ésimo espaço aceso na máscara.
  // Sem isso, faltando um anel tudo o que vem depois aparece uma casa antes no boneco.
  const acesos = [];
  for (let i = 0; i < 32; i++) if (model.equipMask & (1 << i)) acesos.push(i);
  const bySlot = new Map(model.equipment.map((e) => [acesos[e.slot] ?? e.slot, e]));
  const entry = bySlot.get(sel);
  const update = (e) => setModel({ ...model, equipment: model.equipment.map((x) => (x.slot === e.slot ? e : x)) });
  // tirar a peça: a lista do save é densa, e o editor renumera e ajusta a máscara ao gravar
  const [aviso, setAviso] = useState(null);
  const remove = (e) => {
    // tirar a mochila vestida apaga o contêiner dela, e com ele tudo o que estava guardado
    if (items[e.itemId]?.slot === 'Backpack' && model.extra.length) {
      setAviso(t('Não vou tirar a {item}: isso apagaria o contêiner dela e os {n} espaços guardados. Esvazie-o no jogo primeiro.', { item: itemName(e.itemId), n: model.extra.length }));
      return;
    }
    setAviso(null);
    setModel({ ...model, equipment: model.equipment.filter((x) => x.slot !== e.slot) });
    setSel(0);
  };

  // Base medida contra o jogo: com 10 por atributo, quatro dos seis fecham exatamente num
  // personagem de nível 47 (Constituição 2+10=12, Espírito 3+10=13, Força 0+10=10,
  // Inteligência 3+10=13). Resistência e Destreza ficam acima disso, e o que falta vem dos nós
  // que dependem do nível da Chama e do equipamento — nenhum dos dois está no save do
  // personagem, então o editor não soma e avisa.
  const BASE_ATRIBUTO = 10;
  const { daArvore, porChama } = atributosDaArvore(model.skills);
  const attrs = Object.fromEntries(ATRIBUTOS.map((a) => [a, BASE_ATRIBUTO + daArvore[a]]));
  const dependeDaChama = Object.values(porChama).some((n) => n > 0);

  return (
    <div className="screen character">
      {/* medalhão central */}
      <div className="doll-center">
        <img className="doll-orn" src={tex('levelup_ornament')} alt="" />
        <img className="doll-medal" src={tex('attribute_tabIllustration_basics')} alt="" />
        <div className="doll-badge">
          <img className="badge-shape" src={ttex('level_badge_gold')} alt="" />
          <span>{model.level}</span>
        </div>
        <div className="doll-name">{model.name}</div>
      </div>

      <div className="doll-stats">
        <div className="stat-row">
          <span className="caps small">{t('Nível')}</span>
          <Stepper value={model.level} min={1} max={50} onChange={(v) => setModel({ ...model, level: v })} />
        </div>
        <div className="stat-row">
          <span className="caps small">{t('XP no nível')}</span>
          <Stepper value={model.xp} min={0} max={1e9} steps={[100, 1000]} onChange={(v) => setModel({ ...model, xp: v })} />
        </div>
        <Separator />
        <div className="attr-grid">
          {ATRIBUTOS.map((a) => <div key={a}><span className="caps small">{t(a)}</span><b>{attrs[a]}</b></div>)}
        </div>
        <div className="hint">{t('Atributos calculados pelos nós da árvore (editáveis na aba Habilidades).')}</div>
        {dependeDaChama && <div className="hint warn">{t('O jogo soma ainda os nós que dependem do nível da Chama e os bônus do equipamento; nenhum dos dois está no save do personagem.')}</div>}
      </div>

      {EQUIP_SLOTS.map((s, i) => (
        <div key={i} className="doll-slot" style={{ left: s.x, top: s.y, width: SLOT }}>
          <div style={{ width: SLOT, height: SLOT, position: 'relative' }}>
            <SlotView entry={bySlot.get(i)} empty={s.tex} selected={sel === i} onClick={() => setSel(i)} />
          </div>
          <span className="doll-label">{t(s.name)}</span>
        </div>
      ))}
      <div className="doll-line left" />
      <div className="doll-line right" />

      <FancyPanel>
        <div className="caps small">{EQUIP_SLOTS[sel] ? t(EQUIP_SLOTS[sel].name) : t('Espaço {n}', { n: sel })}</div>
        {aviso && <div className="hint error">{aviso}</div>}
        {entry ? (
          <ItemEditor entry={entry} onChange={update} onRemove={() => remove(entry)} />
        ) : (
          <div className="empty-slot">
            <img className="empty-big" src={tex(EQUIP_SLOTS[sel]?.tex ?? 'equipmentslot_head')} alt="" />
            <div className="detail-sub">{t('Espaço vazio. Vista a peça no jogo; aqui dá para editar ou tirar as peças vestidas.')}</div>
          </div>
        )}
      </FancyPanel>
    </div>
  );
}
