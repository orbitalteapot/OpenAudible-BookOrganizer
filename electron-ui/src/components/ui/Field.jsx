import { useId, useLayoutEffect, useRef } from 'react';

/**
 * A labelled control. The label is bound to the control by id rather than by wrapping, so the
 * control can sit beside a button in the same row.
 *
 * Set `group` when the child is a composite (a radiogroup, say) rather than a single form control:
 * a <label for> pointing at a group is not a valid association, and the group carries its own
 * accessible name instead.
 */
export function Field({ label, hint, group = false, children, className = '' }) {
  const id = useId();

  return (
    <div className={className}>
      {group ? (
        <span className="mb-1.5 block text-xs font-medium text-fg-muted">{label}</span>
      ) : (
        <label htmlFor={id} className="mb-1.5 block text-xs font-medium text-fg-muted">
          {label}
        </label>
      )}
      {children(id)}
      {hint && <p className="mt-1.5 text-2xs text-fg-subtle">{hint}</p>}
    </div>
  );
}

/**
 * A read-only path display with an optional leading icon.
 *
 * The end of a path is the part that identifies it, so the field is scrolled to its end rather
 * than showing the first few characters of a long home directory. Scrolling rather than
 * `direction: rtl`, which does keep the tail in view but reorders the leading separator to the
 * wrong end, rendering "/home/me/books.csv" as "home/me/books.csv/".
 */
export function PathInput({ id, value, placeholder, icon: Icon, invalid = false }) {
  const inputRef = useRef(null);

  useLayoutEffect(() => {
    const input = inputRef.current;
    if (input) input.scrollLeft = input.scrollWidth;
  }, [value]);

  return (
    <div className="relative min-w-0 flex-1">
      {Icon && (
        <Icon
          size={14}
          aria-hidden="true"
          className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-subtle"
        />
      )}
      <input
        ref={inputRef}
        id={id}
        type="text"
        readOnly
        value={value}
        placeholder={placeholder}
        title={value || undefined}
        className={[
          'h-control w-full rounded border bg-surface pr-3 text-sm text-fg-muted',
          'placeholder:text-fg-subtle',
          Icon ? 'pl-9' : 'pl-3',
          invalid ? 'border-critical/50' : 'border-line',
        ].join(' ')}
      />
    </div>
  );
}

export function TextInput({ id, icon: Icon, className = '', ...props }) {
  return (
    <div className={`relative ${className}`}>
      {Icon && (
        <Icon
          size={14}
          aria-hidden="true"
          className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-subtle"
        />
      )}
      <input
        id={id}
        className={[
          'h-control w-full rounded border border-line bg-surface pr-3 text-sm text-fg',
          'placeholder:text-fg-subtle',
          'focus:border-accent/60',
          Icon ? 'pl-9' : 'pl-3',
        ].join(' ')}
        {...props}
      />
    </div>
  );
}
