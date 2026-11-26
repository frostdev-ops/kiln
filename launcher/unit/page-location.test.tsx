// The shell's page location (shell/pageLocation.tsx) rests on React Router's UNSAFE_LocationContext, which is not a public
// API: these tests fail when an installed react-router no longer gives a page the address it was opened at (netjunk,
// 2026-11-26: Play from a server's World tab opened its Players tab while the sheet dropped), or is another version than
// the one it was checked with.
import { expect, test } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { MemoryRouter, NavigationType, Route, Routes, useLocation, type Location } from "react-router-dom";
import routerPackage from "react-router/package.json";
import routerDomPackage from "react-router-dom/package.json";
import { PageLocation, ROUTER_CHECKED } from "../src/shell/pageLocation";

const Where = () => <i>{useLocation().pathname}</i>;
/** A server's admin pages (admin/index.tsx): routes of their own, whose index sends to the Players tab. */
const Admin = () => <Routes><Route index element={<b>players</b>} /><Route path="map" element={<b>map</b>} /></Routes>;

/** The page left for Play: the router is at "/", the sheet still shows the server's World tab. */
function render(page: Location | null) {
  const inside = <><Where /><Routes><Route path="servers/:id/admin/*" element={<Admin />} /><Route path="*" element={<b>other</b>} /></Routes></>;
  return renderToStaticMarkup(
    <MemoryRouter initialEntries={["/"]}>
      <Routes><Route path="*" element={page ? <PageLocation location={page} type={NavigationType.Push}>{inside}</PageLocation> : inside} /></Routes>
    </MemoryRouter>,
  );
}

test("a page keeps the address it was opened at: useLocation and the routes inside it read it, not the router's", () => {
  const world: Location = { pathname: "/servers/lab/admin/map", search: "", hash: "", state: null, key: "world" };
  expect(render(world)).toBe("<i>/servers/lab/admin/map</i><b>map</b>");
  // without it the page reads the router's address (what sent Play to the Players tab)
  expect(render(null)).toBe("<i>/</i><b>other</b>");
});

test("react-router is the version the page location was checked with (UNSAFE_LocationContext is not a public API)", () => {
  // a new version: check the test above and tests/server-nav.spec.ts, then set ROUTER_CHECKED (shell/pageLocation.tsx)
  expect(`react-router ${routerPackage.version}`).toBe(`react-router ${ROUTER_CHECKED}`);
  expect(`react-router-dom ${routerDomPackage.version}`).toBe(`react-router-dom ${ROUTER_CHECKED}`);
});
