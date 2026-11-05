// Catálogo de itens: categorias com os ícones do menu de produção do jogo, busca, filtros de
// raridade e tier, grade de ícones e prévia antes de adicionar.
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { items, CATEGORY_PT, newEntry, itemName } from './gamedata.js';
import { ttex, itemIcon, rarityColor, RARITY_PT } from './ui.js';
import { FancyPanel } from './ItemEditor.jsx';
import { GameButton, Stepper, SlotView, tierOf } from './components.jsx';
import { t, localeAtual, idiomaAtual, campoDeNome } from './i18n.js';

const BASE_RARITY = { Common: 1, Uncommon: 2, Rare: 3, Epic: 4, Legendary: 5 };
const isArmor = (it) => it.category === 'Equipment' && /^Armour_(Head|UpperBody|Arms|LowerBody|Feet)$/.test(it.slot || '');
export const CATEGORIES = [
  ['all', 'Todos', 'recipe_category_essential', () => true],
  ['weapon', 'Armas', 'recipe_category_weapon', (it) => it.category === 'Weapons'],
  ['armor', 'Armaduras', 'recipe_category_armor', (it) => isArmor(it) && !/^Vanity/.test(it.debug)],
  ['acc', 'Acessórios', 'recipe_category_upgrade', (it) => it.category === 'Equipment' && !isArmor(it) && !/^(Vanity|VC_)/.test(it.debug)],
  ['ammo', 'Munição', 'recipe_category_ammo', (it) => it.category === 'Ammunition' && it.slot !== 'Spell'],
  ['magic', 'Magias e gemas', 'recipe_category_magical', (it) => (it.category === 'Ammunition' && it.slot === 'Spell') || it.category === 'WeaponGem'],
  ['food', 'Consumíveis', 'recipe_category_supplies', (it) => it.category === 'Consumables' || it.category === 'AnimalFood'],
  ['mat', 'Materiais', 'recipe_category_resources', (it) => it.category === 'Materials' || it.category === 'Currency'],
  ['tools', 'Ferramentas', 'recipe_category_survival', (it) => ['Tools', 'FishingRod', 'Instrument'].includes(it.category)],
  ['build', 'Construção', 'recipe_category_blocks', (it) => it.category === 'BuildTools' || it.category === 'Blueprints'],
  ['vanity', 'Visual e coleções', 'recipe_category_vanity', (it) => it.category === 'Collectibles' || (it.category === 'Equipment' && /^Vanity/.test(it.debug))],
];
// Kiln: built when the catalog opens, not at import: the mod overlay merges into `items` later
const allItems = () => Object.entries(items)
  .filter(([, it]) => !['Customization', 'ColorPalette'].includes(it.category) && (it.pt || it.en))
  .map(([id, it]) => ({ id: Number(id), ...it, r: BASE_RARITY[it.rarity] || 1, tier: tierOf(Number(id)), q: `${it.pt || ''} ${it.en || ''} ${it.debug}`.toLowerCase() }));
// Sem ordenar aqui: a ordem alfabética depende do idioma e isto roda uma vez só, na importação.
// `q` junta os dois idiomas de propósito, para a busca achar o item escrevendo em qualquer um.
const LIMIT = 280;

export default function Catalog({ target, onAdd, onClose, nivel }) {
  const ALL = useMemo(allItems, []);
  const [cat, setCat] = useState('all');
  const [q, setQ] = useState('');
  const [rar, setRar] = useState(0);
  const [tier, setTier] = useState(0);
  const [pick, setPick] = useState(null);
  const [qty, setQty] = useState(1);
  const search = useRef(null);
  useEffect(() => { search.current?.focus(); }, []);
  useEffect(() => {
    const k = (e) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', k); return () => window.removeEventListener('keydown', k);
  }, [onClose]);

  const filter = CATEGORIES.find((c) => c[0] === cat)[3];
  const list = useMemo(() => {
    const busca = q.trim().toLowerCase();
    return ALL.filter((it) => filter(it) && (!busca || it.q.includes(busca)) && (!rar || it.r === rar) && (!tier || it.tier === tier))
      .sort((a, b) => itemName(a.id).localeCompare(itemName(b.id), localeAtual()));
    // `idiomaAtual()` entra nas dependências porque o nome e a ordem mudam com o idioma
  }, [cat, q, rar, tier, idiomaAtual()]);
  const counts = useMemo(() => Object.fromEntries(CATEGORIES.map(([k, , , f]) => [k, ALL.filter(f).length])), []);

  const choose = (it) => { setPick(it); setQty(it.maxStack > 1 ? it.maxStack : 1); };
  const add = () => { if (pick && target !== null) { onAdd(newEntry(pick.id, target, qty, undefined, nivel)); onClose(); } };

  return (
    <div className="catalog-wrap" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="catalog">
        <div className="cat-rail">
          {CATEGORIES.map(([k, label, icon]) => (
            <button key={k} className={`cat ${cat === k ? 'on' : ''}`} onClick={() => setCat(k)} title={t(label)}>
              <img className="cat-icon" src={ttex(`${icon}_${cat === k ? 'gold' : 'light'}`)} alt="" />
              <span className="cat-label">{t(label)}</span>
              <span className="cat-count">{counts[k]}</span>
            </button>
          ))}
        </div>
        <div className="cat-main">
          <div className="cat-top">
            <input ref={search} className="g-input cat-search" placeholder={t('Buscar por nome (português ou inglês)…')} value={q} onChange={(e) => setQ(e.target.value)} />
            <div className="chips">
              <button className={`chip ${rar === 0 ? 'on' : ''}`} onClick={() => setRar(0)}>{t('Todas')}</button>
              {[1, 2, 3, 4, 5].map((r) => (
                <button key={r} className={`chip ${rar === r ? 'on' : ''}`} onClick={() => setRar(rar === r ? 0 : r)} style={{ color: rarityColor(r) }}>
                  <img className="chip-gem" src={ttex(`gem_${r}`)} alt="" />{t(RARITY_PT[r])}
                </button>
              ))}
            </div>
            <div className="chips">
              <span className="chip-title">{t('Tier')}</span>
              <button className={`chip ${tier === 0 ? 'on' : ''}`} onClick={() => setTier(0)}>{t('Todos')}</button>
              {[1, 2, 3, 4, 5, 6, 7].map((n) => <button key={n} className={`chip ${tier === n ? 'on' : ''}`} onClick={() => setTier(tier === n ? 0 : n)}>{n}</button>)}
              <span className="chip-count">{list.length > LIMIT ? t('{n} itens (mostrando {limite}; refine a busca)', { n: list.length, limite: LIMIT }) : t('{n} itens', { n: list.length })}</span>
            </div>
          </div>
          <div className="cat-grid">
            {list.slice(0, LIMIT).map((it) => (
              <div key={it.id} className="cat-tile" onClick={() => choose(it)} onDoubleClick={() => { choose(it); setTimeout(add, 0); }} title={itemName(it.id)}>
                <SlotView entry={{ itemId: it.id, count: it.maxStack }} selected={pick?.id === it.id} inCatalog />
              </div>
            ))}
          </div>
        </div>
        <FancyPanel style={{ position: 'relative', left: 0, top: 0, width: 620, height: '100%' }}>
          {!pick && <div className="detail-empty" style={{ marginTop: 300 }}>{t('Escolha um item.')}</div>}
          {pick && (
            <>
              <div className="ed-icon big" style={{ background: `radial-gradient(circle, ${rarityColor(pick.r).replace(/[\d.]+\)$/, '0.45)')} 0%, transparent 70%)` }}>
                <img src={itemIcon(pick.id)} alt="" />
              </div>
              <div className="detail-title" style={{ color: rarityColor(pick.r), textAlign: 'center' }}>{itemName(pick.id)}</div>
              <div className="detail-sub" style={{ textAlign: 'center' }}>
                {t(CATEGORY_PT[pick.category] || pick.category)}{pick.tier ? ` · ${t('Tier {n}', { n: pick.tier })}` : ''} · {t(RARITY_PT[pick.r])}
              </div>
              {/* o nome no outro idioma, que ajuda a casar com guias e vídeos escritos nele */}
              {pick.en && pick.pt && (
                <div className="hint" style={{ textAlign: 'center' }}>{campoDeNome() === 'pt' ? pick.en : pick.pt}</div>
              )}
              {pick.maxStack > 1 && (
                <div className="ed-row"><div className="ed-label">{t('Quantidade')}</div>
                  <div className="ed-ctrl"><Stepper value={qty} min={1} max={pick.maxStack} steps={[1, 10]} onChange={setQty} /></div></div>
              )}
              <div className="hint">{target === null ? t('Destino: inventário cheio') : t('Destino: espaço {n}', { n: target })}</div>
              <div style={{ marginTop: 'auto' }}>
                <GameButton onClick={add} disabled={target === null}>{t('Adicionar')}</GameButton>
              </div>
            </>
          )}
        </FancyPanel>
        <button className="cat-close" onClick={onClose}><span className="key">ESC</span>{t('Fechar')}</button>
      </div>
    </div>
  );
}
