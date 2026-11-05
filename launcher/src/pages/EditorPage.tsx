// /editor: the vendored Save Editor (src/editor/, MIT, UPSTREAM.md), loaded on first visit.
import { lazy, Suspense } from "react";
import { Spinner } from "../ui";

const KilnEditor = lazy(() => import("../editor/KilnEditor"));

export default function EditorPage() {
  return (
    <div className="k-editor-host">
      <Suspense fallback={<div style={{ display: "grid", placeItems: "center", height: "100%" }}><Spinner size={24} /></div>}>
        <KilnEditor />
      </Suspense>
    </div>
  );
}
