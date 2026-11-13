// Add an item: a working dialog with the categories down the side, search, rarity and tier, the item grid and the
// chosen item's preview with its count. Double-click adds at once (on the Deck: A picks, and focus goes to Add).
// Categories and the new entry's shape are upstream's (src/Catalog.jsx, newEntry in src/gamedata.js).
// The list is sorted by name once, when the dialog opens; a search or a filter only filters it (sorting 4,000 names on
// each keystroke took a quarter of a second on a slow CPU). The grid is drawn a moment after the keystroke or the click
// that changed the filters (useDeferredValue), so the field and the buttons answer at once; its first screens come
// first and the rest of the 280 in a second pass; a cell that stays in the list is not drawn again (memo), and an icon
// below the fold loads when it is scrolled to.
import React, { memo, startTransition, useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import { Button, Modal } from '../../ui';
import { items, newEntry, itemName } from '../src/gamedata.js';
import { rarityColor } from '../src/ui.js';
import { t, localeAtual } from '../src/i18n.js';
import { Slot, Stepper, categoryWord, iconOf, rarityWord, tierOf } from './parts.jsx';
import { aboveStack, amountLimit, MAX_AMOUNT } from './amounts.js';
import { useEditorEnv } from './env.jsx';

const BASE_RARITY = { Common: 1, Uncommon: 2, Rare: 3, Epic: 4, Legendary: 5 };
const isArmor = (it) => it.category === 'Equipment' && /^Armour_(Head|UpperBody|Arms|LowerBody|Feet)$/.test(it.slot || '');
const CATEGORIES = [
  ['all', 'Todos', () => true],
  ['weapon', 'Armas', (it) => it.category === 'Weapons'],
  ['armor', 'Armaduras', (it) => isArmor(it) && !/^Vanity/.test(it.debug)],
  ['acc', 'Acessórios', (it) => it.category === 'Equipment' && !isArmor(it) && !/^(Vanity|VC_)/.test(it.debug)],
  ['ammo', 'Munição', (it) => it.category === 'Ammunition' && it.slot !== 'Spell'],
  ['magic', 'Magias e gemas', (it) => (it.category === 'Ammunition' && it.slot === 'Spell') || it.category === 'WeaponGem'],
  ['food', 'Consumíveis', (it) => it.category === 'Consumables' || it.category === 'AnimalFood'],
  ['mat', 'Materiais', (it) => it.category === 'Materials' || it.category === 'Currency'],
  ['tools', 'Ferramentas', (it) => ['Tools', 'FishingRod', 'Instrument'].includes(it.category)],
  ['build', 'Construção', (it) => it.category === 'BuildTools' || it.category === 'Blueprints'],
  ['vanity', 'Visual e coleções', (it) => it.category === 'Collectibles' || (it.category === 'Equipment' && /^Vanity/.test(it.debug))],
];
// built when the dialog opens: Kiln mod items merge into `items` after the module loads
export const allItems = () => {
  const by = new Intl.Collator(localeAtual()).compare;
  return Object.entries(items)
    .filter(([, it]) => !['Customization', 'ColorPalette'].includes(it.category) && (it.pt || it.en))
    .map(([id, it]) => ({ id: Number(id), ...it, name: itemName(id), r: BASE_RARITY[it.rarity] || 1, tier: tierOf(Number(id)), q: `${it.pt || ''} ${it.en || ''} ${it.debug}`.toLowerCase() }))
    .sort((a, b) => by(a.name, b.name));
};
/** The catalog's items with a search and filters, in the list's (name) order. */
export function filterItems(all, { cat = 'all', q = '', rar = 0, tier = 0 }) {
  const f = CATEGORIES.find((c) => c[0] === cat)[2];
  const s = q.trim().toLowerCase();
  return all.filter((it) => f(it) && (!s || it.q.includes(s)) && (!rar || it.r === rar) && (!tier || it.tier === tier));
}
const LIMIT = 280;
// what the grid draws at once: more than a screen of it (9 columns by 6 rows on the desktop, 12 by 6 on the Deck)
const FIRST = 96;

/** One item of the grid. `it` is the list's own object, so a cell that stays through a search is not drawn again. */
const Cell = memo(function Cell({ it, selected, onChoose, onAdd }) {
  const entry = useMemo(() => ({ itemId: it.id, count: it.maxStack }), [it]);
  return (
    <Slot entry={entry} selected={selected} size={58} title={it.name} label={it.name} lazy
      deckId={`cat-${it.id}`} deckA={t('Escolher')} onClick={() => onChoose(it)} onDoubleClick={() => onAdd(it, it.maxStack > 1 ? it.maxStack : 1)} />
  );
});

/** The item grid and the line under it. Drawn only when the list or the pick changes, not on each keystroke: the
 *  first screens of a new list at once, the cells below them in a second pass. */
const Grid = memo(function Grid({ list, pickId, onChoose, onAdd }) {
  // the list whose cells below the first screens are drawn too (a state set after the list is on screen, not a second
  // useDeferredValue: inside the deferred pass that brings a new list React would not defer again)
  const [whole, setWhole] = useState(null);
  useEffect(() => { startTransition(() => setWhole(list)); }, [list]);
  const n = whole === list ? LIMIT : FIRST;
  return (
    <>
      <div className="ke-cat-grid" role="listbox" aria-label={t('Itens')}>
        {list.slice(0, n).map((it) => <Cell key={it.id} it={it} selected={pickId === it.id} onChoose={onChoose} onAdd={onAdd} />)}
        {!list.length && <span className="ke-note">{t('Nada com esses filtros.')}</span>}
      </div>
      <span className="ke-note">{list.length > LIMIT ? t('{n} itens (mostrando {limite}; refine a busca)', { n: list.length, limite: LIMIT }) : t('{n} itens', { n: list.length })}</span>
    </>
  );
});

export function Catalog({ open, target, onAdd, onClose, level }) {
  const env = useEditorEnv();
  if (!open) return null;
  if (env.deck) {
    const Frame = env.Dialog;
    return <Frame name="editor-catalog" title={t('Adicionar item')} onClose={onClose} wide><CatalogBody target={target} onAdd={onAdd} onClose={onClose} level={level} /></Frame>;
  }
  return (
    <Modal open onClose={onClose} title={t('Adicionar item')} width={1100}
      footer={null}>
      <CatalogBody target={target} onAdd={onAdd} onClose={onClose} level={level} />
    </Modal>
  );
}

function CatalogBody({ target, onAdd, onClose, level }) {
  const ALL = useMemo(allItems, []);
  const [cat, setCat] = useState('all');
  const [q, setQ] = useState('');
  const [rar, setRar] = useState(0);
  const [tier, setTier] = useState(0);
  const [pick, setPick] = useState(null);
  const [qty, setQty] = useState(1);
  const env = useEditorEnv();
  const search = useRef(null);
  const addBtn = useRef(null);
  useEffect(() => { if (!env.deck) search.current?.focus(); }, []);
  // the filters the grid shows follow the ones chosen at a lower priority: typing does not wait for 280 cells
  const filters = useMemo(() => ({ cat, q, rar, tier }), [cat, q, rar, tier]);
  const shown = useDeferredValue(filters);
  const list = useMemo(() => filterItems(ALL, shown), [ALL, shown]);
  const counts = useMemo(() => Object.fromEntries(CATEGORIES.map(([k, , f]) => [k, ALL.filter(f).length])), [ALL]);
  const choose = (it) => {
    setPick(it); setQty(it.maxStack > 1 ? it.maxStack : 1);
    // the Deck: A picks, then A again adds (the amount is one step up)
    if (env.deck) requestAnimationFrame(() => addBtn.current?.focus());
  };
  const add = (it = pick, n = qty) => { if (it && target !== null) { onAdd(newEntry(it.id, target, n, undefined, level)); onClose(); } };
  // the same two functions for the grid's whole life (its cells are memoized); they run the latest render's
  const latest = useRef({ choose, add });
  latest.current = { choose, add };
  const onChoose = useCallback((it) => latest.current.choose(it), []);
  const onAddNow = useCallback((it, n) => latest.current.add(it, n), []);

  return (
    <div className="ke-cat">
      <nav className="ke-cat-rail" aria-label={t('Categorias')}>
        {CATEGORIES.map(([k, label]) => (
          <button key={k} type="button" className={'k-sub' + (cat === k ? ' on' : '')} onClick={() => setCat(k)} aria-current={cat === k || undefined} data-deck-id={`cat-rail-${k}`}>
            <i />{t(label)}<span className="k-mono ke-cat-n">{counts[k]}</span>
          </button>
        ))}
      </nav>
      <div className="ke-cat-main">
        <input ref={search} className="k-input" placeholder={env.deck ? t('Buscar por nome (Steam + X: teclado)') : t('Buscar por nome…')} value={q} onChange={(e) => setQ(e.target.value)} aria-label={t('Buscar')}
          data-deck-id="cat-search" data-deck-a={t('Digitar')} data-deck-autofocus={env.deck ? '' : undefined}
          onKeyDown={(e) => { if (env.deck && e.key === 'Enter') { e.preventDefault(); e.currentTarget.blur(); document.querySelector('.ke-cat-grid .ke-slot')?.focus(); } }} />
        <div className="ke-cat-filters">
          <span className="k-seg" role="radiogroup" aria-label={t('Raridade')}>
            <button type="button" className={rar === 0 ? 'on' : ''} onClick={() => setRar(0)}>{t('Todas')}</button>
            {[1, 2, 3, 4, 5].map((r) => (
              <button key={r} type="button" className={rar === r ? 'on' : ''} onClick={() => setRar(rar === r ? 0 : r)}>
                <i className="ke-mini-dia" style={{ background: rarityColor(r) }} />{rarityWord(r)}
              </button>
            ))}
          </span>
          <span className="k-seg" role="radiogroup" aria-label={t('Tier')}>
            <span className="k-label">{t('Tier')}</span>
            <button type="button" className={tier === 0 ? 'on' : ''} onClick={() => setTier(0)}>{t('Todos')}</button>
            {[1, 2, 3, 4, 5, 6, 7].map((n) => <button key={n} type="button" className={tier === n ? 'on' : ''} onClick={() => setTier(tier === n ? 0 : n)}>{n}</button>)}
          </span>
        </div>
        <Grid list={list} pickId={pick?.id ?? null} onChoose={onChoose} onAdd={onAddNow} />
      </div>
      <aside className="ke-cat-pick">
        {pick ? (
          <>
            <span className="ke-insp-icon big" style={{ '--r': rarityColor(pick.r) }}><img src={iconOf(pick.id)} alt="" /></span>
            <span className="k-sc ke-insp-rar" style={{ color: rarityColor(pick.r) }}>{rarityWord(pick.r)}</span>
            <h3 className="ke-insp-name">{pick.name}</h3>
            <span className="ke-insp-kind">{[categoryWord(pick.category), pick.tier && t('Tier {n}', { n: pick.tier })].filter(Boolean).join(' · ')}</span>
            {pick.maxStack > 1 && (
              <label className="ke-cat-qty"><span className="k-label">{t('Quantidade')}</span>
                <Stepper value={qty} min={1} max={amountLimit(pick.maxStack)} big={10} label={t('Quantidade')} onChange={setQty}
                  presets={[{ label: t('Pilha do jogo ({n})', { n: pick.maxStack }), value: pick.maxStack }, { label: t('Máximo ({n})', { n: MAX_AMOUNT }), value: MAX_AMOUNT }]} /></label>
            )}
            {aboveStack(pick.maxStack, qty) && (
              <span className="ke-note warn">{t('acima da pilha do jogo ({n}): para mundos com um mod que aumenta as pilhas', { n: pick.maxStack })}</span>
            )}
            <span className="ke-note">{target === null ? t('Destino: inventário cheio') : t('Destino: espaço {n}', { n: target + 1 })}</span>
            <span className="k-spacer" />
            <span className="k-row" style={{ justifyContent: 'flex-end' }}>
              <Button variant="ghost" onClick={onClose}>{t('Cancelar')}</Button>
              <Button ref={addBtn} variant="primary" disabled={target === null} onClick={() => add()} data-deck-id="cat-add">{t('Adicionar')}</Button>
            </span>
          </>
        ) : (
          <div className="ke-empty"><span className="ke-empty-word">{t('Escolha um item.')}</span><span className="ke-note">{env.deck ? t('A escolhe; A de novo adiciona.') : t('Duplo clique adiciona direto.')}</span></div>
        )}
      </aside>
    </div>
  );
}
