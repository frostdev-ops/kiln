import React, { useMemo, useState } from 'react';
import { GameButton } from './components.jsx';
import { estadoTexto } from '../shared/knowledge.mjs';
import mapa from '../data/conhecimento.json';
import { nomeConhecimento } from './gamedata.js';
import { t , localeAtual } from './i18n.js';

// Conhecimento do personagem: itens já vistos/pegos e receitas liberadas.
// Tirar daqui um material de nível alto também fecha as receitas que dependem dele.
const TIPO_PT = { i: 'item', r: 'receita', o: 'outro' };

// o estado vem do módulo compartilhado como 'pegou+usou'; cada parte é uma chave de tradução
const estado = (valor, tipo) => estadoTexto(valor, tipo).split('+').map((p) => t(p)).join('+');

export default function Conhecimento({ model, setModel }) {
  const [busca, setBusca] = useState('');
  const [corte, setCorte] = useState('');
  const [tipo, setTipo] = useState('ir');
  const [sel, setSel] = useState(() => new Set());

  const lista = useMemo(() => {
    const n = Number(corte);
    const q = busca.trim().toLowerCase();
    return (model.knowledge ?? [])
      .map((k) => ({ ...k, info: mapa[k.id], nome: mapa[k.id] ? nomeConhecimento(mapa[k.id], k.id) : null }))
      .filter((k) => {
        const t = k.info?.t ?? 'o';
        if (!tipo.includes(t)) return false;
        if (corte !== '' && !((k.info?.l ?? 0) > n)) return false;
        if (q && !((k.nome ?? '').toLowerCase().includes(q) || String(k.id).includes(q))) return false;
        return true;
      })
      .sort((a, b) => (b.info?.l ?? 0) - (a.info?.l ?? 0) || (a.nome ?? '').localeCompare(b.nome ?? '', localeAtual()));
  }, [model.knowledge, busca, corte, tipo]);

  const alterna = (id) => setSel((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });
  const remover = () => {
    setModel({ ...model, knowledge: model.knowledge.filter((k) => !sel.has(k.id)) });
    setSel(new Set());
  };
  const semNome = (model.knowledge ?? []).filter((k) => !mapa[k.id]).length;

  if (!model.knowledge) {
    return <div className="center-msg">{t('Este save não traz o bloco de conhecimento.')}</div>;
  }
  return (
    <div className="know">
      <div className="know-tools">
        <input className="know-busca" placeholder={t('Procurar por nome ou id')} value={busca} onChange={(e) => setBusca(e.target.value)} />
        <label className="know-campo">
          <span className="caps small">{t('Acima do nível')}</span>
          <input type="number" value={corte} placeholder={t('todos')} onChange={(e) => setCorte(e.target.value)} />
        </label>
        <div className="know-tipos">
          {[['ir', 'Itens e receitas'], ['i', 'Só itens'], ['r', 'Só receitas'], ['iro', 'Tudo']].map(([v, rotulo]) => (
            <button key={v} className={`know-tipo ${tipo === v ? 'on' : ''}`} onClick={() => setTipo(v)}>{t(rotulo)}</button>
          ))}
        </div>
        <span className="hint">
          {t('{n} de {total} entradas', { n: lista.length, total: model.knowledge.length })}
          {semNome ? ` · ${t('{n} sem nome conhecido', { n: semNome })}` : ''}
        </span>
        <GameButton small danger disabled={!sel.size} onClick={remover}>{sel.size ? t('Remover {n}', { n: sel.size }) : t('Remover')}</GameButton>
      </div>
      <div className="know-lista">
        {lista.slice(0, 400).map((k) => (
          <button key={k.id} className={`know-row ${sel.has(k.id) ? 'sel' : ''}`} onClick={() => alterna(k.id)}>
            <span className="know-check">{sel.has(k.id) ? '✕' : ''}</span>
            <span className={`know-kind ${k.info?.t ?? 'o'}`}>{t(TIPO_PT[k.info?.t ?? 'o'])}</span>
            <span className="know-nome">{k.nome ?? t('id {id}', { id: k.id })}</span>
            <span className="know-nivel">{k.info?.l ? t('nv {n}', { n: k.info.l }) : ''}</span>
            <span className="know-estado">{estado(k.value, k.info?.t)}</span>
          </button>
        ))}
        {lista.length > 400 && <div className="hint">{t('Mostrando as 400 primeiras; use a busca ou o corte para ver o resto.')}</div>}
        {!lista.length && <div className="hint">{t('Nada com esses filtros.')}</div>}
      </div>
      <div className="hint">
        {t('Remover um material de nível alto fecha as receitas que dependem dele. As receitas não ficam gravadas no save: o jogo as recalcula a partir do conhecimento, aqui e no mundo do servidor.')}
      </div>
    </div>
  );
}
