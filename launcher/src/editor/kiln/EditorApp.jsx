// The save editor as a launcher page (Hearth): the character's name (a button: other saves), one live sentence, Save
// and Backups; tabs for Equipment, Inventory, Skills and Knowledge. Reading, checking and writing saves are upstream's
// (src/tauri-api.js through src/platform.js, src/treecheck.js); this file replaces upstream's in-game frame (src/App.jsx).
// Keys: Ctrl+S save, Z discard, B backups, Q/E previous/next tab (not while typing).
import React, { useEffect, useRef, useState } from 'react';
import { Banner, Button, Modal, Tabs, toast } from '../../ui';
import { api } from '../src/platform.js';
import { checkTree, pointsUsed } from '../src/treecheck.js';
import { t, dataHora, definirIdioma, assinarIdioma } from '../src/i18n.js';
import Equipment from './Equipment.jsx';
import Inventory from './Inventory.jsx';
import Skills from './Skills.jsx';
import Knowledge from './Knowledge.jsx';

// the launcher is English; item and skill names follow (the game ships both)
definirIdioma('en');

const TABS = [['char', 'Equipamento'], ['inv', 'Mochila'], ['skills', 'Habilidades'], ['know', 'Conhecimento']];
const clone = (o) => JSON.parse(JSON.stringify(o));

export default function EditorApp() {
  const [saves, setSaves] = useState(null);
  const [model, setModel] = useState(null);
  const [orig, setOrig] = useState(null);
  const [tab, setTab] = useState('inv');
  const [status, setStatus] = useState(null);
  const [running, setRunning] = useState(false);
  const [backups, setBackups] = useState(false);
  const [pick, setPick] = useState(false);
  const [saving, setSaving] = useState(false);
  const [, redraw] = useState(0);
  useEffect(() => assinarIdioma(() => redraw((n) => n + 1)), []);

  const refresh = () => api.listSaves().then((l) => { setSaves(l); return l; });
  const open = async (file) => {
    try {
      const m = await api.openSave(file);
      setModel(m); setOrig(clone(m)); setPick(false);
      setStatus(m.error ? { tone: 'warn', text: t('Somente leitura: {erro}', { erro: m.error }) } : null);
    } catch (e) { setStatus({ tone: 'err', text: e.message }); }
  };
  useEffect(() => {
    refresh().then((l) => { const best = [...l].filter((s) => !s.error).sort((a, b) => b.modified - a.modified)[0]; if (best) open(best.path); })
      .catch((e) => { setSaves([]); setStatus({ tone: 'err', text: e.message }); });
    const timer = setInterval(() => api.gameRunning().then(setRunning).catch(() => {}), 3000);
    api.gameRunning().then(setRunning).catch(() => {});
    return () => clearInterval(timer);
  }, []);

  const dirty = !!(model && orig && JSON.stringify(model) !== JSON.stringify(orig));
  const locked = running;
  const canSave = dirty && !locked && !model?.readOnly && !saving;
  const save = async () => {
    if (!canSave) return;
    // a tree the game finds invalid is refunded whole on load: refuse before writing
    if (JSON.stringify(model.skills) !== JSON.stringify(orig.skills)) {
      const { problems, used, total } = checkTree(model);
      if (total === null && used > pointsUsed(orig.skills)) {
        problems.push(t('Você aumentou os pontos em uso ({antes} → {depois}). Informe o total de pontos na aba Habilidades para o editor conferir; acima do total, o jogo zera a árvore.', { antes: pointsUsed(orig.skills), depois: used }));
      }
      if (problems.length) { setTab('skills'); setStatus({ tone: 'err', text: t('Não gravado. {problemas}', { problemas: problems.join(' · ') }) }); return; }
    }
    setSaving(true);
    try {
      const { backupDir, model: m } = await api.writeSave(model.path, model);
      setModel(m); setOrig(clone(m)); setStatus(null);
      toast.ok(t('Salvo'), t('Backup em {pasta}', { pasta: backupDir }));
      refresh();
    } catch (e) { setStatus({ tone: 'err', text: e.message }); }
    setSaving(false);
  };
  const discard = () => { if (dirty) { setModel(clone(orig)); setStatus(null); } };
  useEffect(() => {
    const k = (e) => {
      if (e.ctrlKey && e.key.toLowerCase() === 's') { e.preventDefault(); save(); return; }
      if (e.ctrlKey || e.altKey || e.metaKey || e.target.closest?.('input, select, textarea, [role="dialog"]')) return;
      if ((e.key === 'b' || e.key === 'B') && model) setBackups((v) => !v);
      if ((e.key === 'z' || e.key === 'Z') && dirty) discard();
      const at = TABS.findIndex((x) => x[0] === tab);
      if (e.key === 'q' || e.key === 'Q') setTab(TABS[(at + TABS.length - 1) % TABS.length][0]);
      if (e.key === 'e' || e.key === 'E') setTab(TABS[(at + 1) % TABS.length][0]);
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  });

  const file = model?.path?.split(/[\\/]/).pop();
  const say = !model
    ? (saves === null ? t('Procurando saves…') : saves.length ? t('Escolha um personagem.') : t('Nenhum save de personagem encontrado.'))
    : model.readOnly ? t('Este save não pôde ser interpretado para edição.')
      : running ? t('O jogo está aberto: feche-o para salvar.')
        : dirty ? t('Mudanças não salvas. O editor faz um backup antes de gravar.')
          : t('Nível {n}. O editor faz um backup antes de gravar.', { n: model.level });
  const edit = model && !model.readOnly;

  return (
    <div className="k-page k-page-fill ke">
      <header className="k-page-head">
        <div>
          <div className="k-page-name">
            <SaveSwitch model={model} saves={saves} open={pick} setOpen={setPick} onOpen={open} />
            {file && <span className="k-page-meta"><span className="k-mono k-muted">{file}</span>{dirty && <span className="k-sc" style={{ color: 'var(--k-gold-400)' }}>{t('Não salvo')}</span>}</span>}
          </div>
          <p className="k-page-sub">{say}</p>
        </div>
        <div className="k-page-actions">
          {dirty && <Button variant="ghost" onClick={discard} title="Z">{t('Descartar')}</Button>}
          <Button disabled={!model} onClick={() => setBackups(true)} title="B">{t('Backups')}</Button>
          <Button variant="primary" disabled={!canSave} loading={saving} onClick={save} title={locked ? t('Jogo aberto: feche para salvar') : 'Ctrl+S'}>{t('Salvar')}</Button>
        </div>
      </header>
      {status && <Banner tone={status.tone} title={status.tone === 'err' ? t('Erro') : t('Atenção')} actions={<button type="button" className="k-link" onClick={() => setStatus(null)}>{t('Fechar')}</button>}>{status.text}</Banner>}
      {edit && (
        <>
          <Tabs aria-label={t('Partes do save')} value={tab} onChange={setTab}
            items={TABS.map(([id, label]) => ({ id, label: t(label), count: id === 'know' ? model.knowledge?.length : undefined }))} />
          <div className="ke-body" key={model.path}>
            {tab === 'char' && <Equipment model={model} setModel={setModel} />}
            {tab === 'inv' && <Inventory model={model} setModel={setModel} />}
            {tab === 'skills' && <Skills model={model} setModel={setModel} />}
            {tab === 'know' && <Knowledge model={model} setModel={setModel} />}
          </div>
        </>
      )}
      {backups && model && <Backups model={model} onClose={() => setBackups(false)} onRestored={() => open(model.path)} />}
    </div>
  );
}

/** The character's name: a button that lists the other character saves and "Open a save file…". */
function SaveSwitch({ model, saves, open, setOpen, onOpen }) {
  const ref = useRef(null);
  useEffect(() => {
    if (!open) return undefined;
    const close = (e) => { if (!ref.current?.contains(e.target)) setOpen(false); };
    const esc = (e) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', esc);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', esc); };
  }, [open]);
  return (
    <div className="ke-who" ref={ref}>
      <h1 className="k-page-title">
        <button type="button" className="ke-who-btn" aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
          {model ? model.name : t('Editor')}<svg viewBox="0 0 24 24" aria-hidden><path d="M5 9l7 7 7-7" /></svg>
        </button>
      </h1>
      {open && (
        <div className="k-popover ke-who-pop" role="listbox" aria-label={t('Personagens')}>
          {(saves ?? []).map((s) => (
            <button key={s.path} type="button" role="option" aria-selected={model?.path === s.path} className="k-menu-item" onClick={() => onOpen(s.path)}>
              <span style={{ fontWeight: model?.path === s.path ? 700 : 400 }}>{s.name}</span>
              <span className="k-mono k-muted">{s.file}{s.level !== undefined ? ` · ${t('nível {n}', { n: s.level })}` : ''}</span>
              {s.error && <span className="ke-note warn">{s.error}</span>}
            </button>
          ))}
          {saves?.length === 0 && <span className="ke-note" style={{ padding: '6px 10px' }}>{t('Nenhum save de personagem encontrado.')}</span>}
          <div className="k-menu-sep" />
          <button type="button" className="k-menu-item" style={{ color: 'var(--k-ember-300)' }}
            onClick={async () => { const f = await api.browseSave(); if (f) onOpen(f); }}>{t('Abrir arquivo…')}</button>
        </div>
      )}
    </div>
  );
}

const KIND = { jogo: 'Automático do jogo', pasta: 'Pasta de backups' };

/** The save's backups (the game's last ten and the editor's copies): restore one, with a second click to confirm. */
function Backups({ model, onClose, onRestored }) {
  const [list, setList] = useState(null);
  const [err, setErr] = useState(null);
  const [confirm, setConfirm] = useState(null);
  useEffect(() => { api.listBackups(model.path).then(setList).catch((e) => { setList([]); setErr(e.message); }); }, []);
  const restore = async (b) => {
    try {
      await api.restoreBackup(b.path, model.path);
      toast.ok(t('Restaurado'), t('{arquivo} ({quando}). O save anterior foi guardado como backup.', { arquivo: b.label, quando: dataHora(b.modified) }));
      setConfirm(null); onRestored(); onClose();
    } catch (e) { setErr(e.message); setConfirm(null); }
  };
  return (
    <Modal open onClose={onClose} title={t('Backups de {nome}', { nome: model.name })} width={720}
      footer={<><Button variant="ghost" onClick={() => api.openBackups()}>{t('Abrir pasta')}</Button><Button onClick={onClose}>{t('Fechar')}</Button></>}>
      <p className="k-hint">{t('O jogo guarda os 10 últimos salvamentos automaticamente; o editor também copia o save antes de cada gravação.')}</p>
      {err && <Banner tone="err" title={t('Erro')}>{err}</Banner>}
      <div className="ke-bk">
        {list === null && <span className="ke-note">{t('Procurando backups…')}</span>}
        {list?.length === 0 && !err && <span className="ke-note">{t('Nenhum backup encontrado para este save.')}</span>}
        {list?.map((b) => (
          <div key={b.path} className="ke-bk-row">
            <span className="k-sc k-muted">{t(KIND[b.kind] || b.kind)}</span>
            <span className="k-num">{dataHora(b.modified)}</span>
            <span className="k-mono k-ellipsis" title={b.path}>{b.label}</span>
            {confirm === b.path ? (
              <span className="k-row" style={{ gap: 6, justifyContent: 'flex-end' }}>
                <Button size="s" variant="ghost" onClick={() => setConfirm(null)}>{t('Cancelar')}</Button>
                <Button size="s" variant="danger" onClick={() => restore(b)}>{t('Confirmar')}</Button>
              </span>
            ) : (
              <span style={{ textAlign: 'right' }}><Button size="s" onClick={() => setConfirm(b.path)} disabled={!!confirm}>{t('Restaurar')}</Button></span>
            )}
          </div>
        ))}
      </div>
    </Modal>
  );
}
