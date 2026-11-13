// The inspector beside the slots (Inventory, Equipment): one item's name, rarity, count, level, upgrades, gem,
// durability and water, its attribute lines (src/itemstats.js), Duplicate and Remove. The rules are upstream's
// (src/ItemEditor.jsx): the level includes the upgrades; the rarity caps the upgrades; a gem lives in the 5 socket bytes.
import React from 'react';
import { Button } from '../../ui';
import { items, itemName, skills, skillName } from '../src/gamedata.js';
import { itemIcon, rarityOf, rarityColor } from '../src/ui.js';
import { atributosDe, melhoriasDe, temEstatisticas, armaduraDe, danoDe } from '../src/itemstats.js';
import { t, localeAtual } from '../src/i18n.js';
import { Meter, RarityPick, Row, Stepper, UpgradePick, categoryWord, rarityWord, tierOf } from './parts.jsx';
import { aboveStack, amountLimit, MAX_AMOUNT } from './amounts.js';
import { useEditorEnv } from './env.jsx';

const NO_GEM = '0000000000';
function readSocket(hex) {
  if (!hex || hex.length !== 10 || hex === NO_GEM) return { itemId: 0, level: 0 };
  const b = hex.match(/../g).map((x) => parseInt(x, 16));
  return { itemId: ((b[3] << 24) | (b[2] << 16) | (b[1] << 8) | b[0]) >>> 0, level: b[4] };
}
function writeSocket({ itemId, level }) {
  if (!itemId) return NO_GEM;
  return [itemId & 0xff, (itemId >>> 8) & 0xff, (itemId >>> 16) & 0xff, (itemId >>> 24) & 0xff, level & 0xff]
    .map((x) => x.toString(16).padStart(2, '0')).join('');
}
const GEMS = Object.keys(items).filter((id) => items[id].category === 'WeaponGem').map(Number);
const gemList = () => [...GEMS].sort((a, b) => itemName(a).localeCompare(itemName(b), localeAtual()));
/** The number pad's quick values for a stack: the game's stack size and what the save holds. */
const amountPresets = (maxStack) => [{ label: t('Pilha do jogo ({n})', { n: maxStack }), value: maxStack }, { label: t('Máximo ({n})', { n: MAX_AMOUNT }), value: MAX_AMOUNT }];

/** The panel with nothing chosen, an empty slot, or an item. */
export function Inspector({ entry, empty, onChange, onRemove, onDuplicate, caption, warn }) {
  return (
    <aside className="ke-insp" aria-label={t('Item')}>
      {caption && <div className="k-label">{caption}</div>}
      {warn && <div className="ke-note err">{warn}</div>}
      {entry ? <ItemForm entry={entry} onChange={onChange} onRemove={onRemove} onDuplicate={onDuplicate} /> : empty}
    </aside>
  );
}

function ItemForm({ entry, onChange, onRemove, onDuplicate }) {
  const env = useEditorEnv();
  const it = items[entry.itemId];
  const r = rarityOf(entry);
  const set = (k, v) => onChange({ ...entry, [k]: v });
  // the item's level includes its upgrades (a Fitted Bow: base 11 + 5 = 16): changing upgrades moves the level too,
  // or the game drops the change
  const baseLevel = entry.level !== undefined ? entry.level - (entry.upgrades ?? 0) : undefined;
  // the item's maxLevel bounds the base level: the game itself writes a level-10 hat with 5 upgrades as 15
  const over = it?.maxLevel > 0 && baseLevel > it.maxLevel;
  const setUpgrades = (u, rarity) => onChange({
    ...entry, upgrades: u, ...(rarity !== undefined ? { maxUpgrades: rarity } : {}),
    ...(entry.level !== undefined ? { level: Math.max(0, baseLevel + u) } : {}),
  });
  const tier = tierOf(entry.itemId);
  const stack = it?.maxStack > 1;
  const gem = entry.socket !== undefined ? readSocket(entry.socket) : null;
  const setGem = (c) => set('socket', writeSocket({ ...gem, ...c }));
  const kind = [categoryWord(it?.category), tier && t('Tier {n}', { n: tier }), stack && t('pilha de até {n}', { n: it.maxStack })].filter(Boolean).join(' · ');
  return (
    <>
      <header className="ke-insp-head">
        <span className="ke-insp-icon" style={{ '--r': rarityColor(r) }}><img src={itemIcon(entry.itemId)} alt="" /></span>
        <div className="ke-insp-titles">
          {r > 0 && <span className="k-sc ke-insp-rar" style={{ color: rarityColor(r) }}>{rarityWord(r)}</span>}
          <h2 className="ke-insp-name">{itemName(entry.itemId)}</h2>
          <span className="ke-insp-kind">{kind}</span>
        </div>
      </header>

      <dl className="ke-form">
        {entry.maxUpgrades !== undefined && (
          <Row label={t('Raridade')}><RarityPick value={entry.maxUpgrades} onChange={(v) => setUpgrades(Math.min(entry.upgrades ?? 0, v), v)} /></Row>
        )}
        {/* an amount above the game's stack size is kept where a mod raised stack sizes, which the editor cannot
            know: it says so instead of refusing (amounts.js). Max is the game's own stack. */}
        {(stack || entry.count !== 1) && (
          <Row label={t('Quantidade')} note={aboveStack(it?.maxStack, entry.count) && (
            <span className="ke-note warn">{t('acima da pilha do jogo ({n}): para mundos com um mod que aumenta as pilhas', { n: it.maxStack })}</span>
          )}>
            <span className="k-row" style={{ gap: 10 }}>
              <Stepper value={entry.count} min={1} max={amountLimit(it?.maxStack, entry.count)} big={10} label={t('Quantidade')} onChange={(v) => set('count', v)}
                presets={stack ? amountPresets(it.maxStack) : undefined} />
              {stack && <button type="button" className="k-link" onClick={() => set('count', it.maxStack)}>{t('Máx')}</button>}
            </span>
          </Row>
        )}
        {/* the level fits a byte; the server clamps past the item's maxLevel on load unless a mod opened its range,
            which the editor cannot know: it warns instead of refusing */}
        {entry.level !== undefined && (
          <Row label={t('Nível')} note={(
            <>
              {entry.upgrades !== undefined && <span className="ke-note">{t('= base {base} + {n} melhorias', { base: baseLevel, n: entry.upgrades })}</span>}
              {over && <span className="ke-note warn">{t('acima do máximo deste item ({max}): o servidor corta na carga, a menos que a faixa dele tenha sido aberta por mod', { max: it.maxLevel })}</span>}
            </>
          )}>
            <Stepper value={entry.level} min={entry.upgrades ?? 0} max={255} label={t('Nível')} onChange={(v) => set('level', v)} />
          </Row>
        )}
        {entry.upgrades !== undefined && entry.maxUpgrades !== undefined && (
          <Row label={t('Melhorias')} note={<span className="ke-note">{t('A raridade define quantas melhorias o item aceita: nível máximo = nível base + raridade.')}</span>}>
            <UpgradePick value={entry.upgrades} max={Math.max(entry.maxUpgrades, entry.upgrades)} onChange={(v) => setUpgrades(v)} />
          </Row>
        )}
        {gem && (
          <Row label={t('Joia encaixada')} note={gem.itemId > 0 && !items[gem.itemId] && <span className="ke-note warn">{t('id {id} não está no catálogo', { id: gem.itemId })}</span>}>
            {env.deck ? (
              <button type="button" className="k-select ke-select ke-pick" data-deck-id="item-gem" data-deck-a={t('Mudar')} aria-label={`${t('Joia encaixada')}: ${gem.itemId ? itemName(gem.itemId) : t('— sem joia —')}`}
                onClick={() => env.choose({ title: t('Joia encaixada'), value: gem.itemId, options: [{ value: 0, label: t('— sem joia —') }, ...gemList().map((id) => ({ value: id, label: itemName(id) }))],
                  onDone: (id) => setGem({ itemId: id, level: id && !gem.itemId ? Math.max(1, items[id]?.minLevel || 1) : gem.level }) })}>
                {gem.itemId ? itemName(gem.itemId) : t('— sem joia —')}
              </button>
            ) : (
              <select className="k-select ke-select" value={gem.itemId} aria-label={t('Joia encaixada')}
                onChange={(e) => { const id = Number(e.target.value); setGem({ itemId: id, level: id && !gem.itemId ? Math.max(1, items[id]?.minLevel || 1) : gem.level }); }}>
                <option value={0}>{t('— sem joia —')}</option>
                {gemList().map((id) => <option key={id} value={id}>{itemName(id)}</option>)}
              </select>
            )}
          </Row>
        )}
        {gem && gem.itemId > 0 && (
          <Row label={t('Nível da joia')} note={items[gem.itemId]?.maxLevel > 0 && gem.level > items[gem.itemId].maxLevel && (
            <span className="ke-note warn">{t('acima do máximo desta joia ({max}): o servidor corta na carga, a menos que a faixa dela tenha sido aberta por mod', { max: items[gem.itemId].maxLevel })}</span>
          )}>
            <Stepper value={gem.level} min={1} max={255} label={t('Nível da joia')} onChange={(v) => setGem({ level: v })} />
          </Row>
        )}
        {entry.durability !== undefined && (
          <Row label={t('Durabilidade')}>
            <span className="k-row" style={{ gap: 12, flexWrap: 'nowrap' }}>
              <Meter label={t('Durabilidade')} value={entry.durability} max={entry.maxDurability} tone={entry.durability / entry.maxDurability < 0.25 ? 'low' : ''} onChange={(v) => set('durability', v)} />
              <button type="button" className="k-link" disabled={entry.durability >= entry.maxDurability} onClick={() => set('durability', entry.maxDurability)}>{t('Reparar')}</button>
            </span>
          </Row>
        )}
        {entry.liquid !== undefined && (
          <Row label={t('Água')}>
            <span className="k-row" style={{ gap: 12, flexWrap: 'nowrap' }}>
              <Meter label={t('Água')} value={entry.liquid} max={entry.maxLiquid} tone="water" onChange={(v) => set('liquid', v)} />
              <button type="button" className="k-link" disabled={entry.liquid >= entry.maxLiquid} onClick={() => set('liquid', entry.maxLiquid)}>{t('Encher')}</button>
            </span>
          </Row>
        )}
      </dl>

      <Stats entry={entry} />

      <footer className="ke-insp-foot">
        <span className="k-row" style={{ gap: 8 }}>
          {onDuplicate && <Button size="s" onClick={onDuplicate} title={`${t('Copia o item para o primeiro espaço livre')} (Ctrl+D)`} data-deck-id="item-duplicate">{t('Duplicar')}</Button>}
          {onRemove && <Button size="s" variant="danger" onClick={onRemove} title="Delete" data-deck-id="item-remove">{t('Remover')}</Button>}
        </span>
        <span className="k-mono ke-debug" title={it?.debug}>{it?.debug}</span>
      </footer>
    </>
  );
}

/** Armour, damage and attribute lines, then the upgrade levels (applied ones lit). */
function Stats({ entry }) {
  if (!temEstatisticas(entry.itemId)) return null;
  const lvl = entry.level;
  const attrs = atributosDe(entry.itemId, lvl);
  // the game's vector always has five places; how many the item takes is its rarity
  const cap = entry.maxUpgrades ?? 0;
  const ups = melhoriasDe(entry.itemId, lvl, entry.upgrades ?? 0).filter((m) => m.pos < cap);
  if (!attrs.length && !ups.length) return null;
  const armour = armaduraDe(entry.itemId, lvl);
  const dmg = danoDe(entry.itemId, lvl);
  const unknown = [...attrs, ...ups.flatMap((m) => m.linhas)].some((l) => l.valor === null);
  const Line = ({ valor, nome }) => <><span className="k-mono ke-stat-v">{valor ?? '—'}</span><span className="ke-stat-n">{nome}</span></>;
  return (
    <section className="ke-stats">
      <h3 className="k-label">{t('Atributos')}</h3>
      <div className="ke-stat-grid">
        {armour && <><Line valor={armour.fisica} nome={t('Armadura Física')} /><Line valor={armour.magica} nome={t('Armadura Mágica')} /></>}
        {dmg && dmg.map((d, i) => <Line key={`d${i}`} valor={d.valor} nome={d.nome} />)}
        {attrs.map((l, i) => <Line key={i} {...l} />)}
      </div>
      {ups.length > 0 && (
        <>
          <h3 className="k-label">{t('Melhorias')}</h3>
          <ol className="ke-ups">
            {ups.map((m) => (
              <li key={m.nivel} className={m.aplicada ? 'on' : ''}>
                <span className="ke-ups-lv"><i className="ke-mini-dia" />{t('nível {n}', { n: m.nivel })}{m.ganho ? <span className="k-mono k-muted"> +{m.ganho.fisica}/+{m.ganho.magica}</span> : null}</span>
                <span className="ke-stat-grid">
                  {m.armadura && <Line valor={`+${m.armadura.valor}`} nome={m.armadura.nome} />}
                  {m.dano && <Line valor={`+${m.dano.valor}`} nome={m.dano.nome} />}
                  {m.linhas.map((l, i) => <Line key={i} {...l} />)}
                </span>
                {m.no !== null && skills.nodes[m.no] && <span className="ke-note">{t('aumenta o nível de {no} na árvore', { no: skillName(m.no) })}</span>}
              </li>
            ))}
          </ol>
        </>
      )}
      {unknown && <span className="ke-note warn">{t('As linhas sem número variam com o nível do item por uma conta que ainda não foi medida; o editor não mostra um valor que não pode garantir.')}</span>}
    </section>
  );
}
