// Kiln wrapper around the vendored Save Editor (the game art and mod items: gameart). Mounted
// lazily by pages/EditorPage.tsx. The UI is Kiln's own (kiln/EditorApp.jsx, the launcher's look); upstream's in-game
// frame (src/App.jsx and its screens, src/styles.css, the game fonts) is no longer mounted. The art, the items of Kiln
// mods and the placeholders are kiln/art.js (the Deck screen uses it too); while the launcher makes the art from the
// game (it does that on its own: components/GameArt.tsx) a one-line "Preparing game art…" shows. Changes to upstream
// files: UPSTREAM.md.
import React, { useRef } from 'react';
import EditorApp from './kiln/EditorApp.jsx';
import { useEditorArt } from './kiln/art.js';
import './kiln/editor.css';
import './kiln-editor.css';

export default function KilnEditor() {
  const root = useRef(null);
  const art = useEditorArt(root);
  return (
    <div className={'kiln-editor' + (art.assets === false ? ' no-assets' : '')} ref={root}>
      {art.hint && (
        <div className="kiln-editor-hint" role="status">
          {art.hint === 'Game art not found. Run tools/launcher/fetch-editor-assets.sh.' ? <>Game art not found. Run <code>tools/launcher/fetch-editor-assets.sh</code>.</> : art.hint}
        </div>
      )}
      {art.ready && <EditorApp key={art.gen} />}
    </div>
  );
}
