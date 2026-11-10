// The browser mock (./mock.ts with its data, ~170 KB) for a build: a chunk of its own, loaded on first use, so the
// desktop app never fetches it unless a development fallback asks (./fallback.ts). The dev server (vite serve, the
// Playwright tests) uses ./mockLoad.dev.ts instead (vite.config.ts): the mock is part of the page there, as before.
export const loadMock = () => import("./mock");
