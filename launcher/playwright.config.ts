// Playwright smoke tests in mock mode: the vite dev server in a plain browser, so commands.ts
// answers from src/api/mock.ts.   bun run test:smoke
// Browser: Playwright's own chromium when installed (`bun x playwright install chromium`), else KILN_CHROMIUM or the
// newest chromium headless shell found in ~/.cache/ms-playwright (an older revision works for these tests).
// KILN_DEV_PORT runs (or reuses) the dev server on another port than 1420.
import { defineConfig } from "@playwright/test";
import { existsSync, readdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

function chromium(): string | undefined {
  if (process.env.KILN_CHROMIUM) return process.env.KILN_CHROMIUM;
  const cache = join(homedir(), ".cache", "ms-playwright");
  if (!existsSync(cache)) return undefined;
  const shells = readdirSync(cache).filter((d) => d.startsWith("chromium_headless_shell-")).sort().reverse();
  for (const s of shells) {
    const exe = join(cache, s, "chrome-headless-shell-linux64", "chrome-headless-shell");
    if (existsSync(exe)) return exe;
  }
  return undefined;
}

const PORT = Number(process.env.KILN_DEV_PORT || 1420);

export default defineConfig({
  testDir: "tests",
  timeout: 60_000,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: `http://localhost:${PORT}`,
    viewport: { width: 1360, height: 860 },
    launchOptions: { executablePath: chromium() },
  },
  webServer: { command: PORT === 1420 ? "bun run dev" : `bun run dev --port ${PORT}`, url: `http://localhost:${PORT}`, reuseExistingServer: true },
});
