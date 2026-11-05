// UI for the admin pages: the design system (src/ui) plus the few pieces only the
// admin pages need: a suggestion input (item picker, command completion), confirm()/promptText() as promises (on the
// design system's Modal and ConfirmDanger), health bars, stat tiles, avatars and formatters. Styles: ../admin.css.
import { useEffect, useState, type KeyboardEvent, type ReactNode } from "react";
import { create } from "zustand";
import type { KilnError } from "../../api/commands";
import { Banner, Button, ConfirmDanger, Modal, TextInput, toast as uiToast } from "../../ui";

export * from "../../ui";

export const cx = (...c: (string | false | null | undefined)[]) => c.filter(Boolean).join(" ");

// ---------------------------------------------------------------- toasts (short forms)

export const toastOk = (title: ReactNode, detail?: ReactNode) => uiToast.ok(title, detail);
export const toastError = (e: unknown, title?: ReactNode) => uiToast.error(e, title);

// ---------------------------------------------------------------- confirm() / promptText()

interface ConfirmReq {
  title: ReactNode;
  body?: ReactNode;
  ok?: string;
  danger?: boolean;
  /** the user types this word to confirm (ConfirmDanger) */
  typed?: string;
  prompt?: { placeholder?: string; initial?: string; maxLength?: number; label?: ReactNode };
  resolve: (v: string | false) => void;
}
const useConfirmStore = create<{ req: ConfirmReq | null }>(() => ({ req: null }));

/** Asks; resolves true when confirmed. `typed` makes it a typed confirmation (ConfirmDanger). */
export function confirm(o: Omit<ConfirmReq, "resolve" | "prompt">): Promise<boolean> {
  return new Promise((res) => useConfirmStore.setState({ req: { ...o, resolve: (v) => res(v !== false) } }));
}
/** Asks for a short text (waypoint names, ...); resolves to it, or null. */
export function promptText(o: Omit<ConfirmReq, "resolve" | "typed"> & { prompt: NonNullable<ConfirmReq["prompt"]> }): Promise<string | null> {
  return new Promise((res) => useConfirmStore.setState({ req: { ...o, resolve: (v) => res(v === false ? null : v) } }));
}

/** Mounted once by AdminLayout: shows confirm() / promptText() requests. */
export function ConfirmHost() {
  const req = useConfirmStore((s) => s.req);
  const [text, setText] = useState("");
  useEffect(() => setText(req?.prompt?.initial ?? ""), [req]);
  const done = (v: string | false) => {
    req?.resolve(v);
    useConfirmStore.setState({ req: null });
  };
  if (req?.typed)
    return (
      <ConfirmDanger open title={req.title} message={req.body} word={req.typed} action={req.ok ?? "Confirm"} onClose={() => done(false)} onConfirm={() => done("ok")} />
    );
  const okEnabled = !!req && (!req.prompt || text.trim().length > 0);
  const submit = () => okEnabled && done(req?.prompt ? text.trim() : "ok");
  return (
    <Modal
      open={!!req}
      title={req?.title}
      onClose={() => done(false)}
      footer={
        <>
          <Button variant="ghost" onClick={() => done(false)}>Cancel</Button>
          <Button variant={req?.danger ? "danger-fill" : "primary"} disabled={!okEnabled} onClick={submit} data-autofocus={req?.prompt ? undefined : true}>
            {req?.ok ?? "Confirm"}
          </Button>
        </>
      }
    >
      <div className="k-stack">
        {req?.body}
        {req?.prompt ? (
          <TextInput data-autofocus value={text} placeholder={req.prompt.placeholder} maxLength={req.prompt.maxLength} onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); submit(); } }} />
        ) : null}
      </div>
    </Modal>
  );
}

// ---------------------------------------------------------------- small pieces

export function ErrorBanner({ error, title }: { error: KilnError | null | undefined; title?: ReactNode }) {
  if (!error) return null;
  return <Banner tone={error.code === "unsupported" ? "ember" : "warn"} title={title}>{error.message}</Banner>;
}

export function HealthBar({ health, max, wide }: { health?: number; max?: number; wide?: boolean }) {
  if (!max) return null;
  const pct = Math.max(0, Math.min(100, Math.round(((health ?? 0) / max) * 100)));
  return (
    <div className={cx("ka-hp", pct < 25 && "crit", pct >= 25 && pct < 50 && "low", wide && "wide")} title={`${health} / ${max} health`} role="meter" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
      <i style={{ width: `${pct}%` }} />
    </div>
  );
}

export function Stat({ k, v, s }: { k: ReactNode; v: ReactNode; s?: ReactNode }) {
  return (
    <div className="ka-stat">
      <div className="ka-stat-k">{k}</div>
      <div className="ka-stat-v">{v}</div>
      {s != null ? <div className="ka-stat-s">{s}</div> : null}
    </div>
  );
}

export const Stats = ({ children }: { children: ReactNode }) => <div className="ka-stats">{children}</div>;

export const initials = (n: string) => String(n).replace(/[^\p{L}\p{N}]/gu, "").slice(0, 2) || "?";
export function Avatar({ name, online }: { name: string; online?: boolean }) {
  return (
    <span className="ka-avatar">
      {initials(name)}
      {online ? <i className="ka-online" title="online" /> : null}
    </span>
  );
}

export interface Suggestion<T> {
  key: string;
  label: ReactNode;
  sub?: ReactNode;
  value: T;
}

/** A text input with a suggestion list under it: arrows + Enter/Tab pick, Escape closes. */
export function SuggestInput<T>({ value, onChange, suggestions, onPick, placeholder, onEnter, onTab, onHistory, autoFocus, className, mono, id, "aria-label": aria }: {
  value: string;
  onChange: (v: string) => void;
  suggestions: Suggestion<T>[];
  onPick: (s: Suggestion<T>) => void;
  placeholder?: string;
  onEnter?: () => void;
  /** Tab with the list closed (e.g. complete the command line) */
  onTab?: () => void;
  /** ArrowUp/ArrowDown with the list closed (command history) */
  onHistory?: (dir: -1 | 1) => void;
  autoFocus?: boolean;
  className?: string;
  mono?: boolean;
  id?: string;
  "aria-label"?: string;
}) {
  const [sel, setSel] = useState(-1);
  const [open, setOpen] = useState(false);
  useEffect(() => {
    setSel(-1);
    setOpen(suggestions.length > 0);
  }, [suggestions]);
  const pick = (s: Suggestion<T>) => {
    setOpen(false);
    onPick(s);
  };
  const key = (e: KeyboardEvent<HTMLInputElement>) => {
    const n = suggestions.length;
    const listed = open && n > 0;
    if (listed && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
      e.preventDefault();
      setSel((s) => (s + (e.key === "ArrowDown" ? 1 : -1) + n) % n);
      return;
    }
    if (!listed && onHistory && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
      e.preventDefault();
      onHistory(e.key === "ArrowUp" ? -1 : 1);
      return;
    }
    if (listed && (e.key === "Tab" || (e.key === "Enter" && sel >= 0))) {
      e.preventDefault();
      pick(suggestions[Math.max(0, sel)]);
      return;
    }
    if (e.key === "Escape" && listed) {
      e.preventDefault();
      setOpen(false);
      return;
    }
    if (e.key === "Tab" && onTab) {
      e.preventDefault();
      onTab();
      return;
    }
    if (e.key === "Enter" && onEnter) {
      e.preventDefault();
      setOpen(false);
      onEnter();
    }
  };
  return (
    <div className={cx("ka-suggest-host", className)}>
      <TextInput
        id={id}
        aria-label={aria}
        mono={mono}
        value={value}
        placeholder={placeholder}
        autoFocus={autoFocus}
        autoComplete="off"
        spellCheck={false}
        role="combobox"
        aria-expanded={open && suggestions.length > 0}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={key}
        onBlur={() => setTimeout(() => setOpen(false), 120)}
        onFocus={() => setOpen(suggestions.length > 0)}
      />
      {open && suggestions.length ? (
        <div className="ka-suggest" role="listbox">
          {suggestions.map((s, i) => (
            <button key={s.key} type="button" role="option" aria-selected={i === sel} className={i === sel ? "sel" : ""} onMouseDown={(e) => { e.preventDefault(); pick(s); }}>
              <span>{s.label}</span>
              {s.sub ? <small>{s.sub}</small> : null}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}

// ---------------------------------------------------------------- formatting

export function dur(sec: number) {
  sec = Math.max(0, Math.round(sec));
  if (sec < 60) return `${sec}s`;
  const m = Math.floor(sec / 60), h = Math.floor(m / 60), d = Math.floor(h / 24);
  if (d) return `${d}d ${h % 24}h`;
  if (h) return `${h}h ${m % 60}m`;
  return `${m}m`;
}
/** unix seconds -> "3 min ago" */
export const agoS = (t: number | null | undefined) => (t ? fmtAgoMs(t * 1000) : "-");
const fmtAgoMs = (ms: number) => {
  const s = Math.round((Date.now() - ms) / 1000);
  if (s < 45) return "just now";
  if (s < 86400 * 2) return `${dur(s)} ago`;
  return new Date(ms).toLocaleDateString([], { month: "short", day: "numeric" });
};
export const whenS = (t: number | null | undefined) =>
  t ? new Date(t * 1000).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "-";
export const fmtPos = (p?: { x: number; y: number; z: number } | null) => (p ? `${Math.round(p.x)}, ${Math.round(p.y)}, ${Math.round(p.z)}` : "-");
export const pretty = (s: string) => String(s).replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, (c) => c.toUpperCase());

/** Debounced value (search boxes). */
export function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

/** Re-renders every `ms` (relative times, stale state). */
export function useNow(ms: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(t);
  }, [ms]);
  return now;
}
