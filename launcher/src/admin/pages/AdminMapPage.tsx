// /servers/:id/admin/map: the world map with the live overlays (players, waypoints, graves,
// protection zones, enemies) and the game's markers: MapView (../map).
import { useLayoutEffect, useRef, useState } from "react";
import { withAdmin } from "../common";
import { MapView } from "../map/MapView";

function MapPage() {
  const ref = useRef<HTMLDivElement>(null);
  const [h, setH] = useState(640);
  useLayoutEffect(() => {
    const fit = () => {
      const top = ref.current?.getBoundingClientRect().top ?? 0;
      setH(Math.max(420, window.innerHeight - top));
    };
    fit();
    window.addEventListener("resize", fit);
    return () => window.removeEventListener("resize", fit);
  }, []);
  return (
    <div ref={ref} className="ka-mapwrap-page">
      <MapView height={h} />
    </div>
  );
}

export default withAdmin(MapPage);
