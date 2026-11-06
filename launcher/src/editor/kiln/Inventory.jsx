// Inventory: the backpack grid, the swap bar (Alt) and the action bar (1-8), with the inspector beside them. Drag to
// move, double-click an empty slot to add, Delete removes, Ctrl+D duplicates, I opens the catalog. Slot keys and the
// layout are upstream's (shared/inventory.mjs).
import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Button } from '../../ui';
import { items, itemName } from '../src/gamedata.js';
import capacities from '../data/backpack-capacities.json';
import { inventoryLayout, entryAt, replaceSlots, keyOf } from '../shared/inventory.mjs';
import { t } from '../src/i18n.js';
import { Inspector } from './Inspector.jsx';
import { Catalog } from './Catalog.jsx';
import { Slot } from './parts.jsx';

export default function Inventory({ model, setModel }) {
  const [sel, setSel] = useState(null);
  const [catalog, setCatalog] = useState(false);
  const [dragFrom, setDragFrom] = useState(null);
  const layout = inventoryLayout(model, items, capacities);
  const bySlot = new Map(layout.all.map((k) => [k, entryAt(model, k)]).filter(([, e]) => e));
  const entry = sel !== null ? bySlot.get(sel) : null;
  const firstFree = layout.all.find((k) => !bySlot.has(k)) ?? null;
  const change = (edits) => setModel(replaceSlots(model, edits));
  const remove = () => { if (entry) change([[sel, null]]); };
  const target = sel !== null && !bySlot.has(sel) ? sel : firstFree;
  const add = (e) => { if (target !== null) { change([[target, e]]); setSel(target); } };
  const duplicate = () => { if (entry && firstFree !== null) { change([[firstFree, entry]]); setSel(firstFree); } };
  const move = (from, to) => {
    if (from === to) return;
    const a = bySlot.get(from), b = bySlot.get(to);
    if (!a) return;
    change([[from, b || null], [to, a]]);
    setSel(to);
  };

  useEffect(() => {
    const k = (e) => {
      if (catalog || e.target.closest?.('input, select, textarea, [role="dialog"]')) return;
      if (e.key === 'Delete' && entry) remove();
      if (e.key === 'i' || e.key === 'I') setCatalog(true);
      if (e.ctrlKey && (e.key === 'd' || e.key === 'D')) { e.preventDefault(); duplicate(); }
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  });

  const cell = (key, n) => {
    const e = bySlot.get(key);
    return (
      <div key={key} className="ke-cell"
        onDragOver={(ev) => { ev.preventDefault(); ev.dataTransfer.dropEffect = 'move'; }}
        onDrop={(ev) => { ev.preventDefault(); const from = ev.dataTransfer.getData('text/plain') || dragFrom; if (from && bySlot.has(from)) move(from, key); setDragFrom(null); }}>
        <Slot entry={e} n={n} selected={sel === key} drop={dragFrom !== null && dragFrom !== key} title={e ? itemName(e.itemId) : undefined}
          onClick={() => setSel(key)} onDoubleClick={() => { if (!e) { setSel(key); setCatalog(true); } }}
          drag={e ? { draggable: true, onDragStart: (ev) => { ev.dataTransfer.setData('text/plain', key); ev.dataTransfer.effectAllowed = 'move'; setDragFrom(key); }, onDragEnd: () => setDragFrom(null) } : undefined} />
      </div>
    );
  };
  const used = layout.bag.filter((k) => bySlot.has(k)).length;
  // the backpack grid scrolls inside its group: fade its foot while more rows are below
  const scroller = useRef(null);
  const [more, setMore] = useState(false);
  const look = () => { const el = scroller.current; if (el) setMore(el.scrollTop + el.clientHeight < el.scrollHeight - 4); };
  useLayoutEffect(() => {
    look();
    const ro = new ResizeObserver(look);
    if (scroller.current) ro.observe(scroller.current);
    return () => ro.disconnect();
  }, [layout.bag.length]);
  const slotNo = (key) => (layout.bag.indexOf(key) >= 0 ? layout.bag.indexOf(key) + 1 : Number(key.split(':')[1]) + 1);

  return (
    <div className="ke-split">
      <div className="ke-main">
        <section className="ke-group ke-group-grow">
          <header className="ke-group-head">
            <h2 className="k-card-title">{t('Mochila')}</h2>
            <span className="k-card-sub">{t('{n} de {total} espaços', { n: used, total: layout.bag.length })}</span>
            <span className="k-spacer" />
            <Button size="s" onClick={() => setCatalog(true)} title={t('Adicionar item') + ' (I)'}>{t('Adicionar item')}</Button>
          </header>
          <div className={'ke-grid-scroll' + (more ? ' more' : '')} ref={scroller} onScroll={look}><div className="ke-grid">{layout.bag.map((k) => cell(k))}</div></div>
        </section>
        <section className="ke-group">
          <header className="ke-group-head">
            <h2 className="k-card-title">{t('Barra alternativa')}</h2>
            <span className="k-card-sub">{t('Alt troca com a barra atual')}</span>
          </header>
          <div className="ke-grid">{Array.from({ length: 8 }, (_, c) => cell(keyOf('main', 16 + c)))}</div>
        </section>
        <section className="ke-group">
          <header className="ke-group-head"><h2 className="k-card-title">{t('Barra de ação')}</h2></header>
          <div className="ke-grid">{Array.from({ length: 8 }, (_, c) => cell(keyOf('main', 24 + c), c + 1))}</div>
        </section>
        <p className="ke-note">{t('Arraste para mover · duplo clique em espaço vazio para adicionar · Delete remove · Ctrl+D duplica')}</p>
      </div>
      <Inspector entry={entry} onChange={(e) => change([[sel, e]])} onRemove={remove} onDuplicate={firstFree !== null ? duplicate : null}
        empty={sel === null ? (
          <div className="ke-empty"><span className="ke-empty-word">{t('Nenhum item selecionado.')}</span><span className="ke-note">{t('Escolha um espaço para ver e mudar o item.')}</span></div>
        ) : (
          <div className="ke-empty">
            <span className="ke-empty-word">{t('Espaço {n} vazio', { n: slotNo(sel) })}</span>
            <span className="ke-note">{t('Escolha um item no catálogo para colocar aqui.')}</span>
            <Button onClick={() => setCatalog(true)}>{t('Escolher item')}</Button>
          </div>
        )} />
      <Catalog open={catalog} target={target === null ? null : Number(target.split(':')[1])} onAdd={add} onClose={() => setCatalog(false)} level={model.level} />
    </div>
  );
}
