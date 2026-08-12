import { useRef } from 'react';

/**
 * A two-or-more way choice, rendered as a real radiogroup so arrow keys move between options and
 * screen readers announce the selection. Roving tabindex keeps the group a single tab stop.
 */
export default function SegmentedControl({ label, value, options, onChange, disabled = false }) {
  const buttonRefs = useRef([]);
  const currentIndex = Math.max(0, options.findIndex((option) => option.value === value));

  const select = (index) => {
    const next = options[index];
    if (!next) return;

    onChange(next.value);
    // Focus has to follow the selection. Without this the user is left on the option they moved
    // away from, which now reports aria-checked="false" — so a screen reader announces the
    // deselected option and never names the one that was actually chosen.
    buttonRefs.current[index]?.focus();
  };

  const handleKeyDown = (event) => {
    // The buttons refuse clicks while disabled; the arrow keys are handled on the group, so they
    // have to refuse them too rather than changing a setting the UI is showing as locked.
    if (disabled) return;

    const { key } = event;

    if (key === 'ArrowRight' || key === 'ArrowDown') {
      event.preventDefault();
      select((currentIndex + 1) % options.length);
    } else if (key === 'ArrowLeft' || key === 'ArrowUp') {
      event.preventDefault();
      select((currentIndex - 1 + options.length) % options.length);
    } else if (key === 'Home') {
      event.preventDefault();
      select(0);
    } else if (key === 'End') {
      event.preventDefault();
      select(options.length - 1);
    }
  };

  return (
    <div
      role="radiogroup"
      aria-label={label}
      onKeyDown={handleKeyDown}
      className="flex gap-1 rounded border border-line bg-surface p-1"
    >
      {options.map((option, index) => {
        const selected = option.value === value;
        const Icon = option.icon;

        return (
          <button
            key={option.value}
            ref={(element) => {
              buttonRefs.current[index] = element;
            }}
            type="button"
            role="radio"
            aria-checked={selected}
            tabIndex={selected ? 0 : -1}
            disabled={disabled}
            onClick={() => onChange(option.value)}
            className={[
              'inline-flex flex-1 items-center justify-center gap-1.5 rounded-sm px-3 py-1.5',
              'text-sm transition-colors duration-150',
              'disabled:cursor-not-allowed disabled:opacity-45',
              selected ? 'bg-accent font-medium text-accent-fg' : 'text-fg-muted hover:bg-raised hover:text-fg',
            ].join(' ')}
          >
            {Icon && <Icon size={14} aria-hidden="true" />}
            {option.label}
          </button>
        );
      })}
    </div>
  );
}
