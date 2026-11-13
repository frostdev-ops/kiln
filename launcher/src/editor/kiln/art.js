// The game art and the items of Kiln mods, for both shells (KilnEditor.jsx on the desktop, deck/DeckEditor.jsx): the
// mod overlay (<art>/modded/items.json) is merged into the editor's tables before a save is read and again when it
// changes (the open character is read again then, unless it has changes); a missing icon becomes a neutral square;
// `hint` is the one line while the launcher makes the art from the game ("Preparing game art…") or cannot.
import { useEffect, useRef, useState } from 'react';
import { assetsPresent, IS_TAURI, ASSET_BASE } from '../src/platform.js';
import { mergeModded } from '../src/gamedata.js';
import { useAssetsStatus, useExtractTask } from '../../components/GameArt';
import { reopenIfClean } from './session.js';
import { PLACEHOLDER } from './parts.jsx';

async function loadOverlay(version) {
  if (!version) return null;
  try {
    const r = await fetch(`${ASSET_BASE}modded/items.json?v=${encodeURIComponent(version)}`);
    return r.ok ? await r.json() : null;
  } catch {
    return null;
  }
}

/** {ready, gen, assets, hint}: mount the views once `ready`; key them by `gen` (new art: icons that fell back to the
 *  placeholder load again). `root`: the element whose broken images get the placeholder. */
export function useEditorArt(root) {
  const [assets, setAssets] = useState(null);
  const [gen, setGen] = useState(0);
  const [ready, setReady] = useState(!IS_TAURI);
  const st = useAssetsStatus();
  const task = useExtractTask(st.data);
  const e = st.data?.editor;
  const known = !IS_TAURI || st.data !== undefined || st.isError;
  const artKey = e?.extracted ?? '';
  const modVersion = e?.modded?.version ?? '';
  useEffect(() => { assetsPresent().then(setAssets); }, []);
  // new art (first extraction, a game update): load the icons again
  const firstArt = useRef(true);
  useEffect(() => {
    if (!known) return;
    if (firstArt.current) { firstArt.current = false; return; }
    assetsPresent().then((ok) => { setAssets(ok); setGen((g) => g + 1); });
  }, [artKey, known]);
  // mod items: merge before the first mount, again whenever the overlay changes
  const merged = useRef(false);
  useEffect(() => {
    if (!known) return undefined;
    let live = true;
    loadOverlay(modVersion).then((ov) => {
      if (!live) return;
      mergeModded(ov, modVersion);
      if (merged.current) { setGen((g) => g + 1); reopenIfClean(); }
      merged.current = true;
      setReady(true);
    });
    return () => { live = false; };
  }, [modVersion, known]);
  // on the document: the catalog is a dialog drawn outside the editor's element (the desktop's Modal, a Deck dialog)
  useEffect(() => {
    const onError = (ev) => {
      const img = ev.target;
      if (img instanceof HTMLImageElement && img.src !== PLACEHOLDER && (root.current?.contains(img) || img.closest('.ke-cat, .ke-insp, .ke-slot'))) img.src = PLACEHOLDER;
    };
    document.addEventListener('error', onError, true);
    return () => document.removeEventListener('error', onError, true);
  }, [root]);
  const preparing = IS_TAURI && (task || (assets === false && e?.canExtract !== false));
  const hint = preparing ? 'Preparing game art…'
    : assets === false ? (IS_TAURI ? 'Game art needs the Enshrouded client (Settings → Games).' : 'Game art not found. Run tools/launcher/fetch-editor-assets.sh.')
      : null;
  return { ready, gen, assets, hint, preparing: !!preparing };
}
