// Dialogs. Working dialogs (forms: install, play a profile once, modpack offer) are a plain Soot 2 panel with a
// Grenze 36 title and the actions bottom right. The pointed arch (`arch`) is only for warnings and typed confirmation:
// 500 wide, it rises from its base, at most three lines plus the input. ConfirmDanger keeps its typed word.
// Esc and the backdrop close; focus moves in, is trapped, and comes back.
import { X } from "lucide-react";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Button, IconButton } from "./Button";
import { TextInput } from "./Form";

export interface ModalProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  /** px, default 520 (working dialogs: 480-640). */
  width?: number;
  /** Backdrop click and Esc do nothing (a task is running). */
  locked?: boolean;
  /** The pointed arch: a warning or a typed confirmation. */
  arch?: boolean;
}

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function Modal({ open, onClose, title, children, footer, width, locked, arch }: ModalProps) {
  const box = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    if (!open) return;
    const before = document.activeElement as HTMLElement | null;
    const first = box.current?.querySelector<HTMLElement>("[data-autofocus]") ?? box.current?.querySelector<HTMLElement>(FOCUSABLE);
    first?.focus();
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !locked) { e.stopPropagation(); close.current(); }
      if (e.key === "Tab" && box.current) {
        const items = [...box.current.querySelectorAll<HTMLElement>(FOCUSABLE)];
        if (!items.length) return;
        const [a, z] = [items[0], items[items.length - 1]];
        if (e.shiftKey && document.activeElement === a) { e.preventDefault(); z.focus(); }
        else if (!e.shiftKey && document.activeElement === z) { e.preventDefault(); a.focus(); }
      }
    };
    window.addEventListener("keydown", key, true);
    return () => { window.removeEventListener("keydown", key, true); before?.focus?.(); };
  }, [open, locked]);
  if (!open) return null;
  const backdrop = (e: React.MouseEvent) => { if (e.target === e.currentTarget && !locked) onClose(); };
  if (arch) {
    return createPortal(
      <div className="k-modal-backdrop" onMouseDown={backdrop}>
        <div ref={box} className="k-arch" role="dialog" aria-modal="true" aria-labelledby={titleId}>
          <svg viewBox="0 0 500 460" preserveAspectRatio="none" aria-hidden><path d="M1 459V210C1 130 130 60 250 1C370 60 499 130 499 210V459Z" /></svg>
          <div className="k-arch-in">
            <h2 id={titleId} className="k-arch-title">{title}</h2>
            {children}
            {footer && <div className="k-arch-foot">{footer}</div>}
          </div>
        </div>
      </div>,
      document.body,
    );
  }
  return createPortal(
    <div className="k-modal-backdrop" onMouseDown={backdrop}>
      <div ref={box} className="k-modal" role="dialog" aria-modal="true" aria-labelledby={titleId} style={{ ["--w" as string]: `${width ?? 520}px` }}>
        <div className="k-modal-head">
          <h2 className="k-modal-title" id={titleId}>{title}</h2>
          <span className="k-spacer" />
          {!locked && !footer && <IconButton label="Close" icon={<X />} size="s" onClick={onClose} />}
        </div>
        <div className="k-modal-body">{children}</div>
        {footer && <div className="k-modal-foot">{footer}</div>}
      </div>
    </div>,
    document.body,
  );
}

/** The text block of an arch dialog (one to three lines). */
export function ArchText({ children }: { children: ReactNode }) {
  return <div className="k-arch-body">{children}</div>;
}

export interface ConfirmDangerProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  /** One or two short lines: what happens. */
  message?: ReactNode;
  /** The word the user types to confirm (e.g. the profile id, "switch"). */
  word: string;
  /** Button label, e.g. "Delete". */
  action: string;
  onConfirm: () => void | Promise<void>;
  /** Keeps the action disabled (e.g. while what the message names is still loading). */
  disabled?: boolean;
  /** This confirmation may be a plain button when the user turned typing off in Settings (removing a mod from the
   * Library: the word is the mod's id, which can be sixty characters long). Not for uninstalling, servers or saves. */
  relaxable?: boolean;
}

const TYPE_TO_CONFIRM = "kiln.confirm.typeNames";

/** Settings: "Type a mod's name to remove it" (on unless turned off; kept per launcher window, like other view choices). */
export function typeToConfirm(): boolean {
  try { return localStorage.getItem(TYPE_TO_CONFIRM) !== "0"; } catch { return true; }
}

export function setTypeToConfirm(on: boolean) {
  try { localStorage.setItem(TYPE_TO_CONFIRM, on ? "1" : "0"); } catch { /* the choice lasts for this window only */ }
}

/** A destructive action behind a typed confirmation, in the arch. The danger fill stays disabled until the word matches. */
export function ConfirmDanger({ open, onClose, title, message, word, action, onConfirm, disabled, relaxable }: ConfirmDangerProps) {
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  const [typing, setTyping] = useState(true);
  useEffect(() => { if (open) { setTyped(""); setBusy(false); setTyping(!relaxable || typeToConfirm()); } }, [open, relaxable]);
  const ok = (!typing || typed.trim() === word) && !disabled;
  const run = async () => {
    if (!ok || busy) return;
    setBusy(true);
    try { await onConfirm(); onClose(); } finally { setBusy(false); }
  };
  return (
    <Modal arch open={open} onClose={onClose} title={title} locked={busy}
      footer={<>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="danger-fill" disabled={!ok} loading={busy} onClick={run}>{action}</Button>
      </>}>
      {message && <ArchText>{message}</ArchText>}
      {typing && (
        <div className="k-arch-typed">
          <label htmlFor="k-confirm-word">Type <span className="k-danger-word">{word}</span> to confirm</label>
          <TextInput id="k-confirm-word" data-autofocus value={typed} onChange={(e) => setTyped(e.target.value)} mono autoComplete="off" spellCheck={false}
            onKeyDown={(e) => { if (e.key === "Enter") run(); }} />
        </div>
      )}
    </Modal>
  );
}
