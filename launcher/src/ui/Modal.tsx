// Modal and ConfirmDanger. Esc and the backdrop close; focus moves in and comes back.
import { X } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Button, IconButton } from "./Button";
import { Field, TextInput } from "./Form";

export interface ModalProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  /** px, default 520. */
  width?: number;
  /** Backdrop click and Esc do nothing (a task is running). */
  locked?: boolean;
}

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function Modal({ open, onClose, title, children, footer, width, locked }: ModalProps) {
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const before = document.activeElement as HTMLElement | null;
    const first = box.current?.querySelector<HTMLElement>("[data-autofocus]") ?? box.current?.querySelector<HTMLElement>(FOCUSABLE);
    first?.focus();
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !locked) { e.stopPropagation(); onClose(); }
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
  }, [open, locked, onClose]);
  if (!open) return null;
  return createPortal(
    <div className="k-modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget && !locked) onClose(); }}>
      <div ref={box} className="k-modal" role="dialog" aria-modal="true" aria-labelledby="k-modal-title" style={{ ["--w" as string]: `${width ?? 520}px` }}>
        <div className="k-modal-head">
          <h2 className="k-modal-title" id="k-modal-title">{title}</h2>
          <span className="k-spacer" />
          {!locked && <IconButton label="Close" icon={<X />} size="s" onClick={onClose} />}
        </div>
        <div className="k-modal-body">{children}</div>
        {footer && <div className="k-modal-foot">{footer}</div>}
      </div>
    </div>,
    document.body,
  );
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
}

/** A destructive action behind a typed confirmation. */
export function ConfirmDanger({ open, onClose, title, message, word, action, onConfirm }: ConfirmDangerProps) {
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (open) { setTyped(""); setBusy(false); } }, [open]);
  const ok = typed.trim() === word;
  const run = async () => {
    if (!ok || busy) return;
    setBusy(true);
    try { await onConfirm(); onClose(); } finally { setBusy(false); }
  };
  return (
    <Modal open={open} onClose={onClose} title={title} locked={busy}
      footer={<>
        <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="danger-fill" disabled={!ok} loading={busy} onClick={run}>{action}</Button>
      </>}>
      {message && <div>{message}</div>}
      <Field label={<>Type <span className="k-danger-word">{word}</span> to confirm</>} htmlFor="k-confirm-word">
        <TextInput id="k-confirm-word" data-autofocus value={typed} onChange={(e) => setTyped(e.target.value)} mono autoComplete="off" spellCheck={false}
          onKeyDown={(e) => { if (e.key === "Enter") run(); }} />
      </Field>
    </Modal>
  );
}
