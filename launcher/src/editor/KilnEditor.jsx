// Kiln wrapper around the vendored Save Editor (the game art and mod items: gameart). Mounted
// lazily by pages/EditorPage.tsx. Scopes the editor under .kiln-editor, registers its game fonts, merges the items of
// Kiln mods (the launcher's overlay, <art>/modded/items.json) into the editor's tables before a save is read, and
// shows placeholders plus a one-line "Preparing game art…" while the launcher makes the art from the game (it does that
// on its own: components/GameArt.tsx). Changes to upstream files: UPSTREAM.md.
import React, { useEffect, useRef, useState } from 'react';
import App from './src/App.jsx';
import { assetsPresent, registerFonts, IS_TAURI, ASSET_BASE } from './src/platform.js';
import { mergeModded } from './src/gamedata.js';
import { useAssetsStatus, useExtractTask } from '../components/GameArt';
import './src/styles.css';
import './kiln-editor.css';

// a neutral square for a missing game icon or texture
const PLACEHOLDER = 'data:image/svg+xml,' + encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect x="4" y="4" width="56" height="56" rx="8" fill="#2a2421" stroke="#4d413a" stroke-width="2"/><path d="M22 42l8-10 6 7 4-5 6 8z" fill="#6b5e55"/><circle cx="26" cy="24" r="4" fill="#6b5e55"/></svg>');

async function loadOverlay(version) {
  if (!version) return null;
  try {
    const r = await fetch(`${ASSET_BASE}modded/items.json?v=${encodeURIComponent(version)}`);
    return r.ok ? await r.json() : null;
  } catch {
    return null;
  }
}

export default function KilnEditor() {
  const root = useRef(null);
  const [assets, setAssets] = useState(null);
  // bumped when new art or mod items arrive: remounts the editor (images that fell back to placeholders load again,
  // the open save is read again with the new item tables)
  const [gen, setGen] = useState(0);
  const [ready, setReady] = useState(!IS_TAURI);
  const st = useAssetsStatus();
  const task = useExtractTask(st.data);
  const e = st.data?.editor;
  const known = !IS_TAURI || st.data !== undefined || st.isError;
  const artKey = e?.extracted ?? '';
  const modVersion = e?.modded?.version ?? '';
  useEffect(() => { registerFonts(); assetsPresent().then(setAssets); }, []);
  // new art (first extraction, a game update): fonts and images again
  const firstArt = useRef(true);
  useEffect(() => {
    if (!known) return;
    if (firstArt.current) { firstArt.current = false; return; }
    document.getElementById('kiln-editor-fonts')?.remove();
    registerFonts();
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
      if (merged.current) setGen((g) => g + 1);
      merged.current = true;
      setReady(true);
    });
    return () => { live = false; };
  }, [modVersion, known]);
  useEffect(() => {
    const el = root.current;
    if (!el) return undefined;
    const onError = (ev) => {
      const img = ev.target;
      if (img instanceof HTMLImageElement && img.src !== PLACEHOLDER) img.src = PLACEHOLDER;
    };
    el.addEventListener('error', onError, true);
    return () => el.removeEventListener('error', onError, true);
  }, []);
  const preparing = IS_TAURI && (task || (assets === false && e?.canExtract !== false));
  return (
    <div className={'kiln-editor' + (assets === false ? ' no-assets' : '')} ref={root}>
      {preparing ? (
        <div className="kiln-editor-hint" role="status">Preparing game art…</div>
      ) : assets === false && (
        <div className="kiln-editor-hint" role="status">
          {IS_TAURI ? 'Game art needs the Enshrouded client (Settings → Games).' : <>Game art not found. Run <code>tools/launcher/fetch-editor-assets.sh</code>.</>}
        </div>
      )}
      {ready && <App key={gen} />}
    </div>
  );
}
