// Form primitives: Field, TextInput, NumberInput, Select, Slider, TextArea, Toggle, WellInput (a well with its label
// inside: Filter, Search items), FlameToggle (light a mod or put it out). Fields sit in wells; the rim catches flame on focus.
import { forwardRef, useEffect, useId, useRef, useState, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";

export interface FieldProps {
  label?: ReactNode;
  hint?: ReactNode;
  error?: ReactNode;
  /** Badges next to the label (restart, synced). */
  badges?: ReactNode;
  /** Id of the control (for the label); generated when absent. */
  htmlFor?: string;
  children: ReactNode;
}

export function Field({ label, hint, error, badges, htmlFor, children }: FieldProps) {
  return (
    <div className="k-field">
      {(label || badges) && (
        <div className="k-field-head">
          {label && <label className="k-label" htmlFor={htmlFor}>{label}</label>}
          {badges}
        </div>
      )}
      {children}
      {error ? <div className="k-error-text">{error}</div> : hint ? <div className="k-hint">{hint}</div> : null}
    </div>
  );
}

export interface TextInputProps extends InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
  mono?: boolean;
}

export const TextInput = forwardRef<HTMLInputElement, TextInputProps>(function TextInput({ invalid, mono, className, ...rest }, ref) {
  return <input ref={ref} className={["k-input", mono && "k-input-mono", invalid && "invalid", className].filter(Boolean).join(" ")} aria-invalid={invalid || undefined} {...rest} />;
});

export const TextArea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement> & { invalid?: boolean }>(function TextArea(
  { invalid, className, ...rest }, ref,
) {
  return <textarea ref={ref} className={["k-textarea", invalid && "invalid", className].filter(Boolean).join(" ")} aria-invalid={invalid || undefined} {...rest} />;
});

export interface SelectProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, "onChange"> {
  options: (string | { value: string; label: ReactNode })[];
  onChange: (value: string) => void;
  invalid?: boolean;
}

export const Select = forwardRef<HTMLSelectElement, SelectProps>(function Select({ options, onChange, invalid, className, ...rest }, ref) {
  return (
    <select ref={ref} className={["k-select", invalid && "invalid", className].filter(Boolean).join(" ")} onChange={(e) => onChange(e.target.value)} {...rest}>
      {options.map((o) => (typeof o === "string" ? <option key={o} value={o}>{o}</option> : <option key={o.value} value={o.value}>{o.label as string}</option>))}
    </select>
  );
});

export interface NumberInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "value" | "onChange" | "min" | "max" | "step"> {
  value: number | null;
  /** Called on blur / Enter with a parsed number (not on every key). */
  onCommit: (value: number) => void;
  integer?: boolean;
  min?: number;
  max?: number;
  step?: number;
  invalid?: boolean;
}

/** A number box that commits on blur or Enter; keeps the text while typing. */
export function NumberInput({ value, onCommit, integer, min, max, step, invalid, className, ...rest }: NumberInputProps) {
  const [text, setText] = useState(value === null || value === undefined ? "" : String(value));
  const sent = useRef(value);
  useEffect(() => { setText(value === null || value === undefined ? "" : String(value)); sent.current = value; }, [value]);
  const parsed = text.trim() === "" ? NaN : Number(text);
  const bad = Number.isNaN(parsed) || (integer && !Number.isInteger(parsed)) || (min !== undefined && parsed < min) || (max !== undefined && parsed > max);
  // Enter then blur must not send the same value twice
  const commit = () => { if (!bad && parsed !== value && parsed !== sent.current) { sent.current = parsed; onCommit(parsed); } };
  return (
    <input className={["k-input k-num", (invalid || (bad && text !== "")) && "invalid", className].filter(Boolean).join(" ")} inputMode={integer ? "numeric" : "decimal"}
      value={text} onChange={(e) => setText(e.target.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === "Enter") commit(); if (e.key === "Escape") setText(String(value ?? "")); }}
      step={step} min={min} max={max} aria-invalid={invalid || bad || undefined} {...rest} />
  );
}

export interface SliderProps {
  value: number;
  min: number;
  max: number;
  step?: number;
  integer?: boolean;
  onCommit: (value: number) => void;
  disabled?: boolean;
  id?: string;
  "aria-label"?: string;
}

/** Range + number box. The range commits on release; the box on blur/Enter. */
export function Slider({ value, min, max, step, integer, onCommit, disabled, id, ...rest }: SliderProps) {
  const [live, setLive] = useState(value);
  // the last value sent: keyUp and blur both end a keyboard change, before the new `value` comes back
  const sent = useRef(value);
  useEffect(() => { setLive(value); sent.current = value; }, [value]);
  const s = step ?? (integer ? Math.max(1, Math.round((max - min) / 100)) : (max - min) / 100);
  const pct = max > min ? ((live - min) / (max - min)) * 100 : 0;
  const done = () => {
    const v = integer ? Math.round(live) : live;
    if (v !== value && v !== sent.current) { sent.current = v; onCommit(v); }
  };
  return (
    <div className="k-slider-row">
      <input id={id} type="range" className="k-range" min={min} max={max} step={s} value={live} disabled={disabled} aria-label={rest["aria-label"]}
        style={{ backgroundSize: `${pct}% 100%` }} onChange={(e) => setLive(Number(e.target.value))} onPointerUp={done} onKeyUp={done} onBlur={done} />
      <NumberInput value={value} onCommit={onCommit} integer={integer} min={min} max={max} disabled={disabled} aria-label={rest["aria-label"]} />
    </div>
  );
}

export interface ToggleProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label?: ReactNode;
  hint?: ReactNode;
  disabled?: boolean;
  id?: string;
  "aria-label"?: string;
}

export function Toggle({ checked, onChange, label, hint, disabled, id, ...rest }: ToggleProps) {
  const auto = useId();
  return (
    <label className={"k-toggle" + (disabled ? " disabled" : "")} htmlFor={id ?? auto}>
      <input id={id ?? auto} type="checkbox" role="switch" checked={checked} disabled={disabled} aria-label={rest["aria-label"]}
        onChange={(e) => onChange(e.target.checked)} />
      <span className="k-toggle-track" aria-hidden />
      {(label || hint) && (
        <span className="k-toggle-text">
          {label && <span className="k-label">{label}</span>}
          {hint && <span className="k-hint">{hint}</span>}
        </span>
      )}
    </label>
  );
}

export interface WellInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "size"> {
  /** The small-caps word inside the well ("Filter", "Search items"). */
  label: ReactNode;
  /** Text after the input (a count). */
  note?: ReactNode;
  compact?: boolean;
  wellStyle?: React.CSSProperties;
}

/** An input set in a well with its label inside it. */
export const WellInput = forwardRef<HTMLInputElement, WellInputProps>(function WellInput({ label, note, compact, wellStyle, id, className, ...rest }, ref) {
  const auto = useId();
  const fid = id ?? auto;
  return (
    <div className={["k-well", compact && "compact", className].filter(Boolean).join(" ")} style={wellStyle}>
      <label className="k-label" htmlFor={fid}>{label}</label>
      <input ref={ref} id={fid} autoComplete="off" spellCheck={false} {...rest} />
      {note !== undefined && note !== null && <span className="k-well-note">{note}</span>}
    </div>
  );
});

const FLAME = "M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.072-2.143-.224-4.054 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.153.433-2.294 1-3a2.5 2.5 0 0 0 2.5 2.5z";

/** The flame toggle of mod lists: lit (on) or out. `label` is the mod's name. */
export function FlameToggle({ on, onChange, label, disabled }: { on: boolean; onChange: (on: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <button type="button" className={"k-flame" + (on ? " on" : "")} aria-pressed={on} aria-label={`Light ${label}`} title={on ? `${label} is lit: put it out` : `${label} is out: light it`}
      disabled={disabled} onClick={() => onChange(!on)}>
      <svg viewBox="0 0 24 24" aria-hidden><path d={FLAME} /></svg>
    </button>
  );
}
