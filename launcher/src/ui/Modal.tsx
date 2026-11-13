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

let covering = 0;
/** While a dialog covers the page, `<html data-covered>` is set (counted: dialogs can open over dialogs). WebKitGTK
 *  paints the page's thin overlay scrollbars after everything else, so the mods list's scrollbar was drawn across a
 *  confirmation (the owner's report of 13 Nov 2026); theme/base.css hides the page's scrollbars while it is set. */
export function useCoverPage(open: boolean) {
  useEffect(() => {
    if (!open) return;
    covering++;
    document.documentElement.setAttribute("data-covered", "");
    return () => {
      covering = Math.max(0, covering - 1);
      if (covering === 0) document.documentElement.removeAttribute("data-covered");
    };
  }, [open]);
}

export function Modal({ open, onClose, title, children, footer, width, locked, arch }: ModalProps) {
  const box = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const close = useRef(onClose);
  close.current = onClose;
  useCoverPage(open);
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
  /** Report action failures to the user, then reject to keep this dialog open for retry. */
  onConfirm: () => void | Promise<void>;
  /** Keeps the action disabled (e.g. while what the message names is still loading). */
  disabled?: boolean;
  /** This confirmation is a plain button unless the player turned typing on in Settings (removing a mod: the word is
   * the mod's id, which can be sixty characters long). Not for uninstalling, profiles, servers or saves. */
  relaxable?: boolean;
}

const TYPE_TO_CONFIRM = "kiln.confirm.typeNames";

/** Settings: "Type a mod's name to remove it" (off unless turned on; kept per launcher window, like other view choices).
 *  Removing a mod is one click with a plain confirmation by default. */
export function typeToConfirm(): boolean {
  try { return localStorage.getItem(TYPE_TO_CONFIRM) === "1"; } catch { return false; }
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
    try { await onConfirm(); }
    catch { return; } // The action reports the failure; keep the dialog open without leaking an event-handler rejection.
    finally { setBusy(false); }
    onClose();
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
