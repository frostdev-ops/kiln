// Kiln Launcher frontend. Dev server on 1420; Tauri loads it in dev.
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
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
