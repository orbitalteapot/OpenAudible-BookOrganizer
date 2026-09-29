import { forwardRef, useId } from 'react';
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
 *
 * `disabledReason` disables the button and says why. Unlike `disabled`, it keeps the button
 * focusable (aria-disabled), so keyboard and screen-reader users can land on it and hear the
 * reason instead of finding the control silently missing from the tab order. When the page already
 * shows the reason, pass that element's id as `reasonId` and the button points at it instead of
 * carrying a hidden copy of its own.
 */
const Button = forwardRef(function Button(
  {
    variant = 'secondary',
    icon: Icon,
    loading = false,
    disabled = false,
    disabledReason,
    reasonId: shownReasonId,
    children,
    className = '',
    type = 'button',
    onClick,
    'aria-describedby': describedBy,
    ...props
  },
  ref
) {
  const ownReasonId = useId();
  const iconOnly = !children;
  const blocked = Boolean(disabledReason);
  const reasonId = shownReasonId ?? ownReasonId;

  return (
    <>
      <button
        ref={ref}
        type={type}
        disabled={disabled || loading}
        aria-disabled={blocked || undefined}
        aria-describedby={[blocked && reasonId, describedBy].filter(Boolean).join(' ') || undefined}
        title={disabledReason}
        onClick={blocked ? undefined : onClick}
        className={[
          'inline-flex h-control shrink-0 items-center justify-center gap-2 rounded',
          'text-sm transition-colors duration-150',
          'disabled:cursor-not-allowed disabled:opacity-45',
          'aria-disabled:cursor-not-allowed aria-disabled:opacity-45',
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
      {/* Outside the button, or it would become part of the button's name. */}
      {blocked && !shownReasonId && (
        <span id={reasonId} hidden>
          {disabledReason}
        </span>
      )}
    </>
  );
});

export default Button;
