// Buttons. Variants: primary (the hot ember fill, one per view; text #170803, never white), secondary (an edge),
// ghost (no edge), danger, danger-fill (enabled only after typing). "molten" is an old name for primary.
// Text first: a button with words shows no icon; icon-only buttons are IconButton (with a label).
import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from "react";

export type ButtonVariant = "primary" | "molten" | "secondary" | "ghost" | "danger" | "danger-fill";
export type ButtonSize = "s" | "m" | "l";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  /** s: 32 px (table toolbars), m: 38 px, l: 44 px. */
  size?: ButtonSize;
  /** Shown only when the button has no words (IconButton). */
  icon?: ReactNode;
  /** Shows a spinner and disables the button. */
  loading?: boolean;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = "secondary", size = "m", icon, loading, disabled, className, children, type = "button", ...rest }, ref,
) {
  const v = variant === "molten" ? "primary" : variant;
  const cls = ["k-btn", v !== "secondary" && `k-btn-${v}`, size !== "m" && `k-btn-${size}`, className].filter(Boolean).join(" ");
  const words = children !== undefined && children !== null && children !== false && children !== "";
  return (
    <button ref={ref} type={type} className={cls} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading ? <Spinner /> : words ? null : icon}
      {children}
    </button>
  );
});

export interface IconButtonProps extends Omit<ButtonProps, "children"> {
  /** Accessible name, also the tooltip. */
  label: string;
}

export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton(
  { label, variant = "ghost", className, ...rest }, ref,
) {
  return <Button ref={ref} variant={variant} aria-label={label} title={label} className={["k-btn-icon", className].filter(Boolean).join(" ")} {...rest} />;
});

/** Only inside a busy button. */
export function Spinner({ size }: { size?: number }) {
  return <span className="k-spin" role="status" aria-label="Loading" style={size ? { width: size, height: size } : undefined} />;
}
