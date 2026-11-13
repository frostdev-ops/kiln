// The editor's session, shared by the desktop page (EditorApp.jsx) and the Deck screen (../deck/DeckEditor.jsx): the
// character saves, the open one (`model`) and what it was when read (`orig`), the live state line, the game running,
// and the steps: open, change, discard, save, restore. It lives outside the page, so changes survive leaving the page
// or the Deck tab and coming back; they are lost only by Discard, by opening another character (the UIs ask first) or
// by closing the launcher. Reading, checking and writing are upstream's (src/tauri-api.js, src/treecheck.js).
import { create } from 'zustand';
import { api } from '../src/platform.js';
import { editorFileInfo } from '../../api/commands';
import { checkTree, pointsUsed } from '../src/treecheck.js';
import { t } from '../src/i18n.js';
import { usePendingEdits } from './pending.ts';

const clone = (o) => JSON.parse(JSON.stringify(o));
// upstream's own messages for a save it cannot read are Portuguese keys; en.json words them
const said = (msg) => t(msg);

export const useSession = create(() => ({
  /** null: still looking */
  saves: null,
  model: null,
  orig: null,
  /** the model as JSON when read: `dirty` compares with it once per change, not on every render */
  origJson: '',
  dirty: false,
  /** {tone: 'warn' | 'err', text} */
  status: null,
  running: false,
  saving: false,
  /** the save file's time when read: a save the game wrote since is not overwritten */
  readAt: 0,
}));

const set = (p) => {
  useSession.setState(p);
  const s = useSession.getState();
  usePendingEdits.setState({ dirty: s.dirty, name: s.model?.name ?? null });
};

export const setStatus = (status) => set({ status });

export async function refreshSaves() {
  try {
    const l = await api.listSaves();
    set({ saves: l.map((s) => (s.error ? { ...s, error: said(s.error) } : s)) });
    return l;
  } catch (e) {
    set({ saves: [], status: { tone: 'err', text: e.message } });
    return [];
  }
}

/** Reads a save; replaces the open one (the UIs ask before dropping changes). */
export async function openSave(path) {
  try {
    const m = await api.openSave(path);
    const json = JSON.stringify(m);
    set({ model: m, orig: clone(m), origJson: json, dirty: false, readAt: m.modified ?? 0,
      status: m.error ? { tone: 'warn', text: t('Somente leitura: {erro}', { erro: said(m.error) }) } : null });
    return true;
  } catch (e) {
    set({ status: { tone: 'err', text: e.message } });
    return false;
  }
}

/** A change to the open model (the views call it with the whole new model). */
export function setModel(m) {
  const s = useSession.getState();
  set({ model: m, dirty: !!s.orig && JSON.stringify(m) !== s.origJson });
}

export function discard() {
  const s = useSession.getState();
  if (s.dirty) set({ model: clone(s.orig), dirty: false, status: null });
}

/** The first visit: the list, then the newest readable character unless one is open already; the game's state every 3 s.
 *  Returns the stop function. */
export function startSession() {
  refreshSaves().then((l) => {
    if (useSession.getState().model) return;
    const best = [...l].filter((x) => !x.error).sort((a, b) => b.modified - a.modified)[0];
    if (best) openSave(best.path);
  });
  const poll = () => api.gameRunning().then((running) => { if (running !== useSession.getState().running) set({ running }); }).catch(() => {});
  poll();
  const timer = setInterval(poll, 3000);
  return () => clearInterval(timer);
}

/** Reads the open character again when nothing is changed (new item tables: a mod's items merged). */
export function reopenIfClean() {
  const s = useSession.getState();
  if (s.model && !s.dirty) return openSave(s.model.path);
  return Promise.resolve(false);
}

/** A save read only by accepting unknown item ids (a mod that is gone) stays read-only: writing refuses those ids. */
export const readOnly = (m) => !!(m && (m.readOnly || m.error));
export const canSave = (s) => !!(s.dirty && !s.running && s.model && !readOnly(s.model) && !s.saving);

/** Writes the open model. Returns 'saved', or what stopped it: 'skills' (the tree checks), 'changed' (the game wrote the
 *  save since it was read), 'error'. */
export async function saveModel() {
  const s = useSession.getState();
  if (!canSave(s)) return 'error';
  const { model, orig } = s;
  // a tree the game finds invalid is refunded whole on load: refuse before writing
  if (JSON.stringify(model.skills) !== JSON.stringify(orig.skills)) {
    const { problems, used, total } = checkTree(model);
    if (total === null && used > pointsUsed(orig.skills)) {
      problems.push(t('Você aumentou os pontos em uso ({antes} → {depois}). Informe o total de pontos na aba Habilidades para o editor conferir; acima do total, o jogo zera a árvore.', { antes: pointsUsed(orig.skills), depois: used }));
    }
    if (problems.length) { set({ status: { tone: 'err', text: t('Não gravado. {problemas}', { problemas: problems.join(' · ') }) } }); return 'skills'; }
  }
  set({ saving: true });
  try {
    // the game may have written the save since it was read (played after opening the editor): writing the old
    // character over it would lose that play
    const { modified } = await editorFileInfo(model.path);
    if (s.readAt && modified && modified !== s.readAt) {
      set({ saving: false, status: { tone: 'err', text: t('O jogo gravou este save depois que o editor o leu. Leia de novo para editar a versão atual (as mudanças daqui se perdem).') } });
      return 'changed';
    }
    const { model: m } = await api.writeSave(model.path, model);
    set({ model: m, orig: clone(m), origJson: JSON.stringify(m), dirty: false, readAt: m.modified ?? 0, status: null, saving: false });
    refreshSaves();
    return 'saved';
  } catch (e) {
    set({ saving: false, status: { tone: 'err', text: e.message } });
    return 'error';
  }
}

/** Restores a backup over the open save and reads it again (the save before it is kept as a backup). */
export async function restoreBackup(b) {
  const s = useSession.getState();
  await api.restoreBackup(b.path, s.model.path);
  await openSave(s.model.path);
}
