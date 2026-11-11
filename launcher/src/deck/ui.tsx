// Deck UI pieces: big focusable buttons and rows, the screen frame, dialogs (a layer each), hold-to-confirm, and the
// toasts (top centre, a short title only; a notice that stays until closed is a dialog). Every control is a real <button>: A clicks it, a tap clicks it.
import { useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";
import { useToasts } from "../ui";
import { candidates, focusEl, focusFirst, focusable } from "./focus";
import { useLayer, type DeckAction } from "./layers";

// ---------------------------------------------------------------- buttons and rows

export interface DeckButtonProps {
  children: ReactNode;
  onClick?: () => void;
  primary?: boolean;
  danger?: boolean;
  disabled?: boolean;
  /** The hint bar's word for A on this control (default "Select"). */
  a?: string;
  /** Focus memory key. */
  id?: string;
  autoFocus?: boolean;
  className?: string;
  "aria-label"?: string;
}

export function DeckButton({ children, onClick, primary, danger, disabled, a, id, autoFocus, className, ...rest }: DeckButtonProps) {
  return (
    <button type="button" className={"deck-btn" + (primary ? " primary" : "") + (danger ? " danger" : "") + (className ? ` ${className}` : "")}
      disabled={disabled} onClick={onClick} data-deck-a={a} data-deck-id={id} data-deck-autofocus={autoFocus ? "" : undefined} aria-label={rest["aria-label"]}>
      {children}
    </button>
  );
}

export interface RowProps {
  title: ReactNode;
  sub?: ReactNode;
  /** Right side: a state word, a size. */
  end?: ReactNode;
  /** A lit flame (a mod that is on). */
  lit?: boolean;
  onClick?: () => void;
  onFocus?: () => void;
  a?: string;
  id?: string;
  /** A folder's path (Add mods: focus returns to the folder you came up from). */
  path?: string;
  disabled?: boolean;
  dim?: boolean;
  icon?: ReactNode;
  "aria-label"?: string;
}

/** One line of a list: the whole row is the button. */
export function Row({ title, sub, end, lit, onClick, onFocus, a, id, path, disabled, dim, icon, ...rest }: RowProps) {
  return (
    <button type="button" className={"deck-row" + (dim ? " dim" : "") + (lit === undefined ? "" : lit ? " lit" : " out")} onClick={onClick} onFocus={onFocus}
      disabled={disabled} data-deck-a={a} data-deck-id={id} data-deck-path={path} aria-label={rest["aria-label"]}>
      {lit !== undefined && <span className="deck-flame" aria-hidden><i /></span>}
      {icon && <span className="deck-row-icon" aria-hidden>{icon}</span>}
      <span className="deck-row-main">
        <span className="deck-row-title">{title}</span>
        {sub && <span className="deck-row-sub">{sub}</span>}
      </span>
      {end && <span className="deck-row-end">{end}</span>}
    </button>
  );
}

// ---------------------------------------------------------------- screens

const memory = new Map<string, string>();

export interface ScreenProps {
  /** Focus memory and test hook ("home", "mods"). */
  name: string;
  title?: ReactNode;
  /** One live sentence. */
  say?: ReactNode;
  actions?: DeckAction[];
  back?: () => void;
  backLabel?: string;
  children: ReactNode;
  className?: string;
}

/** A tab's page: the base layer. Focus goes to its autofocus element (or what was focused when it was left) as soon
 *  as something focusable shows up. */
export function Screen({ name, title, say, actions, back, backLabel, children, className }: ScreenProps) {
  const ref = useRef<HTMLElement>(null);
  useLayer(ref, { actions, back, backLabel });
  useFirstFocus(ref, memory.get(name) ?? null);
  return (
    <section ref={ref} className={"deck-screen" + (className ? ` ${className}` : "")} data-deck-screen={name} aria-label={typeof title === "string" ? title : name}
      onFocus={(e) => { const id = (e.target as HTMLElement).dataset?.deckId; if (id) memory.set(name, id); }}>
      {(title || say) && (
        <header className="deck-screen-head">
          {title && <h1 className="deck-title">{title}</h1>}
          {say && <p className="deck-say">{say}</p>}
        </header>
      )}
      {children}
    </section>
  );
}

/** Focuses the layer's first stop once it has one (data loads after the mount), unless focus is already inside.
 *  `prefer`: a dialog's choice among the candidates (its autofocus, else its primary button). */
export function useFirstFocus(ref: RefObject<HTMLElement | null>, remembered: string | null, prefer?: (list: HTMLElement[]) => HTMLElement | undefined) {
  useEffect(() => {
    let tries = 0;
    const tick = () => {
      const el = ref.current;
      tries++;
      if (!el || tries > 40) return true;
      if (el.hasAttribute("inert")) return false;
      if (el.contains(document.activeElement) && document.activeElement !== el) return true;
      const pick = prefer?.(candidates(el));
      if (pick) { focusEl(pick); return true; }
      return !!focusFirst(el, remembered);
    };
    if (tick()) return;
    const t = setInterval(() => { if (tick()) clearInterval(t); }, 60);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
}

/** A dialog's first stop: its autofocus element, else its primary button, else the first. */
export const dialogFirst = (list: HTMLElement[]) =>
  list.find((x) => x.hasAttribute("data-deck-autofocus")) ?? list.find((x) => x.classList.contains("primary")) ?? list[0];

// ---------------------------------------------------------------- dialogs

export interface DialogProps {
  title: ReactNode;
  onClose: () => void;
  children?: ReactNode;
  /** The buttons (DeckButton), left to right; the first primary one gets focus. */
  buttons?: ReactNode;
  actions?: DeckAction[];
  backLabel?: string;
  /** B does nothing (a running step). */
  locked?: boolean;
  wide?: boolean;
  name?: string;
}

/** A dialog over the screen: its own layer (B closes it), focus comes back where it was when it closes. */
export function Dialog({ title, onClose, children, buttons, actions, backLabel = "Cancel", locked, wide, name }: DialogProps) {
  const ref = useRef<HTMLDivElement>(null);
  const opener = useRef<HTMLElement | null>(document.activeElement as HTMLElement | null);
  useLayer(ref, { actions, back: locked ? undefined : onClose, backLabel: locked ? undefined : backLabel, modal: true });
  useFirstFocus(ref, null, dialogFirst);
  useReturnFocus(opener);
  return (
    <Overlay name={name}>
      <div ref={ref} className={"deck-dialog" + (wide ? " wide" : "")} role="dialog" aria-modal="true" aria-label={typeof title === "string" ? title : undefined}>
        <h2 className="deck-dialog-title">{title}</h2>
        {children && <div className="deck-dialog-body">{children}</div>}
        {buttons && <div className="deck-dialog-buttons">{buttons}</div>}
      </div>
    </Overlay>
  );
}

/** Gives focus back to what opened a dialog when it closes. */
export function useReturnFocus(opener: RefObject<HTMLElement | null>) {
  useEffect(() => {
    const back = opener.current;
    return () => {
      // the screen under the dialog is live again a frame or two later (useInertBelowTop)
      let tries = 0;
      const t = setInterval(() => {
        const screen = document.querySelector<HTMLElement>("[data-deck-screen]");
        if (++tries > 20 || !screen) { clearInterval(t); return; }
        if (screen.hasAttribute("inert")) return;
        clearInterval(t);
        if (document.querySelector('[role="dialog"]')) return; // another dialog took over
        if (back?.isConnected && focusable(back)) back.focus({ preventScroll: true });
        // what opened it is gone (the screen changed under it): the screen's first stop
        else if (!screen.contains(document.activeElement) || document.activeElement === screen) focusFirst(screen);
      }, 30);
    };
  }, [opener]);
}

/** The veil a dialog sits on, drawn above the screen (outside it: the screen goes inert under a dialog). */
export function Overlay({ name, children }: { name?: string; children: ReactNode }) {
  const host = document.querySelector(".deck-overlays") ?? document.body;
  return createPortal(<div className="deck-veil" data-deck-dialog={name}>{children}</div>, host);
}

// ---------------------------------------------------------------- hold to confirm

export const HOLD_MS = 1200;

/** Destructive actions: hold A (or the finger) for 1.2 s; letting go early does nothing. The Deck's stand-in for the
 *  desktop's typed word. */
export function HoldButton({ children, onConfirm, disabled, id }: { children: ReactNode; onConfirm: () => void; disabled?: boolean; id?: string }) {
  const ref = useRef<HTMLButtonElement>(null);
  const [since, setSince] = useState<number | null>(null);
  const armed = useRef(false);
  const hold = (down: boolean) => { armed.current = down && !ref.current?.disabled; setSince(armed.current ? performance.now() : null); };
  const done = useRef(onConfirm);
  done.current = onConfirm;
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const on = (e: Event) => {
      const down = (e as CustomEvent<boolean>).detail;
      hold(down);
    };
    el.addEventListener("deck-hold", on);
    return () => el.removeEventListener("deck-hold", on);
  }, []);
  useEffect(() => {
    if (since === null) return;
    if (disabled) { hold(false); return; }
    const t = setTimeout(() => {
      const confirmed = armed.current && !ref.current?.disabled;
      hold(false);
      if (confirmed) done.current();
    }, HOLD_MS);
    return () => clearTimeout(t);
  }, [since, disabled]);
  useEffect(() => {
    const cancel = () => hold(false);
    const visibility = () => { if (document.hidden) cancel(); };
    window.addEventListener("blur", cancel);
    document.addEventListener("visibilitychange", visibility);
    return () => { window.removeEventListener("blur", cancel); document.removeEventListener("visibilitychange", visibility); };
  }, []);
  return (
    <button ref={ref} type="button" className={"deck-btn danger deck-hold" + (since !== null ? " holding" : "")} disabled={disabled} data-deck-hold="" data-deck-a="Hold" data-deck-id={id}
      style={{ ["--hold" as string]: `${HOLD_MS}ms` }}
      onPointerDown={() => hold(true)} onPointerUp={() => hold(false)} onPointerLeave={() => hold(false)}
      onPointerCancel={() => hold(false)} onBlur={() => hold(false)}
      onClick={(e) => e.preventDefault()}>
      <span className="deck-hold-fill" aria-hidden />
      <span className="deck-hold-label">{children}</span>
    </button>
  );
}

// ---------------------------------------------------------------- toasts

/** The shared toasts (ui/Toast.tsx), drawn big at the top; errors go after 6 s here (nothing to click them away with).
 *  A notice that stays until it is closed (mod folders Kiln kept, their paths and what to do; the "N earlier notices"
 *  fold of older ones) is a dialog instead, one at a time with all its text: the right stick scrolls it, A on OK or B
 *  closes it, so a pad alone can read and dismiss it. One dialog stays open while notices follow each other, so focus
 *  goes back where it was when the first came. An error folded into the earlier notices goes after 6 s on its own. */
export function DeckToaster() {
  const { items, dismiss } = useToasts();
  useEffect(() => {
    const errs = items.flatMap((t) => (t.folded ? t.folded.filter((f) => f.kind === "err") : t.kind === "err" ? [t] : []));
    if (!errs.length) return;
    const t = setTimeout(() => errs.forEach((e) => dismiss(e.id)), 6000);
    return () => clearTimeout(t);
  }, [items, dismiss]);
  const lasting = (t: (typeof items)[number]) => !!t.folded || (!!t.persistent && t.kind !== "err");
  const notice = items.find(lasting);
  const bar = items.filter((t) => !lasting(t));
  const times = notice && (notice.count ?? 1) > 1 ? ` (${notice.count} times)` : "";
  return (
    <>
      {bar.length > 0 && (
        <div className="deck-toasts" aria-live="polite">
          {bar.slice(-2).map((t) => (
            <div key={t.id} className={`deck-toast ${t.kind}`} role={t.kind === "err" ? "alert" : "status"} onClick={() => dismiss(t.id)}>
              <span className="deck-toast-title">{t.title}</span>
              {t.kind === "err" && t.detail && <span className="deck-toast-detail">{t.detail}</span>}
            </div>
          ))}
        </div>
      )}
      {notice && (
        <Dialog name="notice" title={typeof notice.title === "string" ? notice.title + times : <>{notice.title}{times}</>}
          onClose={() => dismiss(notice.id)} backLabel="Close"
          buttons={<DeckButton primary onClick={() => dismiss(notice.id)}>OK</DeckButton>}>
          {notice.detail && <div className="deck-scroll" data-deck-notice="" style={{ overflowWrap: "anywhere" }}>{notice.detail}</div>}
        </Dialog>
      )}
    </>
  );
}

/** A state word in a glaze colour. */
export function Word({ tone, children }: { tone: "ok" | "warn" | "err" | "info" | "idle" | "ember"; children: ReactNode }) {
  return <span className={`deck-word ${tone}`}>{children}</span>;
}
