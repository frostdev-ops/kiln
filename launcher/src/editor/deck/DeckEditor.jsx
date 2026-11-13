// The save editor in Deck mode (docs/kiln/launcher/deck.md, "Editor"): the Deck tab /deck/editor, lazy like the
// desktop page. One session with the desktop page (kiln/session.js: changes survive leaving the tab), and the desktop's
// own views (Equipment, Inventory, Skills, Knowledge, the inspector, the catalog) inside a Deck env (kiln/env.jsx):
// Deck dialogs, a number pad for numbers, a list for choices, focus instead of hover.
//   A: the focused control · B: back (out of the inspector, a move, else Home) · X: Save · Y: the view's own (Move,
//   −1 level) · L2 / R2: previous / next part · L1 / R1: the Deck's tabs · the buttons at the top: Characters, Backups,
//   Discard, Save.
// Text: the catalog's and knowledge's search fields take Steam's keyboard (Steam + X), as Help says for Steam's own
// fields; nothing needs typing, the number pad sets every number.
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { DeckButton, Dialog, Row, Screen, Word } from '../../deck/ui';
import { GameArtAuto } from '../../components/GameArt';
import { toast } from '../../ui';
import { api } from '../src/platform.js';
import { t, dataHora } from '../src/i18n.js';
import { EditorEnv } from '../kiln/env.jsx';
import { useEditorArt } from '../kiln/art.js';
import { canSave, discard, openSave, restoreBackup, saveModel, setModel, startSession, useSession } from '../kiln/session.js';
import { BACKUP_KIND, TABS, backupName, sayOf } from '../kiln/EditorApp.jsx';
import Equipment from '../kiln/Equipment.jsx';
import Inventory from '../kiln/Inventory.jsx';
import Skills from '../kiln/Skills.jsx';
import Knowledge from '../kiln/Knowledge.jsx';
import '../kiln/editor.css';
import './deck-editor.css';

const STALE = 'O jogo gravou este save depois que o editor o leu. Leia de novo para editar a versão atual (as mudanças daqui se perdem).';

export default function DeckEditor() {
  const root = useRef(null);
  const art = useEditorArt(root);
  const s = useSession();
  const { model, saves, status, dirty, saving } = s;
  const [tab, setTab] = useState('inv');
  const [view, setView] = useState(null); // the open view's actions and back (env.setActions)
  const [pad, setPad] = useState(null);
  const [choice, setChoice] = useState(null);
  const [dialog, setDialog] = useState(null); // 'characters' | 'backups' | 'discard' | {open: path}
  useEffect(() => startSession(), []);
  // the screen's first focus lands on the header before the character is read: once the views are there, the first slot
  // (or the part's first stop) takes it, unless the player has moved on already
  const placed = useRef(false);
  const edit0 = !!(model && !model.readOnly && art.ready);
  useEffect(() => {
    if (!edit0 || placed.current) return;
    placed.current = true;
    requestAnimationFrame(() => {
      const a = document.activeElement;
      if (a instanceof HTMLElement && a.dataset.deckId && a.dataset.deckId !== 'ed-characters') return;
      const first = ['[data-deck-id^="slot-"]', '[data-deck-id^="worn-"]', 'button'].map((q) => document.querySelector(`.dke-body ${q}`)).find(Boolean);
      first?.focus();
    });
  }, [edit0]);

  const env = useMemo(() => ({
    deck: true,
    Dialog: ({ name, title, onClose, wide, children }) => <Dialog name={name} title={title} onClose={onClose} wide={wide} backLabel={t('Fechar')}>{children}</Dialog>,
    askNumber: (o) => setPad(o),
    choose: (o) => setChoice(o),
    setActions: (v) => setView(v),
  }), []);

  // a part's first stop when the player switches to it (what had focus is gone with the part before)
  const firstOf = { inv: '[data-deck-id^="slot-"]', char: '[data-deck-id="worn-5"], [data-deck-id^="worn-"]', skills: '[data-deck-id="node-681148866"], [data-deck-id^="node-"]', know: '[data-deck-id="know-search"]' };
  const switched = useRef(false);
  useEffect(() => {
    if (!switched.current) return;
    switched.current = false;
    requestAnimationFrame(() => [...firstOf[tab].split(', '), 'button'].map((q) => document.querySelector(`.dke-body ${q}`)).find(Boolean)?.focus());
  }, [tab]);
  const pickTab = (id) => { switched.current = true; setTab(id); };
  const at = TABS.findIndex((x) => x[0] === tab);
  const word = (i) => t(TABS[(i + TABS.length) % TABS.length][1]);
  const go = (d) => pickTab(TABS[(at + d + TABS.length) % TABS.length][0]);
  const save = useCallback(async () => {
    const r = await saveModel();
    if (r === 'saved') toast.ok(t('Salvo'));
    if (r === 'skills') setTab('skills');
  }, []);
  const edit = model && !model.readOnly;
  const actions = [
    { button: 'x', label: t('Salvar'), run: () => void save(), disabled: !canSave(s) },
    ...(edit ? view?.actions ?? [] : []),
    ...(edit ? [{ button: 'lt', label: word(at - 1), run: () => go(-1) }, { button: 'rt', label: word(at + 1), run: () => go(1) }] : []),
  ];
  const back = edit && view?.back ? view.back : null;
  const stale = status?.text === t(STALE);

  return (
    <EditorEnv.Provider value={env}>
      <GameArtAuto only="editor" />
      <Screen name="editor" className="deck-editor" actions={actions} back={back?.run} backLabel={back?.label}>
        <div ref={root} className="kiln-editor-deck">
          <header className="dke-head">
            <div className="dke-who">
              <h1 className="deck-title dke-name">{model?.name ?? t('Editor')}</h1>
              <p className="deck-say dke-say" data-testid="deck-editor-say">
                {sayOf(s)}{dirty && <> <Word tone="warn">{t('Não salvo')}</Word></>}
              </p>
            </div>
            <div className="dke-buttons">
              <DeckButton id="ed-characters" a={t('Abrir')} onClick={() => setDialog('characters')}>{t('Personagens')}</DeckButton>
              <DeckButton id="ed-backups" a={t('Abrir')} disabled={!model} onClick={() => setDialog('backups')}>{t('Backups')}</DeckButton>
              {dirty && <DeckButton id="ed-discard" onClick={() => setDialog('discard')}>{t('Descartar')}</DeckButton>}
              <DeckButton id="ed-save" primary disabled={!canSave(s)} onClick={() => void save()}>{saving ? t('Salvando…') : t('Salvar')}</DeckButton>
            </div>
          </header>
          {(status || art.hint) && (
            <p className="deck-line dke-status" role="status">
              {status && <Word tone={status.tone === 'err' ? 'err' : 'warn'}>{status.text}</Word>}
              {stale && <DeckButton id="ed-reread" onClick={() => void openSave(model.path)}>{t('Ler de novo')}</DeckButton>}
              {!status && art.hint && <span className="deck-dim">{art.hint}</span>}
            </p>
          )}
          {edit && (
            <nav className="dke-tabs" aria-label={t('Partes do save')}>
              {TABS.map(([id, label]) => (
                <DeckButton key={id} id={`ed-tab-${id}`} className={tab === id ? 'on' : ''} a={t('Abrir')} onClick={() => setTab(id)}>
                  {t(label)}{id === 'know' && model.knowledge ? <span className="dke-count">{model.knowledge.length}</span> : null}
                </DeckButton>
              ))}
            </nav>
          )}
          {edit && art.ready && (
            <div className="ke ke-body dke-body" key={`${model.path}:${art.gen}`}>
              {tab === 'char' && <Equipment model={model} setModel={setModel} />}
              {tab === 'inv' && <Inventory model={model} setModel={setModel} />}
              {tab === 'skills' && <Skills model={model} setModel={setModel} />}
              {tab === 'know' && <Knowledge model={model} setModel={setModel} />}
            </div>
          )}
          {!edit && saves !== null && (
            <div className="dke-empty">
              {saves.length > 0 && <DeckButton id="ed-pick" primary onClick={() => setDialog('characters')}>{t('Escolher personagem')}</DeckButton>}
            </div>
          )}
        </div>
      </Screen>
      {pad && <NumberPad {...pad} onClose={() => setPad(null)} />}
      {choice && <Choice {...choice} onClose={() => setChoice(null)} />}
      {dialog === 'characters' && (
        <Characters model={model} saves={saves ?? []} onClose={() => setDialog(null)}
          onPick={(path) => { if (dirty && path !== model?.path) setDialog({ open: path }); else { setDialog(null); if (path !== model?.path) void openSave(path); } }} />
      )}
      {dialog === 'backups' && model && <Backups model={model} dirty={dirty} onClose={() => setDialog(null)} />}
      {dialog === 'discard' && (
        <Dialog name="editor-discard" title={t('Descartar as mudanças?')} onClose={() => setDialog(null)}
          buttons={<>
            <DeckButton onClick={() => setDialog(null)}>{t('Cancelar')}</DeckButton>
            <DeckButton id="discard-confirm" primary onClick={() => { setDialog(null); discard(); }}>{t('Descartar')}</DeckButton>
          </>}>
          <p className="deck-note">{t('As mudanças em {nome} ainda não foram salvas.', { nome: model?.name ?? '' })}</p>
        </Dialog>
      )}
      {dialog?.open && (
        <Dialog name="editor-switch" title={t('Descartar as mudanças?')} onClose={() => setDialog(null)}
          buttons={<>
            <DeckButton onClick={() => setDialog(null)}>{t('Cancelar')}</DeckButton>
            <DeckButton id="switch-confirm" primary onClick={() => { const p = dialog.open; setDialog(null); void openSave(p); }}>{t('Descartar e abrir')}</DeckButton>
          </>}>
          <p className="deck-note">{t('As mudanças em {nome} ainda não foram salvas.', { nome: model?.name ?? '' })}</p>
        </Dialog>
      )}
    </EditorEnv.Provider>
  );
}

/** Focus back to what opened the pad or the list when it closes, also inside another dialog (the catalog's amount):
 *  the Deck's own return waits for the screen, which stays inert under that dialog. */
function useBackToOpener() {
  const opener = useRef(document.activeElement);
  useEffect(() => () => {
    const el = opener.current;
    let tries = 0;
    const t = setInterval(() => {
      if (++tries > 20 || !(el instanceof HTMLElement) || !el.isConnected) { clearInterval(t); return; }
      if (el.closest('[inert]')) return;
      clearInterval(t);
      if (!el.closest('[role="dialog"]')) return; // on the screen: the Deck's own return does it
      el.focus({ preventScroll: true });
    }, 30);
  }, []);
}

/** The number pad: digits (the first one replaces the value), Delete (X), quick values, OK. A physical or Steam keyboard
 *  types digits too. */
export function NumberPad({ title, value, min = 0, max = 65535, presets = [], onDone, onClose }) {
  useBackToOpener();
  const [text, setTextState] = useState(String(value));
  // the text as typed so far (the keyboard listener and the buttons read it), and whether the next digit replaces it
  const cur = useRef(String(value));
  const fresh = useRef(true);
  const setText = (v) => { cur.current = v; fresh.current = false; setTextState(v); };
  const n = text === '' ? NaN : Number(text);
  const ok = Number.isInteger(n) && n >= min && n <= max;
  const okRef = useRef(ok);
  okRef.current = ok;
  const type = (d) => { const next = fresh.current || cur.current === '0' ? d : cur.current + d; if (next.length <= 6) setText(next); };
  const del = () => setText(cur.current.slice(0, -1));
  const done = (v = n) => { onClose(); onDone(Math.max(min, Math.min(max, v))); };
  useEffect(() => {
    const k = (e) => { if (/^[0-9]$/.test(e.key)) { e.preventDefault(); type(e.key); } };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, []);
  const key = (d) => <DeckButton key={d} id={`pad-${d}`} autoFocus={d === '5'} a={d} className="dke-key" onClick={() => type(d)}>{d}</DeckButton>;
  return (
    <Dialog name="editor-number" title={title} onClose={onClose}
      actions={[{ button: 'x', label: t('Apagar'), run: del }, { button: 'y', label: 'OK', disabled: !ok, run: () => { if (okRef.current) done(Number(cur.current)); } }]}
      buttons={<>
        <DeckButton onClick={onClose}>{t('Cancelar')}</DeckButton>
        <DeckButton id="pad-ok" primary disabled={!ok} onClick={() => done()}>OK</DeckButton>
      </>}>
      <div className="dke-pad">
        <div className="dke-pad-value deck-mono" data-testid="pad-value" aria-live="polite">{text || ' '}</div>
        <p className="deck-note">{ok || text === '' ? t('De {min} a {max}', { min, max }) : <Word tone="err">{t('De {min} a {max}', { min, max })}</Word>}</p>
        <div className="dke-pad-keys">
          {['1', '2', '3', '4', '5', '6', '7', '8', '9'].map(key)}
          <DeckButton id="pad-del" className="dke-key" a={t('Apagar')} onClick={del}>⌫</DeckButton>
          {key('0')}
          <DeckButton id="pad-min" className="dke-key" a={String(min)} onClick={() => setText(String(min))}>{t('Mín')}</DeckButton>
        </div>
        {presets.length > 0 && (
          <div className="dke-pad-presets">
            {presets.map((p) => <DeckButton key={p.label} id={`pad-preset-${p.value}`} a={t('Usar')} onClick={() => done(p.value)}>{p.label}</DeckButton>)}
          </div>
        )}
      </div>
    </Dialog>
  );
}

/** One choice from a list (a gem): A picks. */
function Choice({ title, value, options, onDone, onClose }) {
  useBackToOpener();
  return (
    <Dialog name="editor-choice" title={title} onClose={onClose} wide>
      <div className="deck-list deck-scroll">
        {options.map((o) => (
          <Row key={o.value} id={`choice-${o.value}`} title={o.label} sub={o.sub} a={t('Escolher')}
            end={o.value === value ? <Word tone="ember">{t('Atual')}</Word> : undefined}
            onClick={() => { onClose(); onDone(o.value); }} />
        ))}
      </div>
    </Dialog>
  );
}

/** The character saves: A opens one (a save the editor cannot read opens read-only, with why). */
function Characters({ model, saves, onPick, onClose }) {
  return (
    <Dialog name="editor-characters" title={t('Personagens')} onClose={onClose} wide>
      <div className="deck-list deck-scroll">
        {saves.map((x) => (
          <Row key={x.path} id={`char-${x.file}`} title={x.name} a={t('Abrir')} dim={!!x.error}
            sub={x.error ? x.error : `${x.file}${x.level !== undefined ? ` · ${t('nível {n}', { n: x.level })}` : ''}`}
            end={model?.path === x.path ? <Word tone="ember">{t('Aberto')}</Word> : undefined}
            onClick={() => onPick(x.path)} />
        ))}
        {!saves.length && <p className="deck-note">{t('Nenhum save de personagem encontrado.')}</p>}
      </div>
    </Dialog>
  );
}

/** The open save's backups: A on one asks, then restores it (the save before it is kept as a backup). */
function Backups({ model, dirty, onClose }) {
  const [list, setList] = useState(null);
  const [err, setErr] = useState(null);
  const [confirm, setConfirm] = useState(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.listBackups(model.path).then(setList).catch((e) => { setList([]); setErr(e.message); }); }, []);
  const restore = async (b) => {
    setBusy(true);
    try {
      await restoreBackup(b);
      toast.ok(t('Restaurado'));
      setConfirm(null); onClose();
    } catch (e) { setErr(e.message); setConfirm(null); }
    setBusy(false);
  };
  if (confirm) {
    return (
      <Dialog name="editor-restore" title={t('Restaurar este backup?')} onClose={() => setConfirm(null)} locked={busy}
        buttons={<>
          <DeckButton onClick={() => setConfirm(null)} disabled={busy}>{t('Cancelar')}</DeckButton>
          <DeckButton id="restore-confirm" primary disabled={busy} onClick={() => void restore(confirm)}>{t('Restaurar')}</DeckButton>
        </>}>
        <p className="deck-note">{t(BACKUP_KIND[confirm.kind] || confirm.kind)} · {dataHora(confirm.modified)}. {t('O save de agora fica guardado como backup.')}{dirty ? ` ${t('Restaurar descarta as mudanças não salvas.')}` : ''}</p>
      </Dialog>
    );
  }
  return (
    <Dialog name="editor-backups" title={t('Backups de {nome}', { nome: model.name })} onClose={onClose} wide backLabel={t('Fechar')}>
      {err && <p className="deck-note"><Word tone="err">{err}</Word></p>}
      <div className="deck-list deck-scroll">
        {list === null && <p className="deck-note">{t('Procurando backups…')}</p>}
        {list?.length === 0 && !err && <p className="deck-note">{t('Nenhum backup encontrado para este save.')}</p>}
        {list?.map((b) => (
          <Row key={b.path} id={`bk-${b.path}`} title={dataHora(b.modified)} sub={`${t(BACKUP_KIND[b.kind] || b.kind)} · ${backupName(b)}`} a={t('Restaurar')}
            onClick={() => setConfirm(b)} />
        ))}
      </div>
    </Dialog>
  );
}
