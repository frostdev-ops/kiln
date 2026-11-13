// Deck UI pieces: big focusable buttons and rows, the screen frame, dialogs (a layer each), hold-to-confirm, and the
// toasts (top centre, a short title only; a notice that stays until closed is a dialog). Every control is a real <button>: A clicks it, a tap clicks it.
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";
import { useToasts } from "../ui";
import type { ToastItem } from "../ui/Toast";
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
 *  goes back where it was when the first came.
 *  A notice counts as seen (its `onClosed`) only when the player closes it: A on OK, or B.
 *  An error whose text does not fit the bar (three lines of title, four of detail) is such a dialog too: in the bar it
 *  ran off the screen's lower edge, with nothing to scroll it and six seconds to read it. As a dialog it stays until
 *  closed.
 *  The six seconds are only for an error that was drawn in the bar and fits it. One that waits behind the two the bar
 *  shows has not been seen yet and waits its turn; one folded into the earlier notices stays with them until the
 *  player closes them (it may have been folded before it was ever drawn or measured, so nothing is known of it). */
export function DeckToaster() {
  const { items, dismiss, close } = useToasts();
  // what BarToast found of each error it drew, by toast id (an id is never used twice): the part that did not fit the
  // bar, or that it fits. An error that was never drawn is not in here.
  const [measured, setMeasured] = useState<ReadonlyMap<number, Fit>>(() => new Map());
  const onMeasured = (id: number, fit: Fit) =>
    setMeasured((prev) => {
      const was = prev.get(id);
      // once too long, always: as a dialog it is no longer measured, and a text never shrinks back into the bar
      return was === fit || (was !== undefined && was !== "fits") ? prev : new Map(prev).set(id, fit);
    });
  const long = (id: number) => { const fit = measured.get(id); return fit === "title" || fit === "detail"; };
  useEffect(() => {
    // forget the ones that are gone (one folded into the earlier notices is still here)
    const here = new Set(items.flatMap((t) => [t.id, ...(t.folded ?? []).map((f) => f.id)]));
    setMeasured((prev) => ([...prev.keys()].every((id) => here.has(id)) ? prev : new Map([...prev].filter(([id]) => here.has(id)))));
  }, [items]);
  useEffect(() => {
    // six seconds for the errors the bar has drawn and that fit it; no others (see above)
    const errs = items.filter((t) => t.kind === "err" && !t.folded && measured.get(t.id) === "fits");
    if (!errs.length) return;
    const t = setTimeout(() => errs.forEach((e) => dismiss(e.id)), 6000);
    return () => clearTimeout(t);
  }, [items, dismiss, measured]);
  const lasting = (t: (typeof items)[number]) => !!t.folded || (!!t.persistent && t.kind !== "err") || long(t.id);
  const notice = items.find(lasting);
  const bar = items.filter((t) => !lasting(t));
  const times = notice && (notice.count ?? 1) > 1 ? ` (${notice.count} times)` : "";
  // a long error whose title is the long text: the dialog's heading stays short, and the text is in the body, which scrolls
  const titled = notice && measured.get(notice.id) === "title";
  return (
    <>
      {bar.length > 0 && (
        <div className="deck-toasts" aria-live="polite">
          {bar.slice(-2).map((t) => <BarToast key={t.id} t={t} onMeasured={onMeasured} onClick={() => close(t.id)} />)}
        </div>
      )}
      {notice && (
        <Dialog name="notice"
          title={titled ? "Something went wrong" + times : typeof notice.title === "string" ? notice.title + times : <>{notice.title}{times}</>}
          onClose={() => close(notice.id)} backLabel="Close"
          buttons={<DeckButton primary onClick={() => close(notice.id)}>OK</DeckButton>}>
          {(titled || notice.detail) && (
            <div className="deck-scroll" data-deck-notice="" data-kind={notice.kind} style={{ overflowWrap: "anywhere" }}>
              {titled && <div>{notice.title}</div>}
              {notice.detail}
            </div>
          )}
        </Dialog>
      )}
    </>
  );
}

/** One toast of the bar. An error's title gets three lines and its detail four (deck.css); when either needs more,
 *  the toaster is told before the frame is painted and shows the error as a dialog instead. The text sits in an inner
 *  element that is watched for growing later (a detail that is a component of its own can change without this one
 *  rendering again). */
/** What the bar found of an error it drew: the part that does not fit, or that all of it does. */
type Fit = "title" | "detail" | "fits";

function BarToast({ t, onMeasured, onClick }: { t: ToastItem; onMeasured: (id: number, fit: Fit) => void; onClick: () => void }) {
  const title = useRef<HTMLSpanElement>(null);
  const detail = useRef<HTMLSpanElement>(null);
  const err = t.kind === "err";
  // what was last told to the toaster: the same answer is not told again (telling it on every render is a state update
  // in each commit, and under load those nested until React stopped them: "Maximum update depth exceeded")
  const told = useRef<Fit | null>(null);
  const measure = () => {
    if (!err) return;
    const over = (el: HTMLElement | null) => !!el && el.scrollHeight > el.clientHeight + 1;
    const fit: Fit = over(title.current) ? "title" : over(detail.current) ? "detail" : "fits";
    if (fit === told.current) return;
    told.current = fit;
    onMeasured(t.id, fit);
  };
  useLayoutEffect(measure);
  useEffect(() => {
    if (!err || typeof ResizeObserver === "undefined") return;
    const watch = new ResizeObserver(measure);
    for (const el of [title.current, detail.current]) if (el?.firstElementChild) watch.observe(el.firstElementChild);
    return () => watch.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [err, t.id]);
  return (
    <div className={`deck-toast ${t.kind}`} role={err ? "alert" : "status"} onClick={onClick}>
      <span ref={title} className="deck-toast-title"><span>{t.title}</span></span>
      {err && t.detail && <span ref={detail} className="deck-toast-detail"><span>{t.detail}</span></span>}
    </div>
  );
}

/** A state word in a glaze colour. */
export function Word({ tone, children }: { tone: "ok" | "warn" | "err" | "info" | "idle" | "ember"; children: ReactNode }) {
  return <span className={`deck-word ${tone}`}>{children}</span>;
}
