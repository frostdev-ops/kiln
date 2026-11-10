// ./mockLoad.ts for the dev server (vite.config.ts aliases it): the mock loads with the page.
import * as mock from "./mock";

export const loadMock = () => Promise.resolve(mock);
