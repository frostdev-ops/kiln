// File drop target + path picker. In Tauri, drops arrive as window drag-drop events with
// real paths; in a plain browser (mock mode) the file names stand in for paths.
// Two forms: the niche (an arch that fills with fire while something is dragged over it or `busy` imports) and a
// one-line row ("Add a runtime bundle  kiln-runtime-<version>.zip  [Files]").
import { FolderOpen } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { isTauri } from "../api/commands";
import { Button } from "./Button";
import { TextInput } from "./Form";

export interface PickOptions {
  directory?: boolean;
  multiple?: boolean;
  /** e.g. [{ name: "Mods", extensions: ["zip", "dll"] }] */
  filters?: { name: string; extensions: string[] }[];
  title?: string;
  defaultPath?: string;
}

/** Native open dialog; null when canceled or not in Tauri. */
export async function pickPaths(opts: PickOptions = {}): Promise<string[] | null> {
  if (!isTauri()) return null;
  const { open } = await import("@tauri-apps/plugin-dialog");
  const r = await open({ directory: opts.directory, multiple: opts.multiple, filters: opts.filters, title: opts.title, defaultPath: opts.defaultPath });
  if (r === null) return null;
  return Array.isArray(r) ? r : [r];
}

export interface DropZoneProps {
  onPaths: (paths: string[]) => void;
  title?: ReactNode;
  hint?: ReactNode;
  /** The Choose button's dialog. */
  pick?: PickOptions;
  /** A second button for folders. */
  pickFolder?: boolean;
  disabled?: boolean;
  /** "row": one line; default: the niche. */
  variant?: "niche" | "row";
  /** The niche burns while an import runs; this replaces its word ("Firing"). */
  busy?: ReactNode;
}

const NICHE = "M1 299V128C1 74 62 31 116 1C170 31 231 74 231 128V299Z";

export function DropZone({ onPaths, title = "Drop files here", hint, pick, pickFolder, disabled, variant = "niche", busy }: DropZoneProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [over, setOver] = useState(false);
  const cb = useRef(onPaths);
  cb.current = onPaths;

  useEffect(() => {
    if (!isTauri() || disabled) return;
    let off: (() => void) | undefined;
    let live = true;
    const inside = (p?: { x: number; y: number }) => {
      const r = ref.current?.getBoundingClientRect();
      if (!r || !p) return false;
      const x = p.x / window.devicePixelRatio, y = p.y / window.devicePixelRatio;
      return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
    };
    import("@tauri-apps/api/webview").then(({ getCurrentWebview }) =>
      getCurrentWebview().onDragDropEvent((e) => {
        const p = e.payload;
        if (p.type === "over" || p.type === "enter") setOver(inside(p.position));
        else if (p.type === "drop") { setOver(false); if (inside(p.position) && p.paths.length) cb.current(p.paths); }
        else setOver(false);
      })).then((f) => (live ? (off = f) : f()));
    return () => { live = false; off?.(); };
  }, [disabled]);

  const browserDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setOver(false);
    if (disabled || isTauri()) return;
    const names = [...e.dataTransfer.files].map((f) => f.name);
    if (names.length) cb.current(names);
  };
  const choose = async (directory: boolean) => {
    const r = await pickPaths({ ...pick, directory, multiple: true });
    if (r?.length) onPaths(r);
  };

  const desk = isTauri() ? undefined : "Needs the desktop app";
  const buttons = (
    <div className="k-row" style={{ gap: 6 }}>
      <Button size={variant === "row" ? "s" : "m"} disabled={disabled || !isTauri()} title={desk} onClick={() => choose(false)}>Files</Button>
      {pickFolder && <Button size={variant === "row" ? "s" : "m"} disabled={disabled || !isTauri()} title={desk} onClick={() => choose(true)}>Folder</Button>}
    </div>
  );
  const dragProps = {
    onDragOver: (e: React.DragEvent) => { e.preventDefault(); if (!isTauri()) setOver(true); },
    onDragLeave: () => !isTauri() && setOver(false),
    onDrop: browserDrop,
  };
  if (variant === "row") {
    return (
      <div ref={ref} className={"k-drop" + (over ? " over" : "")} aria-disabled={disabled || undefined} {...dragProps}>
        <span className="k-drop-title">{title}</span>
        {hint && <span className="k-drop-hint">{hint}</span>}
        {buttons}
      </div>
    );
  }
  const hot = over || !!busy;
  return (
    <div ref={ref} className={"k-niche" + (hot ? " hot" : "")} aria-disabled={disabled || undefined} data-drop {...dragProps}>
      <div className="k-niche-fire" aria-hidden><i /></div>
      <svg viewBox="0 0 232 300" aria-hidden><path d={NICHE} /></svg>
      <div className="k-niche-in">
        <span className="k-niche-word" role={busy ? "status" : undefined}>{busy || title}</span>
        {hint && <span className="k-niche-hint">{hint}</span>}
        {buttons}
      </div>
    </div>
  );
}

export interface PathPickerProps {
  value: string;
  onChange: (path: string) => void;
  directory?: boolean;
  filters?: PickOptions["filters"];
  placeholder?: string;
  id?: string;
  disabled?: boolean;
}

/** A path box with a Browse button (native dialog in Tauri; typing works everywhere). */
export function PathPicker({ value, onChange, directory, filters, placeholder, id, disabled }: PathPickerProps) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  return (
    <div className="k-input-group">
      <TextInput id={id} mono value={text} placeholder={placeholder} disabled={disabled} spellCheck={false}
        onChange={(e) => setText(e.target.value)} onBlur={() => text !== value && onChange(text)} onKeyDown={(e) => { if (e.key === "Enter") onChange(text); }} />
      <Button icon={<FolderOpen />} disabled={disabled || !isTauri()} title={isTauri() ? "Browse" : "Needs the desktop app"}
        onClick={async () => { const r = await pickPaths({ directory, filters, defaultPath: value || undefined }); if (r?.[0]) onChange(r[0]); }}>
        Browse
      </Button>
    </div>
  );
}
