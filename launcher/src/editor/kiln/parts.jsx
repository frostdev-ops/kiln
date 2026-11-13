// Small pieces of the Kiln editor UI (Hearth): the item slot, the stepper, rarity and upgrade diamonds, the meter.
// Game art appears only as content (item icons); every frame, fill and word is the launcher's.
import React from 'react';
import { items, CATEGORY_PT } from '../src/gamedata.js';
import { itemIcon, rarityColor, rarityOf, RARITY_PT } from '../src/ui.js';
import { t } from '../src/i18n.js';
import { useEditorEnv } from './env.jsx';

/** A neutral square: an item with no icon (an unknown item, a mod item without one) and, through art.js, a game icon
 *  that does not load. */
export const PLACEHOLDER = 'data:image/svg+xml,' + encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect x="4" y="4" width="56" height="56" rx="8" fill="#2a2421" stroke="#4d413a" stroke-width="2"/><path d="M22 42l8-10 6 7 4-5 6 8z" fill="#6b5e55"/><circle cx="26" cy="24" r="4" fill="#6b5e55"/></svg>');
/** The item's icon, or the neutral square when it has none. */
export const iconOf = (itemId) => itemIcon(itemId) || PLACEHOLDER;

/** "T3" from the item's debug name, or null. */
export const tierOf = (itemId) => {
  const m = /_T(\d)/.exec(items[itemId]?.debug || '');
  return m ? Number(m[1]) : null;
};

/** A category's word: the translation, else the game's name spelled out ("FishingRod": "Fishing rod"). */
export const categoryWord = (c) => {
  if (!c) return '';
  const w = t(CATEGORY_PT[c] || c);
  return w !== c ? w : c.replace(/([a-z])([A-Z])/g, (_, a, b) => `${a} ${b.toLowerCase()}`);
};

/** The rarity words, 1..5 (Common .. Legendary). */
export const rarityWord = (r) => t(RARITY_PT[r] || RARITY_PT[0]);

/**
 * One slot: a dark well, the item's icon, its count (stacks) or level, a durability line, and its rarity as a glaze
 * line along the bottom. `n`: a small number in the corner (action bar keys). `lazy`: the icon loads when the slot
 * is scrolled to (the catalog's long grid).
 */
export function Slot({ entry, selected, onClick, onDoubleClick, drag, drop, n, size, title, dim, onFocus, deckId, deckA, label, lazy }) {
  const it = entry && items[entry.itemId];
  const r = entry ? rarityOf(entry) : 0;
  const dur = entry?.durability !== undefined && entry.maxDurability ? entry.durability / entry.maxDurability : null;
  const name = entry ? (title ?? undefined) : undefined;
  return (
    <button type="button" className={'ke-slot' + (selected ? ' on' : '') + (entry ? ' full' : '') + (drop ? ' drop' : '') + (dim ? ' dim' : '')}
      style={{ ...(size ? { '--s': `${size}px` } : null), ...(r > 0 ? { '--r': rarityColor(r) } : null) }}
      onClick={onClick} onDoubleClick={onDoubleClick} onFocus={onFocus} title={name} aria-pressed={selected || undefined} aria-label={label}
      data-deck-id={deckId} data-deck-a={deckA} {...(drag || {})}>
      {entry && <img src={iconOf(entry.itemId)} alt="" draggable={false} loading={lazy ? 'lazy' : undefined} decoding={lazy ? 'async' : undefined} />}
      {n !== undefined && <span className="ke-slot-n">{n}</span>}
      {entry && it?.maxStack > 1 && <span className="ke-slot-count">{entry.count}</span>}
      {entry && !(it?.maxStack > 1) && entry.level !== undefined && <span className="ke-slot-count lv">{entry.level}</span>}
      {dur !== null && <i className={'ke-slot-dur' + (dur < 0.25 ? ' low' : '')} style={{ width: `calc((100% - 12px) * ${Math.max(0, Math.min(1, dur)).toFixed(3)})` }} />}
    </button>
  );
}

/** − value + (and ±10 when `big`), clamped; the value is typed in the middle (on the Deck: A on it opens the number
 *  pad, with `presets` beside the digits). */
export function Stepper({ value, onChange, min = 0, max = 65535, big, label, presets, id }) {
  const env = useEditorEnv();
  const clamp = (v) => Math.max(min, Math.min(max, v));
  const step = (d) => onChange(clamp(value + d));
  return (
    <span className="ke-step" role="group" aria-label={label}>
      {big && <button type="button" className="ke-step-b" onClick={() => step(-big)} disabled={value <= min} aria-label={`−${big}`}>−{big}</button>}
      <button type="button" className="ke-step-b" onClick={() => step(-1)} disabled={value <= min} aria-label="−1">−</button>
      {env.deck ? (
        <button type="button" className="ke-step-v" aria-label={`${label}: ${value}`} data-deck-a={t('Mudar')} data-deck-id={id ?? `value-${label}`}
          onClick={() => env.askNumber({ title: label, value, min, max, presets, onDone: (v) => onChange(clamp(v)) })}>{value}</button>
      ) : (
        <input className="ke-step-v" type="number" inputMode="numeric" value={value} min={min} max={max} aria-label={label}
          onChange={(e) => onChange(clamp(Math.floor(Number(e.target.value)) || 0))} />
      )}
      <button type="button" className="ke-step-b" onClick={() => step(1)} disabled={value >= max} aria-label="+1">+</button>
      {big && <button type="button" className="ke-step-b" onClick={() => step(big)} disabled={value >= max} aria-label={`+${big}`}>+{big}</button>}
    </span>
  );
}

/** Five diamonds in the rarity glazes; the chosen one and those below it are lit. */
export function RarityPick({ value, onChange }) {
  return (
    <span className="ke-dias" role="radiogroup" aria-label={t('Raridade')}>
      {[1, 2, 3, 4, 5].map((r) => (
        <button key={r} type="button" role="radio" aria-checked={value === r} title={rarityWord(r)} className={'ke-dia' + (r <= value ? ' on' : '')}
          style={{ '--r': rarityColor(r) }} onClick={() => onChange(r)}><i /></button>
      ))}
      <span className="ke-dias-word" style={{ color: rarityColor(value) }}>{rarityWord(value)}</span>
    </span>
  );
}

/** Upgrade diamonds: `value` of `max` lit (gold); a click sets the count, a click on the last lit one takes it back. */
export function UpgradePick({ value, max, onChange }) {
  return (
    <span className="ke-dias" role="group" aria-label={t('Melhorias')}>
      {Array.from({ length: max }, (_, i) => (
        <button key={i} type="button" className={'ke-dia gold' + (i < value ? ' on' : '')} aria-label={`${i + 1}`}
          onClick={() => onChange(i + 1 === value ? i : i + 1)}><i /></button>
      ))}
      <span className="ke-dias-word k-num">{value}/{max}</span>
    </span>
  );
}

/** A thin bar to click on (durability, water) with the numbers beside it; on the Deck the bar is a button that opens
 *  the number pad. */
export function Meter({ value, max, tone, onChange, label }) {
  const env = useEditorEnv();
  const pct = max ? Math.max(0, Math.min(1, value / max)) : 0;
  const set = (e) => {
    const r = e.currentTarget.getBoundingClientRect();
    onChange(Math.round(Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)) * max));
  };
  const bar = <i style={{ width: `${pct * 100}%` }} />;
  return (
    <span className="ke-meter">
      {env.deck ? (
        <button type="button" className={`ke-meter-bar ${tone || ''}`} aria-label={`${label}: ${value}/${max}`} data-deck-a={t('Mudar')} data-deck-id={`value-${label}`}
          onClick={() => env.askNumber({ title: label, value, min: 0, max, presets: [{ label: t('Cheio'), value: max }], onDone: onChange })}>{bar}</button>
      ) : (
        <span className={`ke-meter-bar ${tone || ''}`} onClick={set} title={t('Clique para definir')} role="presentation">{bar}</span>
      )}
      <span className="k-mono ke-meter-n">{value}/{max}</span>
    </span>
  );
}

/** A label and its control, in the inspector's two columns. */
export function Row({ label, children, note }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>{children}{note}</dd>
    </>
  );
}
