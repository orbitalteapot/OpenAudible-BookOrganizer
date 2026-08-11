import { Loader2 } from 'lucide-react';

const VARIANTS = {
  primary: 'bg-accent text-accent-fg font-medium hover:bg-accent-hover',
  secondary: 'bg-raised text-fg border border-line-strong hover:bg-line',
  ghost: 'text-fg-muted hover:bg-raised hover:text-fg',
  danger: 'bg-raised text-critical border border-line-strong hover:bg-critical/10',
};

/**
 * The only button in the app. Every variant is the same height so buttons, inputs and selects
 * sit on one line without per-site padding overrides.
 */
export default function Button({
  variant = 'secondary',
  icon: Icon,
  loading = false,
  disabled = false,
  children,
  className = '',
  type = 'button',
  ...props
}) {
  const iconOnly = !children;

  return (
    <button
      type={type}
      disabled={disabled || loading}
      className={[
        'inline-flex h-control shrink-0 items-center justify-center gap-2 rounded',
        'text-sm transition-colors duration-150',
        'disabled:cursor-not-allowed disabled:opacity-45',
        iconOnly ? 'w-control' : 'px-3.5',
        VARIANTS[variant],
        className,
      ].join(' ')}
      {...props}
    >
      {loading ? (
        <Loader2 size={15} className="animate-spin" aria-hidden="true" />
      ) : (
        Icon && <Icon size={15} aria-hidden="true" />
      )}
      {children}
    </button>
  );
}
