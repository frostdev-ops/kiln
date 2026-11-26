// The address a page sees: the one it was opened at, also while the sheet (or the hero) lingers with it after it was left
// (AppShell's useLinger). A server's admin pages are routes of their own (admin/index.tsx), which read "/" as their index
// and sent Play back to the server's Players tab (netjunk, 2026-11-26).
//
// React Router has no public way to give a part of the page another location than the router's (<Routes location> covers
// its own routes only, not the admin pages' <Routes> inside them), so this provides its UNSAFE_LocationContext, which
// useLocation and every <Routes> read. unit/page-location.test.tsx checks that it still does, and that react-router is the
// version it was checked with (ROUTER_CHECKED): a new version fails there until it is checked again.
import { useMemo, type ReactNode } from "react";
import { UNSAFE_LocationContext as LocationContext, type Location, type NavigationType } from "react-router-dom";

/** The react-router (and react-router-dom) version the page location was checked with. */
export const ROUTER_CHECKED = "7.18.5";

/** `location` and `type` (useLocation, useNavigationType) for everything inside. */
export function PageLocation({ location, type, children }: { location: Location; type: NavigationType; children: ReactNode }) {
  const value = useMemo(() => ({ location, navigationType: type }), [location, type]);
  return <LocationContext.Provider value={value}>{children}</LocationContext.Provider>;
}
