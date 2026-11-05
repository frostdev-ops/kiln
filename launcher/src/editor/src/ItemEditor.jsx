// Painel de detalhes do item (moldura fancy_box do jogo), usado na Mochila e no Personagem.
import React from 'react';
import { items, itemName, CATEGORY_PT } from './gamedata.js';
import { ui, tex, ttex, itemIcon, rgba, rarityOf, rarityColor } from './ui.js';
import { GameButton, Stepper, RarityGems, UpgradePips, MeterBar, Separator, tierOf } from './components.jsx';
import { t, localeAtual } from './i18n.js';
import ItemStats from './ItemStats.jsx';

export function FancyPanel({ children, style }) {
  return (
    <div className="detail" style={style}>
      <img className="detail-bg" src={ttex('panel_bg')} alt="" />
      <div className="detail-pattern" style={{ backgroundImage: `url(${tex(ui.menu.fancyBox.backgroundPattern)})` }} />
      <img className="detail-frame" src={tex(ui.menu.fancyBox.frameFull)} alt="" />
      <div className="detail-body">{children}</div>
    </div>
  );
}

function Row({ label, children }) {
  return <div className="ed-row"><div className="ed-label">{label}</div><div className="ed-ctrl">{children}</div></div>;
}

// A joia encaixada na arma mora nos 5 bytes do soquete: itemId da joia (u32 little-endian) e o
// nível dela (u8) — a mesma forma de uma entrada de item. Soquete zerado é arma sem joia.
const SEM_JOIA = '0000000000';

function leSoquete(hex) {
  if (!hex || hex.length !== 10 || hex === SEM_JOIA) return { itemId: 0, level: 0 };
  const b = hex.match(/../g).map((x) => parseInt(x, 16));
  return { itemId: ((b[3] << 24) | (b[2] << 16) | (b[1] << 8) | b[0]) >>> 0, level: b[4] };
}

function escreveSoquete({ itemId, level }) {
  if (!itemId) return SEM_JOIA;
  const b = [itemId & 0xff, (itemId >>> 8) & 0xff, (itemId >>> 16) & 0xff, (itemId >>> 24) & 0xff, level & 0xff];
  return b.map((x) => ('0' + x.toString(16)).slice(-2)).join('');
}

// as joias do catálogo, para o seletor; só os ids, porque nome e ordem dependem do idioma
const JOIAS = Object.keys(items)
  .filter((id) => items[id].category === 'WeaponGem')
  .map((id) => Number(id));

export default function ItemEditor({ entry, onChange, onRemove, onDuplicate, caption }) {
  const it = items[entry.itemId];
  const rar = rarityOf(entry);
  const set = (k, v) => onChange({ ...entry, [k]: v });
  // no jogo o nível do item já inclui as melhorias (Arco Ajustado: base 11 + 5 = 16);
  // mudar as melhorias muda o nível junto, senão o jogo descarta a mudança
  const baseLevel = entry.level !== undefined ? entry.level - (entry.upgrades ?? 0) : undefined;
  const setUpgrades = (u, rarity) => onChange({
    ...entry, upgrades: u, ...(rarity !== undefined ? { maxUpgrades: rarity } : {}),
    ...(entry.level !== undefined ? { level: Math.max(0, baseLevel + u) } : {}),
  });
  const tier = tierOf(entry.itemId);
  const stack = it?.maxStack > 1;
  const joia = entry.socket !== undefined ? leSoquete(entry.socket) : null;
  const setJoia = (mudanca) => set('socket', escreveSoquete({ ...joia, ...mudanca }));
  return (
    <>
      {caption && <div className="caps small">{caption}</div>}
      <div className="ed-head">
        <div className="ed-icon" style={{ background: `radial-gradient(circle, ${rarityColor(rar).replace(/[\d.]+\)$/, '0.45)')} 0%, transparent 70%)` }}>
          <img src={itemIcon(entry.itemId)} alt="" />
        </div>
        <div className="ed-title">
          <div className="detail-title" style={{ color: rarityColor(rar) }}>{itemName(entry.itemId)}</div>
          <div className="detail-sub">
            {t(CATEGORY_PT[it?.category] || it?.category)}{tier ? ` · ${t('Tier {n}', { n: tier })}` : ''}{stack ? ` · ${t('pilha de até {n}', { n: it.maxStack })}` : ''}
          </div>
        </div>
      </div>
      <img className="stripe" src={ttex(`stripe_${rar}`)} alt="" />

      <div className="ed-rows">
        {entry.maxUpgrades !== undefined && (
          <Row label={t('Raridade')}><RarityGems value={entry.maxUpgrades} onChange={(v) => setUpgrades(Math.min(entry.upgrades ?? 0, v), v)} /></Row>
        )}
        {(stack || entry.count !== 1) && (
          <Row label={t('Quantidade')}>
            <Stepper value={entry.count} min={1} max={Math.max(it?.maxStack || 1, entry.count)} steps={[1, 10]} onChange={(v) => set('count', v)}
              extra={stack && <GameButton small onClick={() => set('count', it.maxStack)}>{t('Máx')}</GameButton>} />
          </Row>
        )}
        {/* O nível cabe num byte, então o campo aceita até 255. O servidor corta o que passa do
            maxLevel do item ao carregar (um 255 num arco de faixa 1-55 virou 55), a não ser que a
            faixa daquele item tenha sido aberta por mod no servidor. Como o editor não tem como
            saber disso, aqui a gente avisa em vez de impedir. */}
        {entry.level !== undefined && (
          <Row label={t('Nível')}>
            <Stepper value={entry.level} min={entry.upgrades ?? 0} max={255} onChange={(v) => set('level', v)} />
            {entry.upgrades !== undefined && <span className="hint">{t('= base {base} + {n} melhorias', { base: baseLevel, n: entry.upgrades })}</span>}
            {it?.maxLevel > 0 && entry.level > it.maxLevel && (
              <span className="hint warn">
                {t('acima do máximo deste item ({max}): o servidor corta na carga, a menos que a faixa dele tenha sido aberta por mod', { max: it.maxLevel })}
              </span>
            )}
          </Row>
        )}
        {entry.upgrades !== undefined && entry.maxUpgrades !== undefined && (
          <Row label={t('Melhorias')}><UpgradePips value={entry.upgrades} max={Math.max(entry.maxUpgrades, entry.upgrades)} onChange={(v) => setUpgrades(v)} /><span className="hint">{t('nível base {base}', { base: baseLevel })}</span></Row>
        )}
        {/* A joia encaixada: trocar, tirar, e mexer no nível dela (é o nível que define a força
            do efeito, pela curva WeaponPowerCreep). */}
        {joia && (
          <>
            <Row label={t('Joia encaixada')}>
              <select className="ed-select" value={joia.itemId}
                onChange={(e) => {
                  const id = Number(e.target.value);
                  setJoia({ itemId: id, level: id && !joia.itemId ? Math.max(1, items[id]?.minLevel || 1) : joia.level });
                }}>
                <option value={0}>{t('— sem joia —')}</option>
                {[...JOIAS].sort((a, b) => itemName(a).localeCompare(itemName(b), localeAtual()))
                  .map((id) => <option key={id} value={id}>{itemName(id)}</option>)}
              </select>
              {joia.itemId > 0 && !items[joia.itemId] && <span className="hint warn">{t('id {id} não está no catálogo', { id: joia.itemId })}</span>}
            </Row>
            {joia.itemId > 0 && (
              <Row label={t('Nível da joia')}>
                <Stepper value={joia.level} min={1} max={255} onChange={(v) => setJoia({ level: v })} />
                {items[joia.itemId]?.maxLevel > 0 && joia.level > items[joia.itemId].maxLevel && (
                  <span className="hint warn">
                    {t('acima do máximo desta joia ({max}): o servidor corta na carga, a menos que a faixa dela tenha sido aberta por mod', { max: items[joia.itemId].maxLevel })}
                  </span>
                )}
              </Row>
            )}
          </>
        )}
        {entry.durability !== undefined && (
          <Row label={t('Durabilidade')}>
            <MeterBar value={entry.durability} max={entry.maxDurability} color={rgba(entry.durability / entry.maxDurability < 0.25 ? ui.itemSlot.durabilityLow : ui.itemSlot.durability)} onChange={(v) => set('durability', v)} />
            <GameButton small onClick={() => set('durability', entry.maxDurability)}>{t('Reparar')}</GameButton>
          </Row>
        )}
        {entry.liquid !== undefined && (
          <Row label={t('Água')}>
            <MeterBar value={entry.liquid} max={entry.maxLiquid} color={rgba(ui.itemSlot.water)} onChange={(v) => set('liquid', v)} />
            <GameButton small onClick={() => set('liquid', entry.maxLiquid)}>{t('Encher')}</GameButton>
          </Row>
        )}
      </div>
      {entry.maxUpgrades !== undefined && <div className="hint">{t('A raridade define quantas melhorias o item aceita: nível máximo = nível base + raridade.')}</div>}

      <ItemStats entry={entry} />

      <div className="ed-actions">
        <Separator />
        <div className="row-gap">
          {onDuplicate && <GameButton onClick={onDuplicate} title={t('Copia o item para o primeiro espaço livre')}>{t('Duplicar')}</GameButton>}
          {onRemove && <GameButton danger onClick={onRemove} title="Delete">{t('Remover')}</GameButton>}
        </div>
        <div className="debug">{it?.debug} · {t('espaço {n}', { n: entry.slot })}</div>
      </div>
    </>
  );
}
