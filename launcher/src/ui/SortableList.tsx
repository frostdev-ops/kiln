// Drag-reorder list. Mouse/touch: drag the grip (pointer events, so it also works where the
// webview's file-drop handler disables HTML5 drag and drop, e.g. Windows). Keyboard: focus the grip, Alt+Up/Down;
// or the arrow buttons.
import { ChevronDown, ChevronUp, GripVertical } from "lucide-react";
import { useRef, useState, type ReactNode } from "react";
import { IconButton } from "./Button";

export interface SortableListProps<T> {
  items: T[];
  itemKey: (item: T) => string;
  render: (item: T, index: number) => ReactNode;
  onReorder: (items: T[]) => void;
  disabled?: boolean;
  "aria-label"?: string;
}

export function SortableList<T>({ items, itemKey, render, onReorder, disabled, ...rest }: SortableListProps<T>) {
  const box = useRef<HTMLDivElement>(null);
  const [drag, setDrag] = useState<{ from: number; over: number } | null>(null);
  const move = (from: number, to: number) => {
    if (to < 0 || to >= items.length || from === to) return;
    const next = [...items];
    const [x] = next.splice(from, 1);
    next.splice(to, 0, x);
    onReorder(next);
  };
  const indexAt = (y: number) => {
    const rows = [...(box.current?.querySelectorAll<HTMLElement>(":scope > .k-sort-item") ?? [])];
    for (let i = 0; i < rows.length; i++) {
      const r = rows[i].getBoundingClientRect();
      if (y < r.top + r.height / 2) return i;
    }
    return rows.length - 1;
  };
  const start = (e: React.PointerEvent, i: number) => {
    if (disabled || e.button !== 0) return;
    e.preventDefault();
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
    setDrag({ from: i, over: i });
  };
  return (
    <div ref={box} className="k-sort" role="list" aria-label={rest["aria-label"]}>
      {items.map((it, i) => (
        <div key={itemKey(it)} role="listitem" className={"k-sort-item" + (drag?.from === i ? " dragging" : "") + (drag && drag.over === i && drag.from !== i ? " over" : "")}>
          <span className="k-grip" tabIndex={disabled ? -1 : 0} aria-label={`Reorder ${itemKey(it)}`} title="Drag, or Alt+Up/Down" style={{ touchAction: "none" }}
            onPointerDown={(e) => start(e, i)}
            onPointerMove={(e) => { if (drag) { const o = indexAt(e.clientY); if (o !== drag.over) setDrag({ ...drag, over: o }); } }}
            onPointerUp={() => { if (drag) { move(drag.from, drag.over); setDrag(null); } }}
            onPointerCancel={() => setDrag(null)}
            onKeyDown={(e) => { if (e.altKey && e.key === "ArrowUp") { e.preventDefault(); move(i, i - 1); } if (e.altKey && e.key === "ArrowDown") { e.preventDefault(); move(i, i + 1); } }}>
            <GripVertical size={16} />
          </span>
          <div style={{ flex: 1, minWidth: 0 }}>{render(it, i)}</div>
          {!disabled && items.length > 1 && (
            <span className="k-row" style={{ gap: 0 }}>
              <IconButton label="Move up" size="s" icon={<ChevronUp />} disabled={i === 0} onClick={() => move(i, i - 1)} />
              <IconButton label="Move down" size="s" icon={<ChevronDown />} disabled={i === items.length - 1} onClick={() => move(i, i + 1)} />
            </span>
          )}
        </div>
      ))}
    </div>
  );
}
