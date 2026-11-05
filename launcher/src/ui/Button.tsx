// Buttons. Primary = ember fill with dark text (#0f0d0c), never white.
import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from "react";

export type ButtonVariant = "primary" | "molten" | "secondary" | "ghost" | "danger" | "danger-fill";
export type ButtonSize = "s" | "m" | "l";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** Leading icon (a lucide-react element). */
  icon?: ReactNode;
  /** Shows a spinner and disables the button. */
  loading?: boolean;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = "secondary", size = "m", icon, loading, disabled, className, children, type = "button", ...rest }, ref,
) {
  const cls = ["k-btn", variant !== "secondary" && `k-btn-${variant}`, size !== "m" && `k-btn-${size}`, className].filter(Boolean).join(" ");
  return (
    <button ref={ref} type={type} className={cls} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading ? <Spinner /> : icon}
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

export function Spinner({ size }: { size?: number }) {
  return <span className="k-spin" role="status" aria-label="Loading" style={size ? { width: size, height: size } : undefined} />;
}
