// Kiln: the editor runs inside the Kiln Launcher. `api` is the Kiln port of tauri-api.js (editor_* commands through
// launcher/src/api/commands.ts; in a plain browser the launcher's mock answers). Game assets (fonts, icons, UI
// textures) come from the launcher's kiln-asset protocol (tools/launcher/fetch-editor-assets.sh copies them locally;
// they are never committed). Upstream: platform.js picked window.api (Electron) or tauri-api.js, game:// assets.
import tauriApi from './tauri-api.js';
import { isTauri } from '../../api/commands';

export const IS_TAURI = isTauri();
export const api = tauriApi;
const WINDOWS = typeof navigator !== 'undefined' && /Windows/i.test(navigator.userAgent);
// Tauri 2 custom protocols: kiln-asset://localhost/ on Linux/macOS, http://kiln-asset.localhost/ on Windows.
// The vite dev server (browser mock and `tauri dev`) serves launcher/assets-local/ itself: WebKitGTK does not load
// custom-scheme images into a page from http://localhost.
export const ASSET_BASE = import.meta.env.DEV || !IS_TAURI
  ? '/assets-local/editor/'
  : (WINDOWS ? 'http://kiln-asset.localhost/editor/' : 'kiln-asset://localhost/editor/');

// fontes do jogo (UiFontResource), registradas aqui porque o endereço depende da plataforma
export function registerFonts() {
  if (document.getElementById('kiln-editor-fonts')) return;
  const faces = [
    ['Harmonique', 'font00.otf', 400, 'normal'], ['Harmonique', 'font04.otf', 400, 'italic'],
    ['Harmonique', 'font24.otf', 700, 'normal'], ['Harmonique', 'font08.otf', 700, 'italic'],
    ['NotoSerifGame', 'font10.ttf', 400, 'normal'], ['NotoSerifGame', 'font26.ttf', 700, 'normal'],
  ];
  const css = faces.map(([f, file, w, s]) => `@font-face { font-family: '${f}'; src: url('${ASSET_BASE}fonts/${file}'); font-weight: ${w}; font-style: ${s}; }`).join('\n');
  const el = document.createElement('style');
  el.id = 'kiln-editor-fonts';
  el.textContent = css;
  document.head.appendChild(el);
}

/** Kiln: true when the game assets answer (else the editor shows placeholders and a hint). */
export async function assetsPresent() {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => resolve(true);
    img.onerror = () => resolve(false);
    img.src = `${ASSET_BASE}ui/tinted/tab_mark.webp`;
  });
}
