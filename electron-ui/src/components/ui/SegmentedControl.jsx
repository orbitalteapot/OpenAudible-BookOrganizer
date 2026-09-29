import { useRef } from 'react';

/**
 * A two-or-more way choice, rendered as a real radiogroup so arrow keys move between options and
 * screen readers announce the selection. Roving tabindex keeps the group a single tab stop.
 *
 * `iconOnly` shows each option's icon alone, keeping its label as the accessible name and tooltip;
 * `className` lays the group out (a vertical stack in a narrow rail, say).
 */
export default function SegmentedControl({
  label,
  value,
  options,
  onChange,
  disabled = false,
  describedBy,
  iconOnly = false,
  className = 'flex',
}) {
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
      aria-describedby={describedBy}
      aria-disabled={disabled || undefined}
      onKeyDown={handleKeyDown}
      // A filled track rather than an outline: 2px of track around 32px options is exactly the
      // control height, so the group lines up with buttons and inputs beside it. The options take a
      // min-height, not a height: in a vertical group flex-1 would otherwise shrink them to nothing.
      className={`${className} gap-0.5 rounded bg-raised p-0.5`}
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
            aria-label={iconOnly ? option.label : undefined}
            title={iconOnly ? option.label : undefined}
            tabIndex={selected ? 0 : -1}
            disabled={disabled}
            onClick={() => onChange(option.value)}
            className={[
              'inline-flex min-h-8 flex-1 items-center justify-center gap-2 whitespace-nowrap rounded-sm px-3',
              'text-sm transition-colors duration-150',
              'disabled:cursor-not-allowed disabled:opacity-45',
              selected ? 'bg-accent font-medium text-accent-fg' : 'text-fg-muted hover:bg-fg/5 hover:text-fg',
            ].join(' ')}
          >
            {Icon && <Icon size={14} aria-hidden="true" />}
            {!iconOnly && option.label}
          </button>
        );
      })}
    </div>
  );
}
