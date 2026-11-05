// Tela "Mochila", com a geometria medida no jogo em 2560x1440:
// grade 8x4 em (228,158), espaço de 118 px com passo de 132 px; barra alternativa (ALT, espaços 16-23)
// em y=1046 e barra de ação atual (espaços 24-31) em y=1192; painel de detalhes em (1400,150)-(2380,1320).
import React, { useEffect, useState } from 'react';
import { items } from './gamedata.js';
import capacities from '../data/backpack-capacities.json';
import { inventoryLayout, entryAt, replaceSlots, keyOf } from '../shared/inventory.mjs';
import { tex } from './ui.js';
import ItemEditor, { FancyPanel } from './ItemEditor.jsx';
import Catalog from './Catalog.jsx';
import { GameButton, SlotView } from './components.jsx';
import { t } from './i18n.js';

const SLOT = 118, PITCH = 132, GRID_X = 228, GRID_Y = 158;
export default function Backpack({ model, setModel }) {
  const [sel, setSel] = useState(null);
  const [catalog, setCatalog] = useState(false);
  const [dragFrom, setDragFrom] = useState(null);
  const layout = inventoryLayout(model, items, capacities);
  const bySlot = new Map(layout.all.map(key => [key,entryAt(model,key)]).filter(([,e]) => e));
  const entry = sel !== null ? bySlot.get(sel) : null;
  const firstFree = layout.all.find(key => !bySlot.has(key)) ?? null;
  const change = edits => setModel(replaceSlots(model,edits));
  const update = e => change([[sel,e]]);
  const remove = () => { if (entry) change([[sel,null]]); };
  const target = sel !== null && !bySlot.has(sel) ? sel : firstFree;
  const add = e => { if (target !== null) { change([[target,e]]); setSel(target); } };
  const duplicate = () => {
    if (entry && firstFree !== null) { change([[firstFree,entry]]); setSel(firstFree); }
  };
  const move = (from,to) => {
    if (from === to) return;
    const a=bySlot.get(from), b=bySlot.get(to);
    if (!a) return;
    change([[from,b || null],[to,a]]); setSel(to);
  };

  useEffect(() => {
    const k = (e) => {
      if (catalog || e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
      if (e.key === 'Delete' && entry) remove();
      if ((e.key === 'i' || e.key === 'I')) setCatalog(true);
      if (e.ctrlKey && (e.key === 'd' || e.key === 'D')) { e.preventDefault(); duplicate(); }
    };
    window.addEventListener('keydown', k); return () => window.removeEventListener('keydown', k);
  });

  const slotAt = (s, x, y, badge) => (
    <div key={s} className={`slot-pos ${dragFrom !== null && dragFrom !== s ? 'drop-ok' : ''}`} style={{ left: x, top: y, width: SLOT, height: SLOT }}
      onDragOver={(e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; }} onDrop={(e) => { e.preventDefault(); const from = e.dataTransfer.getData('text/plain') || dragFrom; if (from !== null && bySlot.has(from)) move(from, s); setDragFrom(null); }}
      onDoubleClick={() => { if (!bySlot.has(s)) { setSel(s); setCatalog(true); } }}>
      <SlotView entry={bySlot.get(s)} selected={sel === s} badge={badge} onClick={() => setSel(s)}
        drag={bySlot.has(s) ? { draggable: true, onDragStart: (e) => { e.dataTransfer.setData('text/plain', s); e.dataTransfer.effectAllowed = 'move'; setDragFrom(s); }, onDragEnd: () => setDragFrom(null) } : undefined} />
    </div>
  );

  return (
    <div className="screen backpack">
      <div className="bag-grid-scroll" style={{position:'absolute',left:GRID_X-8,top:GRID_Y-8,width:1090,height:760,overflowY:'auto',overflowX:'hidden'}}>
        <div style={{position:'relative',height:Math.ceil(layout.bag.length/8)*PITCH+8}}>
          {layout.bag.map((key,i)=>slotAt(key,8+(i%8)*PITCH,8+Math.floor(i/8)*PITCH))}
        </div>
      </div>
      {Array.from({ length: 8 }, (_, c) => slotAt(keyOf('main',16 + c), GRID_X + c * PITCH, 1046))}
      {Array.from({ length: 8 }, (_, c) => slotAt(keyOf('main',24 + c), GRID_X + c * PITCH, 1192, c + 1))}
      <div className="separator" style={{ left: 210, top: 1013, width: 1100 }} />
      <div className="bar-label" style={{ left: 40, top: 1080 }}><span className="key">ALT</span><span>{t('Trocar')}</span></div>
      {/* a quebra de linha é do rótulo, não do layout: cada idioma escolhe onde ela cai */}
      <div className="bar-label" style={{ left: 30, top: 1232 }}><span className="caps linhas">{t('Barra de\nação atual')}</span></div>
      <div className="bar-frame" style={{ left: 197, top: 1180, width: 1100, height: 144 }} />
      <div className="bag-tools" style={{ left: 228, top: 940 }}>
        <GameButton onClick={() => setCatalog(true)}><img className="btn-ico" src={tex('recipe_category_essential')} alt="" />{t('Adicionar item')}</GameButton>
        <span className="hint">{t('Arraste para mover · duplo clique em espaço vazio para adicionar · Delete remove · Ctrl+D duplica')}</span>
      </div>
      <FancyPanel>
        {entry && <ItemEditor entry={entry} onChange={update} onRemove={remove} onDuplicate={firstFree !== null ? duplicate : null} />}
        {!entry && sel === null && <div className="detail-empty">{t('Nenhum Item Selecionado.')}</div>}
        {!entry && sel !== null && (
          <div className="empty-slot">
            <div className="detail-title">{t('Espaço {n} vazio', { n: layout.bag.indexOf(sel) >= 0 ? layout.bag.indexOf(sel) + 1 : Number(sel.split(':')[1]) + 1 })}</div>
            <div className="detail-sub">{t('Escolha um item no catálogo para colocar aqui.')}</div>
            <GameButton onClick={() => setCatalog(true)}>{t('Escolher item')}</GameButton>
          </div>
        )}
      </FancyPanel>
      {catalog && <Catalog target={target === null ? null : Number(target.split(':')[1])} onAdd={add} onClose={() => setCatalog(false)} nivel={model.level} />}
    </div>
  );
}
