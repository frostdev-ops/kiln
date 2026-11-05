// Tela "Habilidades": canvas de 8000x8000 com centro em (4000,4000), como no SkillTreeResource.
// Texturas, larguras nominais, ligações, anel central e rótulos de classe vêm do FbUiBundle.
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { skills, nomeDe, descricaoDe, ATRIBUTOS, atributosDaArvore } from './gamedata.js';
import { ui, tex, ttex, rgba } from './ui.js';
import { ASSET_BASE } from './platform.js';
import { totalKey as totalKeyOf, checkTree } from './treecheck.js';
import { t } from './i18n.js';

const ST = ui.skilltree;
const CENTER = { x: 4000, y: 4000 };
const TYPE_CFG = { Root: 'root', Attribute: 'simpleAttribute', PassiveSkill: 'passiveSkill', ActiveSkill: 'activeSkill' };
const CLASS_GLOW = { Ranger: 'ranger', Warrior: 'warrior', Mage: 'mage', Neutral: 'neutral' };

const nodes = Object.entries(skills.nodes).map(([id, n]) => ({ id: Number(id), ...n }));
const byId = new Map(nodes.map((n) => [n.id, n]));
const adj = new Map(nodes.map((n) => [n.id, new Set()]));
for (const [a, b] of skills.links) { adj.get(a)?.add(b); adj.get(b)?.add(a); }

export default function SkillTree({ model, setModel, stage }) {
  const levels = useMemo(() => new Map(model.skills.map((s) => [s.id, s.level])), [model.skills]);
  const active = (id) => byId.get(id)?.type === 'Root' || (levels.get(id) || 0) > 0;
  const state = (n) => (active(n.id) ? 'activated' : [...adj.get(n.id)].some(active) ? 'inReach' : 'outOfReach');

  const used = model.skills.reduce((t, s) => t + (byId.get(s.id)?.cost || 0) * s.level, 0);

  // O total de pontos não fica no save do personagem (parte vem do mundo, ex.: nível da Chama).
  // O usuário informa o total que o jogo mostra e o editor calcula os disponíveis.
  const totalKey = totalKeyOf(model);
  const readTotal = () => { try { const v = localStorage.getItem(totalKey); return v === null ? null : Number(v); } catch { return null; } };
  const [total, setTotal] = useState(readTotal);
  const [editTotal, setEditTotal] = useState(false);
  const [draft, setDraft] = useState('');
  useEffect(() => { setTotal(readTotal()); }, [totalKey]);
  const saveTotal = () => {
    const v = Math.max(0, Math.floor(Number(draft)) || 0);
    try { localStorage.setItem(totalKey, String(v)); } catch {}
    setTotal(v); setEditTotal(false);
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

  const setLevel = (id, lv) => {
    const n = byId.get(id); const max = n.maxLevel || 1;
    lv = Math.max(0, Math.min(max, lv));
    const list = model.skills.some((s) => s.id === id)
      ? model.skills.map((s) => (s.id === id ? { ...s, level: lv } : s))
      : [...model.skills, { id, level: lv }];
    setModel({ ...model, skills: list.filter((s) => s.level > 0 || model.skills.some((o) => o.id === s.id)) });
  };

  // câmera: começa centrada no anel, no primeiro passo de zoom do jogo
  const [view, setView] = useState({ x: CENTER.x, y: CENTER.y, z: 1 / ST.zoomSteps[0] });
  const drag = useRef(null);
  const [hover, setHover] = useState(null);
  const W = stage.w, H = stage.h;
  const onWheel = (e) => {
    const f = e.deltaY > 0 ? 0.88 : 1.14;
    setView((v) => ({ ...v, z: Math.max(0.12, Math.min(1.6, v.z * f)) }));
  };
  const onDown = (e) => { if (e.button === 1 || e.button === 0) drag.current = { sx: e.clientX, sy: e.clientY, v: view, moved: false }; };
  const onMove = (e) => {
    const d = drag.current; if (!d) return;
    const dx = (e.clientX - d.sx) / stage.scale, dy = (e.clientY - d.sy) / stage.scale;
    if (Math.abs(dx) + Math.abs(dy) > 4) d.moved = true;
    setView({ ...d.v, x: d.v.x - dx / d.v.z, y: d.v.y - dy / d.v.z });
  };
  const onUp = () => { setTimeout(() => (drag.current = null), 0); };
  useEffect(() => { window.addEventListener('mouseup', onUp); return () => window.removeEventListener('mouseup', onUp); }, []);

  const canvasStyle = { transform: `translate(${W / 2}px, ${H / 2}px) scale(${view.z}) translate(${-view.x}px, ${-view.y}px)` };

  const linkKind = (a, b) => (active(a) && active(b) ? 'active' : active(a) || active(b) ? 'toCandidate' : 'inactive');
  const sortedLinks = useMemo(() => {
    const order = { inactive: 0, toCandidate: 1, active: 2 };
    return skills.links.map(([a, b]) => [a, b, linkKind(a, b)]).sort((p, q) => order[p[2]] - order[q[2]]);
  }, [levels]);

  return (
    <div className="screen skilltree" onWheel={onWheel} onMouseDown={onDown} onMouseMove={onMove}
      style={{ background: rgba(ST.backgroundColor) }}>
      {/* fundo em camadas com paralaxe, como no jogo */}
      {ST.bgLayers.map((l, i) => {
        const z = view.z, p = 1 - l.parallax;
        const s = `translate(${W / 2}px, ${H / 2}px) scale(${z}) translate(${-(CENTER.x + (view.x - CENTER.x) * p)}px, ${-(CENTER.y + (view.y - CENTER.y) * p)}px)`;
        return (
          <div key={i} className="st-layer" style={{ transform: s }}>
            <div style={{ position: 'absolute', left: CENTER.x - l.size.x / 2, top: CENTER.y - l.size.y / 2, width: l.size.x, height: l.size.y, backgroundImage: `url(${ttex(`st_bg_${i}`)})`, backgroundSize: '100% 100%' }} />
          </div>
        );
      })}
      <div className="st-canvas" style={canvasStyle}>
        <img className="st-ring" src={tex(ST.centerRing)} alt=""
          style={{ left: CENTER.x - ST.centerRingRadius * 1.25, top: CENTER.y - ST.centerRingRadius * 1.25, width: ST.centerRingRadius * 2.5, height: ST.centerRingRadius * 2.5 }} />
        <svg className="st-links" width="8000" height="8000">
          {sortedLinks.map(([a, b, k], i) => {
            const A = byId.get(a), B = byId.get(b); if (!A || !B) return null;
            const c = ST.links[k];
            return (
              <g key={i}>
                {k !== 'inactive' && <line x1={A.x} y1={A.y} x2={B.x} y2={B.y} stroke={rgba(c.glowTint, 0.45)} strokeWidth={c.glow.x * 0.6} strokeLinecap="round" />}
                <line x1={A.x} y1={A.y} x2={B.x} y2={B.y} stroke={rgba(c.coreTint)} strokeWidth={c.core.x * 0.5} strokeLinecap="round" />
              </g>
            );
          })}
        </svg>
        {nodes.map((n) => {
          const cfg = ST.nodes[TYPE_CFG[n.type]];
          if (!cfg) return null;
          const st = state(n), lv = levels.get(n.id) || 0, w = cfg.width;
          const glowC = ST.glow[CLASS_GLOW[n.class] || 'neutral'];
          return (
            <div key={n.id} className={`st-node t-${n.type}`} style={{ left: n.x - w / 2, top: n.y - w / 2, width: w, height: w }}
              onMouseEnter={() => setHover(n)} onMouseLeave={() => setHover((h) => (h === n ? null : h))}
              onClick={() => { if (!drag.current?.moved && n.type !== 'Root') setLevel(n.id, lv + 1); }}
              onContextMenu={(e) => { e.preventDefault(); if (n.type !== 'Root') setLevel(n.id, lv - 1); }}>
              {st === 'activated' && cfg.glow && n.type !== 'Root' && (
                <img className="st-glow" alt="" src={ttex(`st_glow_${CLASS_GLOW[n.class] || 'neutral'}`)} style={{ width: w * cfg.glowScale, height: w * cfg.glowScale, left: (w - w * cfg.glowScale) / 2, top: (w - w * cfg.glowScale) / 2 }} />
              )}
              <img src={tex(cfg[st] || cfg.inReach)} alt="" draggable={false} />
              {n.icon && <img className="st-icon" src={tex(st === 'activated' ? n.icon : n.iconOff)} alt="" draggable={false} style={{ width: w * ST.skillIconScale, height: w * ST.skillIconScale, left: (w - w * ST.skillIconScale) / 2, top: (w - w * ST.skillIconScale) / 2 }} />}
              {n.maxLevel > 0 && <span className="st-level">{lv}/{n.maxLevel}</span>}
            </div>
          );
        })}
      </div>

      {ST.classHints.map((h, i) => {
        const r = ST.centerRingRadius + h.distance, a = (h.angle * Math.PI) / 180;
        const sx = W / 2 + (CENTER.x + Math.cos(a) * r - view.x) * view.z, sy = H / 2 + (CENTER.y + Math.sin(a) * r - view.y) * view.z;
        const c = { ...h.color, a: 1 };
        return (
          <div key={i} className="st-class" style={{ left: sx, top: sy, color: rgba(c), textShadow: `0 0 18px ${rgba(h.color)}`, background: `radial-gradient(ellipse, ${rgba(h.color, 0.55)} 0%, transparent 72%)` }}>
            {h.label}
          </div>
        );
      })}
      <div className="st-points">
        <img src={`${ASSET_BASE}icons/Token_SkillPoint_icon.webp`} alt="" />
        <div>
          <div className="caps small">{t('Em uso')} <b>{used}</b> · {t('nível {n}', { n: model.level })}</div>
          <div className="caps small">{t('Disponível')}</div>
          {total === null ? (
            <button className="set-total" onClick={() => { setDraft(String(used)); setEditTotal(true); }}>{t('informar total…')}</button>
          ) : (
            <div className={`big ${total - used < 0 ? 'neg' : ''}`} onClick={() => { setDraft(String(total)); setEditTotal(true); }} title={t('Clique para mudar o total')}>{total - used}</div>
          )}
          {editTotal && (
            <div className="total-edit" onMouseDown={(e) => e.stopPropagation()}>
              <span className="hint">{t('Total de pontos (em uso + disponível, como mostra o jogo):')}</span>
              <input className="g-input" autoFocus type="number" value={draft} onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') saveTotal(); if (e.key === 'Escape') setEditTotal(false); }} />
              <button className="step" onClick={saveTotal}>OK</button>
            </div>
          )}
        </div>
      </div>
      {(() => { const { problems } = checkTree(model); return problems.length ? <div className="st-warn">⚠ {problems.join(' · ')}</div> : null; })()}
      <div className="st-attrs">
        {ATRIBUTOS.map((a) => (
          <div key={a}><div className="caps small">{t(a)}</div><div className="big">{attrs[a]}</div></div>
        ))}
      </div>
      {hover && (
        <div className="st-tip">
          <div className="detail-title">{nomeDe(hover, '')}</div>
          <div className="detail-sub">{hover.class} · {t('custo {n}', { n: hover.cost })}{hover.maxLevel ? ` · ${t('máx {n}', { n: hover.maxLevel })}` : ''}</div>
          <div className="tip-desc">{descricaoDe(hover).replace(/%k/g, '?')}</div>
          <div className="hint">{t('Clique: +1 nível · Botão direito: −1')}</div>
        </div>
      )}
    </div>
  );
}
