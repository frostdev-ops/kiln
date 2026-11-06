// Add an item: a working dialog with the categories down the side, search, rarity and tier, the item grid and the
// chosen item's preview with its count. Double-click adds at once. Categories and the new entry's shape are upstream's
// (src/Catalog.jsx, newEntry in src/gamedata.js).
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Button, Modal } from '../../ui';
import { items, newEntry, itemName } from '../src/gamedata.js';
import { itemIcon, rarityColor } from '../src/ui.js';
import { t, localeAtual } from '../src/i18n.js';
import { Slot, Stepper, categoryWord, rarityWord, tierOf } from './parts.jsx';

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
const allItems = () => Object.entries(items)
  .filter(([, it]) => !['Customization', 'ColorPalette'].includes(it.category) && (it.pt || it.en))
  .map(([id, it]) => ({ id: Number(id), ...it, r: BASE_RARITY[it.rarity] || 1, tier: tierOf(Number(id)), q: `${it.pt || ''} ${it.en || ''} ${it.debug}`.toLowerCase() }));
const LIMIT = 280;

export function Catalog({ open, target, onAdd, onClose, level }) {
  if (!open) return null;
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
  const search = useRef(null);
  useEffect(() => { search.current?.focus(); }, []);
  const filter = CATEGORIES.find((c) => c[0] === cat)[2];
  const list = useMemo(() => {
    const s = q.trim().toLowerCase();
    return ALL.filter((it) => filter(it) && (!s || it.q.includes(s)) && (!rar || it.r === rar) && (!tier || it.tier === tier))
      .sort((a, b) => itemName(a.id).localeCompare(itemName(b.id), localeAtual()));
  }, [ALL, cat, q, rar, tier]);
  const counts = useMemo(() => Object.fromEntries(CATEGORIES.map(([k, , f]) => [k, ALL.filter(f).length])), [ALL]);
  const choose = (it) => { setPick(it); setQty(it.maxStack > 1 ? it.maxStack : 1); };
  const add = (it = pick, n = qty) => { if (it && target !== null) { onAdd(newEntry(it.id, target, n, undefined, level)); onClose(); } };

  return (
    <div className="ke-cat">
      <nav className="ke-cat-rail" aria-label={t('Categorias')}>
        {CATEGORIES.map(([k, label]) => (
          <button key={k} type="button" className={'k-sub' + (cat === k ? ' on' : '')} onClick={() => setCat(k)} aria-current={cat === k || undefined}>
            <i />{t(label)}<span className="k-mono ke-cat-n">{counts[k]}</span>
          </button>
        ))}
      </nav>
      <div className="ke-cat-main">
        <input ref={search} className="k-input" placeholder={t('Buscar por nome…')} value={q} onChange={(e) => setQ(e.target.value)} aria-label={t('Buscar')} />
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
        <div className="ke-cat-grid" role="listbox" aria-label={t('Itens')}>
          {list.slice(0, LIMIT).map((it) => (
            <Slot key={it.id} entry={{ itemId: it.id, count: it.maxStack }} selected={pick?.id === it.id} size={58} title={itemName(it.id)}
              onClick={() => choose(it)} onDoubleClick={() => add(it, it.maxStack > 1 ? it.maxStack : 1)} />
          ))}
          {!list.length && <span className="ke-note">{t('Nada com esses filtros.')}</span>}
        </div>
        <span className="ke-note">{list.length > LIMIT ? t('{n} itens (mostrando {limite}; refine a busca)', { n: list.length, limite: LIMIT }) : t('{n} itens', { n: list.length })}</span>
      </div>
      <aside className="ke-cat-pick">
        {pick ? (
          <>
            <span className="ke-insp-icon big" style={{ '--r': rarityColor(pick.r) }}><img src={itemIcon(pick.id)} alt="" /></span>
            <span className="k-sc ke-insp-rar" style={{ color: rarityColor(pick.r) }}>{rarityWord(pick.r)}</span>
            <h3 className="ke-insp-name">{itemName(pick.id)}</h3>
            <span className="ke-insp-kind">{[categoryWord(pick.category), pick.tier && t('Tier {n}', { n: pick.tier })].filter(Boolean).join(' · ')}</span>
            {pick.maxStack > 1 && (
              <label className="ke-cat-qty"><span className="k-label">{t('Quantidade')}</span>
                <Stepper value={qty} min={1} max={pick.maxStack} big={10} label={t('Quantidade')} onChange={setQty} /></label>
            )}
            <span className="ke-note">{target === null ? t('Destino: inventário cheio') : t('Destino: espaço {n}', { n: target + 1 })}</span>
            <span className="k-spacer" />
            <span className="k-row" style={{ justifyContent: 'flex-end' }}>
              <Button variant="ghost" onClick={onClose}>{t('Cancelar')}</Button>
              <Button variant="primary" disabled={target === null} onClick={() => add()}>{t('Adicionar')}</Button>
            </span>
          </>
        ) : (
          <div className="ke-empty"><span className="ke-empty-word">{t('Escolha um item.')}</span><span className="ke-note">{t('Duplo clique adiciona direto.')}</span></div>
        )}
      </aside>
    </div>
  );
}
