import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import Backpack from './Backpack.jsx';
import SkillTree from './SkillTree.jsx';
import Character from './Character.jsx';
import Conhecimento from './Conhecimento.jsx';
import { ui, tex, ttex, rgba } from './ui.js';
import { api } from './platform.js';
import { GameButton } from './components.jsx';
import { checkTree, pointsUsed } from './treecheck.js';
import { t, dataHora, hora, IDIOMAS, idiomaAtual, definirIdioma, assinarIdioma } from './i18n.js';

const W = 2560, H = 1440; // resolução de referência das medidas tiradas do jogo
// Kiln: the 'srv' tab (Guardian server copies over SFTP) is hidden; the launcher manages servers itself.
const TABS = [['char', 'Personagem', 'N'], ['inv', 'Mochila', 'TAB'], ['skills', 'Habilidades', 'H'],
  ['know', 'Conhecimento', 'K']];
const clone = (o) => JSON.parse(JSON.stringify(o));

// Kiln: fits the stage to the editor's own box (the launcher's content area), not the window.
function useStageScale(ref) {
  const [s, setS] = useState({ scale: 1, x: 0, y: 0 });
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return undefined;
    const fit = () => {
      const w = el.clientWidth, h = el.clientHeight;
      const scale = Math.min(w / W, h / H);
      setS({ scale, x: (w - W * scale) / 2, y: (h - H * scale) / 2 });
    };
    fit();
    const ro = new ResizeObserver(fit);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return s;
}

const KIND_PT = { jogo: 'Automático do jogo', pasta: 'Pasta de backups' };

function Backups({ model, onClose, onRestored }) {
  const [list, setList] = useState(null);
  const [msg, setMsg] = useState(null);
  const [confirm, setConfirm] = useState(null);
  const base = model.path.split(/[\\/]/).pop();
  useEffect(() => { api.listBackups(model.path).then(setList).catch((e) => { setList([]); setMsg({ kind: 'error', text: e.message }); }); }, []);
  useEffect(() => {
    const k = (e) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', k); return () => window.removeEventListener('keydown', k);
  }, [onClose]);
  const restore = async (b) => {
    try {
      await api.restoreBackup(b.path, model.path);
      setMsg({ kind: 'ok', text: t('Restaurado: {arquivo} ({quando}). O save anterior foi guardado como backup.', { arquivo: b.label, quando: dataHora(b.modified) }) });
      setConfirm(null); onRestored();
    } catch (e) { setMsg({ kind: 'error', text: e.message }); setConfirm(null); }
  };
  return (
    <div className="modal" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="modal-box">
        <div className="detail-title">{t('Backups de {nome}', { nome: model.name })} <span className="detail-sub">({base})</span></div>
        <div className="hint">{t('O jogo guarda os 10 últimos salvamentos automaticamente; o editor também copia o save antes de cada gravação.')}</div>
        {msg && <div className={`hint ${msg.kind}`}>{msg.text}</div>}
        <div className="backup-list">
          {list === null && <div className="hint">{t('Procurando backups…')}</div>}
          {list?.length === 0 && <div className="hint">{t('Nenhum backup encontrado para este save.')}</div>}
          {list?.map((b) => (
            <div key={b.path} className="backup-row">
              <span className={`bk-kind ${b.kind}`}>{t(KIND_PT[b.kind] || b.kind)}</span>
              <span className="bk-date">{dataHora(b.modified)}</span>
              <span className="bk-label" title={b.path}>{b.label}</span>
              {confirm === b.path ? (
                <span className="row-gap">
                  <GameButton small danger onClick={() => restore(b)}>{t('Confirmar')}</GameButton>
                  <GameButton small onClick={() => setConfirm(null)}>{t('Cancelar')}</GameButton>
                </span>
              ) : (
                <GameButton small onClick={() => setConfirm(b.path)} disabled={!!confirm}>{t('Restaurar')}</GameButton>
              )}
            </div>
          ))}
        </div>
        <div className="row-gap">
          <GameButton onClick={() => api.openBackups()}>{t('Abrir pasta')}</GameButton>
          <GameButton onClick={onClose}>{t('Fechar')}</GameButton>
        </div>
      </div>
    </div>
  );
}

export default function App() {
  const viewport = useRef(null);
  const stage = useStageScale(viewport);
  const [saves, setSaves] = useState([]);
  const [model, setModel] = useState(null);
  const [orig, setOrig] = useState(null);
  const [tab, setTab] = useState('inv');
  const [status, setStatus] = useState(null);
  const [running, setRunning] = useState(false);
  const [showBackups, setShowBackups] = useState(false);
  const [showSaves, setShowSaves] = useState(false);
  // trocar o idioma redesenha o App; os filhos não são memoizados, então o texto deles vem junto
  const [, redesenhar] = useState(0);
  useEffect(() => assinarIdioma(() => redesenhar((n) => n + 1)), []);

  const refresh = () => api.listSaves().then((l) => { setSaves(l); return l; });
  const open = async (file) => {
    try {
      const m = await api.openSave(file);
      setModel(m); setOrig(clone(m)); setShowSaves(false);
      setStatus(m.error ? { kind: 'warn', text: t('Somente leitura: {erro}', { erro: m.error }) } : null);
    } catch (e) { setStatus({ kind: 'error', text: e.message }); }
  };
  useEffect(() => {
    const forced = new URLSearchParams(location.search).get('open');
    refresh().then((l) => { if (forced) return open(forced); const best = [...l].filter((s) => !s.error).sort((a, b) => b.modified - a.modified)[0]; if (best) open(best.path); });
    const timer = setInterval(() => api.gameRunning().then(setRunning), 3000);
    api.gameRunning().then(setRunning);
    return () => clearInterval(timer);
  }, []);

  const dirty = model && orig && JSON.stringify(model) !== JSON.stringify(orig);
  // o jogo aberto só atrapalha o save local; a cópia do servidor é gravada pelo editor
  const travado = running && !model?.servidor;

  // cópia do servidor: o jogo salva a cada 30 s, então o editor acompanha.
  // Sem edição pendente ele recarrega sozinho; com edição pendente só avisa, para não perder o seu trabalho.
  useEffect(() => {
    if (!model?.servidor) return;
    let carimbo = null, vivo = true;
    const olhar = async () => {
      const c = await api.serverStamp(model.path);
      if (!vivo || !c) return;
      if (carimbo === null) { carimbo = c; return; }
      if (c === carimbo) return;
      carimbo = c;
      if (dirty) {
        setStatus({ kind: 'warn', text: t('A cópia no servidor mudou (o jogador continua jogando). Grave ou descarte: recarregar agora perderia sua edição.') });
      } else {
        open(model.path).then(() => setStatus({ kind: 'ok', text: t('Cópia atualizada do servidor às {hora}', { hora: hora(Date.now()) }) }));
      }
    };
    olhar();
    const timer = setInterval(olhar, 10000);
    return () => { vivo = false; clearInterval(timer); };
  }, [model?.path, model?.servidor, dirty]);
  const save = async () => {
    if (!dirty || travado || model.readOnly) return;
    // árvore inválida faz o jogo reembolsar todos os pontos ao carregar: bloqueia antes de gravar
    if (JSON.stringify(model.skills) !== JSON.stringify(orig.skills)) {
      const { problems, used, total } = checkTree(model);
      if (total === null && used > pointsUsed(orig.skills)) {
        problems.push(t('Você aumentou os pontos em uso ({antes} → {depois}). Informe o total de pontos na aba Habilidades para o editor conferir; acima do total, o jogo zera a árvore.', { antes: pointsUsed(orig.skills), depois: used }));
      }
      if (problems.length) { setTab('skills'); setStatus({ kind: 'error', text: t('Não gravado. {problemas}', { problemas: problems.join(' · ') }) }); return; }
    }
    try {
      const { backupDir, model: m } = await api.writeSave(model.path, model);
      setModel(m); setOrig(clone(m));
      setStatus({ kind: 'ok', text: model.servidor
        ? t('Gravado na cópia do servidor. Expulse o jogador: ele volta com esta versão. Backup em {pasta}', { pasta: backupDir })
        : t('Salvo. Backup em {pasta}', { pasta: backupDir }) });
      refresh();
    } catch (e) { setStatus({ kind: 'error', text: e.message }); }
  };
  useEffect(() => {
    const k = (e) => {
      if (e.ctrlKey && e.key.toLowerCase() === 's') { e.preventDefault(); save(); }
      if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
      if ((e.key === 'b' || e.key === 'B') && !e.ctrlKey && model) setShowBackups((v) => !v);
      if ((e.key === 'z' || e.key === 'Z') && !e.ctrlKey && dirty) setModel(clone(orig));
      if (e.key === 'q' || e.key === 'Q') setTab((a) => TABS[(TABS.findIndex((x) => x[0] === a) + TABS.length - 1) % TABS.length][0]);
      if (e.key === 'e' || e.key === 'E') setTab((a) => TABS[(TABS.findIndex((x) => x[0] === a) + 1) % TABS.length][0]);
    };
    window.addEventListener('keydown', k); return () => window.removeEventListener('keydown', k);
  });

  const stageInfo = { w: W, h: H, scale: stage.scale };
  return (
    <div className="viewport" ref={viewport}>
      <div className="stage" style={{ width: W, height: H, transform: `translate(${stage.x}px, ${stage.y}px) scale(${stage.scale})` }}>
        <div className="world-bg" />
        {model && !model.readOnly && tab === 'inv' && <Backpack model={model} setModel={setModel} />}
        {model && !model.readOnly && tab === 'skills' && <SkillTree model={model} setModel={setModel} stage={stageInfo} />}
        {model && !model.readOnly && tab === 'char' && <Character model={model} setModel={setModel} />}
        {model && !model.readOnly && tab === 'know' && <Conhecimento model={model} setModel={setModel} />}
        {model?.readOnly && <div className="center-msg">{t('Este save não pôde ser interpretado para edição.')}</div>}

        <div className="topbar">
          <div className="topbar-bg" style={{ backgroundImage: `url(${tex(ui.menu.topTab)})` }} />
          <div className="topbar-line" />
          <button className="who" onClick={() => setShowSaves((v) => !v)}>
            <span className="caps small">{t('Personagem')}</span>
            <b>{model ? model.name : '—'}</b>
          </button>
          <div className="tabs">
            <span className="key">Q</span>
            {TABS.map(([k, rotulo, key]) => (
              <button key={k} className={`tab ${tab === k ? 'active' : ''}`} onClick={() => setTab(k)}
                style={{ color: rgba(tab === k ? ui.menu.tabHighlight : ui.menu.tabIdle) }}>
                <span className="key">{key}</span>
                {t(rotulo)}
                {tab === k && <img className="tab-mark" src={ttex('tab_mark')} alt="" />}
              </button>
            ))}
            <span className="key">E</span>
          </div>
        </div>

        {showSaves && (
          <div className="save-menu">
            {saves.map((s) => (
              <button key={s.path} className={`save-item ${model?.path === s.path ? 'on' : ''}`} onClick={() => open(s.path)}>
                <b>{s.name}{s.servidor && <span className="srv-tag">{t('servidor')}</span>}{s.online && <span className="srv-tag on">{t('na sessão')}</span>}</b>
                <span>{s.file}{s.level !== undefined ? ` · ${t('nível {n}', { n: s.level })}` : ''}</span>
                {s.error && <span className="warn">{s.error}</span>}
              </button>
            ))}
            <button className="save-item" onClick={async () => { const f = await api.browseSave(); if (f) open(f); }}><b>{t('Abrir arquivo…')}</b></button>
          </div>
        )}

        <div className="hints">
          <select className="lang-select" value={idiomaAtual()} onChange={(e) => definirIdioma(e.target.value)} title={t('Idioma')}>
            {IDIOMAS.map((i) => <option key={i.codigo} value={i.codigo}>{i.nome}</option>)}
          </select>
          {status && <span className={`status ${status.kind}`}>{status.text}</span>}
          {model?.servidor
            ? <span className={`game-state ${model.online ? 'on' : ''}`}>
                {model.online ? t('Jogador na sessão: o servidor sobrescreve a cópia a cada 30 s — expulse-o antes') : t('Cópia do servidor')}
              </span>
            : <span className={`game-state ${running ? 'on' : ''}`}>{running ? t('Jogo aberto: feche para salvar') : t('Jogo fechado')}</span>}
          {dirty && <button className="hint-btn" onClick={() => setModel(clone(orig))}><span className="key">Z</span>{t('Descartar')}</button>}
          {!model?.servidor && <button className="hint-btn" onClick={() => setShowBackups(true)}><span className="key">B</span>{t('Backups')}</button>}
          <button className={`hint-btn primary ${dirty && !travado ? '' : 'off'}`} onClick={save}>
            <span className="key">CTRL+S</span>{model?.servidor ? t('Gravar no servidor') : t('Salvar no save')}
          </button>
        </div>
        {showBackups && model && !model.servidor && <Backups model={model} onClose={() => setShowBackups(false)} onRestored={() => open(model.path)} />}
      </div>
    </div>
  );
}
