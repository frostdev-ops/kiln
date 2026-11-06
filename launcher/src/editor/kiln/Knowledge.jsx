// Knowledge: what the character has seen, picked up and unlocked (items, recipes). Pick entries and remove them;
// removing a high-level material also closes the recipes that need it. Upstream: src/Conhecimento.jsx.
import React, { useMemo, useState } from 'react';
import { Button } from '../../ui';
import { estadoTexto } from '../shared/knowledge.mjs';
import map from '../data/conhecimento.json';
import { nomeConhecimento } from '../src/gamedata.js';
import { t, localeAtual } from '../src/i18n.js';

const KIND = { i: 'item', r: 'receita', o: 'outro' };
const state = (v, kind) => estadoTexto(v, kind).split('+').map((p) => t(p)).join(', ');
const SHOWN = 400;

export default function Knowledge({ model, setModel }) {
  const [q, setQ] = useState('');
  const [above, setAbove] = useState('');
  const [kinds, setKinds] = useState('ir');
  const [sel, setSel] = useState(() => new Set());
  const list = useMemo(() => {
    const n = Number(above), s = q.trim().toLowerCase();
    return (model.knowledge ?? [])
      .map((k) => ({ ...k, info: map[k.id], name: map[k.id] ? nomeConhecimento(map[k.id], k.id) : null }))
      .filter((k) => kinds.includes(k.info?.t ?? 'o') && (above === '' || (k.info?.l ?? 0) > n)
        && (!s || (k.name ?? '').toLowerCase().includes(s) || String(k.id).includes(s)))
      .sort((a, b) => (b.info?.l ?? 0) - (a.info?.l ?? 0) || (a.name ?? '').localeCompare(b.name ?? '', localeAtual()));
  }, [model.knowledge, q, above, kinds]);
  if (!model.knowledge) return <div className="ke-empty"><span className="ke-empty-word">{t('Este save não traz o bloco de conhecimento.')}</span></div>;
  const toggle = (id) => setSel((p) => { const n = new Set(p); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const remove = () => { setModel({ ...model, knowledge: model.knowledge.filter((k) => !sel.has(k.id)) }); setSel(new Set()); };
  const unnamed = model.knowledge.filter((k) => !map[k.id]).length;
  const shown = list.slice(0, SHOWN);
  const allOn = shown.length > 0 && shown.every((k) => sel.has(k.id));

  return (
    <div className="ke-know">
      <div className="ke-know-bar">
        <input className="k-input" style={{ width: 280 }} placeholder={t('Procurar por nome ou id')} value={q} onChange={(e) => setQ(e.target.value)} aria-label={t('Procurar por nome ou id')} />
        <label className="k-well compact" style={{ width: 168 }}><span className="k-label">{t('Acima do nível')}</span>
          <input type="number" value={above} placeholder={t('todos')} onChange={(e) => setAbove(e.target.value)} /></label>
        <span className="k-seg" role="radiogroup" aria-label={t('Mostrar')}>
          {[['ir', 'Itens e receitas'], ['i', 'Só itens'], ['r', 'Só receitas'], ['iro', 'Tudo']].map(([v, label]) => (
            <button key={v} type="button" role="radio" aria-checked={kinds === v} className={kinds === v ? 'on' : ''} onClick={() => setKinds(v)}>{t(label)}</button>
          ))}
        </span>
        <span className="k-spacer" />
        <Button size="s" variant="danger" disabled={!sel.size} onClick={remove}>{sel.size ? t('Remover {n}', { n: sel.size }) : t('Remover')}</Button>
      </div>
      <div className="ke-know-table" role="table" aria-label={t('Conhecimento')}>
        <div className="ke-know-row head" role="row">
          <span role="columnheader"><input type="checkbox" checked={allOn} aria-label={t('Todos')}
            onChange={() => setSel((p) => { const n = new Set(p); for (const k of shown) { if (allOn) n.delete(k.id); else n.add(k.id); } return n; })} /></span>
          <span role="columnheader">{t('Tipo')}</span><span role="columnheader">{t('Nome')}</span>
          <span role="columnheader" style={{ textAlign: 'right' }}>{t('Nível')}</span><span role="columnheader">{t('Estado')}</span>
        </div>
        <div className="ke-know-body">
          {shown.map((k) => (
            <label key={k.id} className={'ke-know-row' + (sel.has(k.id) ? ' on' : '')} role="row">
              <span role="cell"><input type="checkbox" checked={sel.has(k.id)} onChange={() => toggle(k.id)} /></span>
              <span role="cell" className={`k-sc ke-kind ${k.info?.t ?? 'o'}`}>{t(KIND[k.info?.t ?? 'o'])}</span>
              <span role="cell" className="k-ellipsis">{k.name ?? <span className="k-mono k-muted">{t('id {id}', { id: k.id })}</span>}</span>
              <span role="cell" className="k-mono" style={{ textAlign: 'right' }}>{k.info?.l || ''}</span>
              <span role="cell" className="k-muted k-ellipsis">{state(k.value, k.info?.t)}</span>
            </label>
          ))}
          {!list.length && <div className="ke-note" style={{ padding: 12 }}>{t('Nada com esses filtros.')}</div>}
        </div>
      </div>
      <p className="ke-note">
        {t('{n} de {total} entradas', { n: list.length, total: model.knowledge.length })}
        {unnamed ? ` · ${t('{n} sem nome conhecido', { n: unnamed })}` : ''}
        {list.length > SHOWN ? ` · ${t('Mostrando as 400 primeiras; use a busca ou o corte para ver o resto.')}` : ''}
        {' · '}{t('Remover um material de nível alto fecha as receitas que dependem dele. As receitas não ficam gravadas no save: o jogo as recalcula a partir do conhecimento, aqui e no mundo do servidor.')}
      </p>
    </div>
  );
}
