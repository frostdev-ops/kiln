// Kiln Launcher frontend. Dev server on 1420; Tauri loads it in dev.
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";

// the dev server keeps the browser mock in the page (src/api/mockLoad.dev.ts); a build loads it on first use
const mockInPage: Plugin = {
  name: "kiln-mock-in-page",
  apply: "serve",
  enforce: "pre",
  resolveId(id, importer) {
    return id === "./mockLoad" && importer?.replaceAll("\\", "/").endsWith("/src/api/commands.ts") ? fileURLToPath(new URL("./src/api/mockLoad.dev.ts", import.meta.url)) : null;
  },
};

export default defineConfig({
  plugins: [react(), mockInPage],
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    // the mock layer imports examples from ../schema
    fs: { allow: [".."] },
    watch: { ignored: ["**/src-tauri/**"] },
  },
  envPrefix: ["VITE_", "TAURI_ENV_"],
  // pre-bundle what lazy routes import (the save editor), so a first visit does not trigger a dep re-optimization
  optimizeDeps: { include: ["buffer", "hash-wasm", "@tauri-apps/plugin-dialog", "@tauri-apps/api/webview"] },
  build: {
    target: "es2022",
    sourcemap: true,
    // the save editor chunk (lazy) carries its item/skill data (~2.3 MB)
    chunkSizeWarningLimit: 2600,
  },
});
