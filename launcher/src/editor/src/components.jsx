// Peças de interface montadas com as texturas do jogo.
import React from 'react';
import { items } from './gamedata.js';
import { ui, tex, ttex, itemIcon, rgba, rarityColor, RARITY_PT } from './ui.js';
import { t } from './i18n.js';

// botão decorado do jogo: fancy_button_{left,middle,right}_{base,destructive}
export function GameButton({ children, onClick, danger, disabled, small, title }) {
  const v = danger ? 'destructive' : 'base';
  return (
    <button className={`gbtn ${small ? 'small' : ''} ${disabled ? 'off' : ''}`} onClick={disabled ? undefined : onClick} title={title}>
      <span className="gbtn-l" style={{ backgroundImage: `url(${tex(`fancy_button_left_${v}`)})` }} />
      <span className="gbtn-m" style={{ backgroundImage: `url(${tex(`fancy_button_middle_${v}`)})` }}>{children}</span>
      <span className="gbtn-r" style={{ backgroundImage: `url(${tex(`fancy_button_right_${v}`)})` }} />
    </button>
  );
}

export function Stepper({ value, onChange, min = 0, max = 65535, steps = [1], extra }) {
  const clamp = (v) => Math.max(min, Math.min(max, v));
  return (
    <div className="stepper">
      {[...steps].reverse().map((s) => <button key={`-${s}`} className="step" onClick={() => onChange(clamp(value - s))}>−{s > 1 ? s : ''}</button>)}
      <input className="step-val" type="number" value={value} onChange={(e) => onChange(clamp(Math.floor(Number(e.target.value)) || 0))} />
      {steps.map((s) => <button key={`+${s}`} className="step" onClick={() => onChange(clamp(value + s))}>+{s > 1 ? s : ''}</button>)}
      {extra}
    </div>
  );
}

// gemas de raridade (item_rarity tingido com as cores de raridade do jogo)
export function RarityGems({ value, onChange }) {
  return (
    <div className="gems">
      {[1, 2, 3, 4, 5].map((r) => (
        <button key={r} className={`gem ${value === r ? 'on' : ''}`} title={t(RARITY_PT[r])} onClick={() => onChange(r)}>
          <img src={ttex(`gem_${r}`)} alt="" />
        </button>
      ))}
      <span className="gem-label" style={{ color: rarityColor(value) }}>{t(RARITY_PT[value])}</span>
    </div>
  );
}

// marcadores de melhoria: um por nível de raridade
export function UpgradePips({ value, max, onChange }) {
  return (
    <div className="pips">
      {Array.from({ length: max }, (_, i) => (
        <button key={i} className={`pip ${i < value ? 'on' : ''}`} onClick={() => onChange(i + 1 === value ? i : i + 1)} />
      ))}
      <span className="pip-label">{value}/{max}</span>
    </div>
  );
}

// barra clicável (durabilidade, água)
export function MeterBar({ value, max, color, onChange }) {
  const pct = max ? Math.max(0, Math.min(1, value / max)) : 0;
  const set = (e) => {
    const r = e.currentTarget.getBoundingClientRect();
    onChange(Math.round(((e.clientX - r.left) / r.width) * max));
  };
  return (
    <div className="meter" onClick={set} title={t('Clique para definir')}>
      <div className="meter-fill" style={{ width: `${pct * 100}%`, background: color }} />
      <span className="meter-text">{value} / {max}</span>
    </div>
  );
}

export const Separator = () => <div className="gsep" style={{ backgroundImage: `url(${tex('separator_line_horizontal')})` }} />;

// espaço de item (itemslot_backpack tingido) com ícone, quantidade, nível e durabilidade
// no jogo, projetos de construção (IsBlueprint) aparecem nos espaços como o martelo de construção
const slotIcon = (id, inCatalog) => (items[id]?.blueprint && !inCatalog ? tex('blueprint_intro_item') : itemIcon(id));

export function SlotView({ entry, selected, empty, label, badge, onClick, drag, inCatalog }) {
  const it = entry && items[entry.itemId];
  const dur = entry?.durability !== undefined && entry.maxDurability ? entry.durability / entry.maxDurability : null;
  const rar = entry ? (entry.maxUpgrades ?? { Common: 1, Uncommon: 2, Rare: 3, Epic: 4, Legendary: 5 }[it?.rarity] ?? 0) : 0;
  return (
    <div className={`slotv ${selected ? 'sel' : ''}`} onClick={onClick} {...(drag || {})}>
      <img className="slot-bg" src={ttex('slot_bg')} alt="" draggable={false} />
      {entry && rar > 1 && <img className="slot-rarity" src={ttex(`slot_rarity_${rar}`)} alt="" draggable={false} />}
      {entry ? <img className="slot-icon" src={slotIcon(entry.itemId, inCatalog)} alt="" draggable={false} /> : empty && <img className="slot-empty" src={tex(empty)} alt="" draggable={false} />}
      {entry && it?.maxStack > 1 && <span className="slot-count">{entry.count}</span>}
      {entry && entry.level !== undefined && <span className="slot-level">{entry.level}</span>}
      {dur !== null && <div className="slot-dur"><div style={{ width: `${Math.round(dur * 100)}%`, background: rgba(dur < 0.25 ? ui.itemSlot.durabilityLow : ui.itemSlot.durability) }} /></div>}
      {selected && <img className="slot-outline" src={tex(ui.itemSlot.outline)} alt="" />}
      {badge && <span className="slot-badge">{badge}</span>}
      {label && <span className="slot-label">{label}</span>}
    </div>
  );
}

export const tierOf = (itemId) => {
  const m = /_T(\d)/.exec(items[itemId]?.debug || '');
  return m ? Number(m[1]) : null;
};
