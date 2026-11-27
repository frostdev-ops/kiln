// /servers/:id/admin/map: the world map with the live overlays (players, waypoints, graves,
// protection zones, enemies) and the game's markers: MapView (../map).
import { useLayoutEffect, useRef, useState } from "react";
import { useT } from "../../i18n";
import { withAdmin } from "../common";
import { MapView } from "../map/MapView";

function MapPage() {
  const t = useT();
  const ref = useRef<HTMLDivElement>(null);
  // the window's height under the page: the map's height where the page sits in no flex column (map.css); in one, as
  // in the launcher, the page fills the column and the map the page
  const [h, setH] = useState(480);
  useLayoutEffect(() => {
    const fit = () => {
      const top = ref.current?.getBoundingClientRect().top ?? 0;
      setH(Math.max(300, window.innerHeight - top - 18));
    };
    fit();
    window.addEventListener("resize", fit);
    return () => window.removeEventListener("resize", fit);
  }, []);
  return (
    <div ref={ref} className="ka-mapwrap-page">
      {/* the page's name for screen readers and outlines: the map fills the page, no visible header */}
      <h1 className="k-sr-only">{t("admin.pages.map")}</h1>
      <MapView height={h} />
    </div>
  );
}

export default withAdmin(MapPage);
