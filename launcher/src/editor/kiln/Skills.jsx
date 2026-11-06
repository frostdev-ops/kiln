// Skills: the game's skill tree (8000×8000 units round (4000,4000), from data/skills.json) drawn in the launcher's own
// shapes: one SVG, panned by dragging and zoomed by the wheel round the pointer. Click a node for +1 level, right-click
// for −1. Nodes keep the game's skill icons (content); links and rings are lines in the glazes. Points in use, the
// total the player types (it is not in the save) and the tree checks (src/treecheck.js) sit over the tree.
import React, { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Button } from '../../ui';
import { skills, nomeDe, descricaoDe, ATRIBUTOS, atributosDaArvore } from '../src/gamedata.js';
import { ui, tex } from '../src/ui.js';
import { totalKey as totalKeyOf, checkTree } from '../src/treecheck.js';
import { t } from '../src/i18n.js';

const ST = ui.skilltree;
const C = 4000;
const RING = ST.centerRingRadius;
const CLASS = { Warrior: '#ff8f45', Ranger: '#8fd6b4', Mage: '#8cbbe3', Neutral: '#f9c74f' };
const classOf = (n) => CLASS[n.class] || CLASS.Neutral;
// node radius (tree units) by type
const R = { Root: 64, Attribute: 22, PassiveSkill: 38, ActiveSkill: 50 };
const hexRgb = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)).join(',');

const nodes = Object.entries(skills.nodes).map(([id, n]) => ({ id: Number(id), ...n }));
const byId = new Map(nodes.map((n) => [n.id, n]));
const adj = new Map(nodes.map((n) => [n.id, new Set()]));
for (const [a, b] of skills.links) { adj.get(a)?.add(b); adj.get(b)?.add(a); }
const BASE_ATTR = 10;

export default function Skills({ model, setModel }) {
  const levels = useMemo(() => new Map(model.skills.map((s) => [s.id, s.level])), [model.skills]);
  const active = (id) => byId.get(id)?.type === 'Root' || (levels.get(id) || 0) > 0;
  const stateOf = (n) => (active(n.id) ? 'on' : [...adj.get(n.id)].some(active) ? 'reach' : 'off');
  const used = model.skills.reduce((s, x) => s + (byId.get(x.id)?.cost || 0) * x.level, 0);

  // the total is not in the character save (part of it comes from the world): the player types what the game shows
  const totalKey = totalKeyOf(model);
  const readTotal = () => { try { const v = localStorage.getItem(totalKey); return v === null ? null : Number(v); } catch { return null; } };
  const [total, setTotal] = useState(readTotal);
  const [editTotal, setEditTotal] = useState(false);
  const [draft, setDraft] = useState('');
  useEffect(() => { setTotal(readTotal()); }, [totalKey]);
  const saveTotal = () => {
    const v = Math.max(0, Math.floor(Number(draft)) || 0);
    try { localStorage.setItem(totalKey, String(v)); } catch { /* no storage: this session only */ }
    setTotal(v); setEditTotal(false);
  };
  const { daArvore } = atributosDaArvore(model.skills);
  const { problems } = checkTree(model);

  const setLevel = (id, lv) => {
    const n = byId.get(id); const max = n.maxLevel || 1;
    lv = Math.max(0, Math.min(max, lv));
    const list = model.skills.some((s) => s.id === id)
      ? model.skills.map((s) => (s.id === id ? { ...s, level: lv } : s))
      : [...model.skills, { id, level: lv }];
    setModel({ ...model, skills: list.filter((s) => s.level > 0 || model.skills.some((o) => o.id === s.id)) });
  };

  // the camera: the box's size, the point at its centre and the zoom (screen px per tree unit)
  const box = useRef(null);
  const [size, setSize] = useState({ w: 1000, h: 600 });
  const fit = (w, h) => Math.min(w, h) / (RING * 3.6);
  const [view, setView] = useState(null);
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return undefined;
    const measure = () => {
      const w = el.clientWidth || 1000, h = el.clientHeight || 600;
      setSize({ w, h });
      setView((v) => v ?? { x: C, y: C, z: fit(w, h) });
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const v = view ?? { x: C, y: C, z: fit(size.w, size.h) };
  const zMin = fit(size.w, size.h) * 0.45, zMax = fit(size.w, size.h) * 8;
  const zoomAt = (f, cx = size.w / 2, cy = size.h / 2) => setView((o) => {
    const p = o ?? v;
    const z = Math.max(zMin, Math.min(zMax, p.z * f));
    const wx = p.x + (cx - size.w / 2) / p.z, wy = p.y + (cy - size.h / 2) / p.z;
    return { x: wx - (cx - size.w / 2) / z, y: wy - (cy - size.h / 2) / z, z };
  });
  useEffect(() => {
    const el = box.current;
    if (!el) return undefined;
    const wheel = (e) => {
      e.preventDefault();
      const r = el.getBoundingClientRect();
      zoomAt(e.deltaY > 0 ? 0.88 : 1.14, e.clientX - r.left, e.clientY - r.top);
    };
    el.addEventListener('wheel', wheel, { passive: false });
    return () => el.removeEventListener('wheel', wheel);
  });
  const drag = useRef(null);
  const down = (e) => { if (e.button === 0 || e.button === 1) drag.current = { sx: e.clientX, sy: e.clientY, v, moved: false }; };
  const move = (e) => {
    const d = drag.current;
    if (!d) return;
    const dx = e.clientX - d.sx, dy = e.clientY - d.sy;
    if (Math.abs(dx) + Math.abs(dy) > 4) d.moved = true;
    if (d.moved) setView({ ...d.v, x: d.v.x - dx / d.v.z, y: d.v.y - dy / d.v.z });
  };
  // the click on a node comes after pointerup: keep `moved` until then
  useEffect(() => {
    const up = () => { setTimeout(() => { drag.current = null; }, 0); };
    window.addEventListener('pointerup', up);
    return () => window.removeEventListener('pointerup', up);
  }, []);
  const [hover, setHover] = useState(null);

  const linkKind = (a, b) => (active(a) && active(b) ? 'on' : active(a) || active(b) ? 'reach' : 'off');
  const links = useMemo(() => {
    const order = { off: 0, reach: 1, on: 2 };
    return skills.links.map(([a, b]) => [a, b, linkKind(a, b)]).sort((p, q) => order[p[2]] - order[q[2]]);
  }, [levels]);
  const vb = `${v.x - size.w / 2 / v.z} ${v.y - size.h / 2 / v.z} ${size.w / v.z} ${size.h / v.z}`;
  const showText = v.z > 0.3;

  return (
    <div className="ke-tree" ref={box} onPointerDown={down} onPointerMove={move}>
      <svg className="ke-tree-svg" viewBox={vb} preserveAspectRatio="xMidYMid meet" aria-label={t('Árvore de habilidades')} role="img">
        <defs>
          <radialGradient id="ke-hearth"><stop offset="0" stopColor="rgba(255,110,40,.16)" /><stop offset="1" stopColor="rgba(255,110,40,0)" /></radialGradient>
        </defs>
        <circle cx={C} cy={C} r={RING * 2.6} fill="url(#ke-hearth)" />
        <circle cx={C} cy={C} r={RING} fill="none" stroke="rgba(255,190,140,.16)" strokeWidth={4} />
        <circle cx={C} cy={C} r={RING - 26} fill="none" stroke="rgba(255,190,140,.07)" strokeWidth={2} strokeDasharray="10 16" />
        {links.map(([a, b, k], i) => {
          const A = byId.get(a), B = byId.get(b);
          if (!A || !B) return null;
          return k === 'on'
            ? <g key={i}><line x1={A.x} y1={A.y} x2={B.x} y2={B.y} stroke="rgba(255,120,40,.22)" strokeWidth={22} strokeLinecap="round" /><line x1={A.x} y1={A.y} x2={B.x} y2={B.y} stroke="#ffb066" strokeWidth={7} strokeLinecap="round" /></g>
            : <line key={i} x1={A.x} y1={A.y} x2={B.x} y2={B.y} stroke={k === 'reach' ? 'rgba(255,190,140,.34)' : 'rgba(255,190,140,.11)'} strokeWidth={k === 'reach' ? 6 : 5} strokeLinecap="round" />;
        })}
        {ST.classHints.map((h, i) => {
          const r = RING + h.distance, a = (h.angle * Math.PI) / 180;
          const col = h.color.b > h.color.r ? CLASS.Mage : h.color.g > h.color.r ? CLASS.Ranger : CLASS.Warrior;
          return <text key={i} x={C + Math.cos(a) * r} y={C + Math.sin(a) * r} className="ke-tree-class" fill={col} textAnchor="middle" dominantBaseline="middle">{t(h.label)}</text>;
        })}
        {nodes.map((n) => {
          const st = stateOf(n), lv = levels.get(n.id) || 0, col = classOf(n), r = R[n.type] ?? 30;
          const rgb = hexRgb(col);
          const isRoot = n.type === 'Root';
          const click = () => { if (!drag.current?.moved && !isRoot) setLevel(n.id, lv + 1); };
          const ctx = (e) => { e.preventDefault(); if (!isRoot) setLevel(n.id, lv - 1); };
          return (
            <g key={n.id} className={`ke-node ${st}`} transform={`translate(${n.x} ${n.y})`} onClick={click} onContextMenu={ctx}
              onPointerEnter={() => setHover(n)} onPointerLeave={() => setHover((h) => (h === n ? null : h))}>
              {st === 'on' && !isRoot && <circle r={r * 1.9} fill={`rgba(${rgb},.16)`} />}
              {isRoot ? (
                <rect x={-r * 0.7} y={-r * 0.7} width={r * 1.4} height={r * 1.4} transform="rotate(45)" fill="#2a1a0e" stroke="#f9c74f" strokeWidth={6} />
              ) : n.type === 'ActiveSkill' ? (
                <rect x={-r * 0.78} y={-r * 0.78} width={r * 1.56} height={r * 1.56} rx={r * 0.2} transform="rotate(45)"
                  fill={st === 'on' ? `rgba(${rgb},.3)` : '#140d0a'} stroke={st === 'off' ? 'rgba(255,190,140,.2)' : col} strokeOpacity={st === 'reach' ? 0.6 : 1} strokeWidth={st === 'on' ? 6 : 4} />
              ) : (
                <circle r={r} fill={st === 'on' ? `rgba(${rgb},.3)` : '#140d0a'} stroke={st === 'off' ? 'rgba(255,190,140,.2)' : col} strokeOpacity={st === 'reach' ? 0.6 : 1} strokeWidth={st === 'on' ? 6 : 4} />
              )}
              {n.icon && n.type !== 'Attribute' && (
                <image href={tex(st === 'on' ? n.icon : n.iconOff || n.icon)} x={-r * 0.72} y={-r * 0.72} width={r * 1.44} height={r * 1.44} opacity={st === 'off' ? 0.4 : st === 'reach' ? 0.75 : 1} />
              )}
              {n.type === 'Attribute' && <circle r={r * 0.32} fill={st === 'on' ? col : st === 'reach' ? `rgba(${rgb},.5)` : 'rgba(255,190,140,.18)'} />}
              {showText && n.maxLevel > 1 && <text y={r + 30} className={'ke-node-lv' + (lv ? ' on' : '')} textAnchor="middle">{lv}/{n.maxLevel}</text>}
              <circle r={Math.max(r * 1.25, 30)} fill="transparent" />
            </g>
          );
        })}
      </svg>

      <div className="ke-tree-points" onPointerDown={(e) => e.stopPropagation()}>
        <div><span className="k-label">{t('Em uso')}</span><b className="k-num">{used}</b></div>
        <div>
          <span className="k-label">{t('Disponível')}</span>
          {total === null
            ? <button type="button" className="k-link" onClick={() => { setDraft(String(used)); setEditTotal(true); }}>{t('informar total…')}</button>
            : <button type="button" className={'ke-tree-avail k-num' + (total - used < 0 ? ' neg' : '')} title={t('Clique para mudar o total')} onClick={() => { setDraft(String(total)); setEditTotal(true); }}>{total - used}</button>}
        </div>
        {editTotal && (
          <div className="ke-tree-total">
            <span className="ke-note">{t('Total de pontos (em uso + disponível, como mostra o jogo):')}</span>
            <span className="k-row" style={{ gap: 6, flexWrap: 'nowrap' }}>
              <input className="k-input k-input-mono" style={{ width: 90 }} autoFocus type="number" value={draft} onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') saveTotal(); if (e.key === 'Escape') setEditTotal(false); }} aria-label={t('Total de pontos')} />
              <Button size="s" onClick={saveTotal}>OK</Button>
            </span>
          </div>
        )}
      </div>

      {problems.length > 0 && <div className="ke-tree-warn" role="alert">{problems.join(' · ')}</div>}

      <dl className="ke-attrs ke-tree-attrs" onPointerDown={(e) => e.stopPropagation()}>
        {ATRIBUTOS.map((a) => <div key={a}><dt className="k-label">{t(a)}</dt><dd className="k-num">{BASE_ATTR + daArvore[a]}</dd></div>)}
      </dl>

      <div className="ke-tree-zoom" onPointerDown={(e) => e.stopPropagation()}>
        <button type="button" onClick={() => zoomAt(1.25)} aria-label={t('Aproximar')}>+</button>
        <button type="button" onClick={() => zoomAt(0.8)} aria-label={t('Afastar')}>−</button>
        <button type="button" className="k-sc" onClick={() => setView({ x: C, y: C, z: fit(size.w, size.h) })}>{t('Centro')}</button>
      </div>

      {hover && (
        <div className="ke-tree-tip">
          <span className="k-sc" style={{ color: classOf(hover) }}>{t(hover.class)}{hover.type !== 'Root' ? ` · ${t('custo {n}', { n: hover.cost })}` : ''}{hover.maxLevel ? ` · ${t('máx {n}', { n: hover.maxLevel })}` : ''}</span>
          <strong className="ke-tree-tip-name">{nomeDe(hover, '')}</strong>
          {descricaoDe(hover) && <span className="ke-tree-tip-desc">{descricaoDe(hover).replace(/%k/g, '?')}</span>}
          {hover.type !== 'Root' && <span className="ke-note">{t('Clique: +1 nível · Botão direito: −1')}</span>}
        </div>
      )}
    </div>
  );
}
