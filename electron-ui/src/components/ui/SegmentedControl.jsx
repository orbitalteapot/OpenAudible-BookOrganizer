import { useRef, useState } from 'react';

/**
 * A two-or-more way choice, rendered as a real radiogroup so arrow keys move between options and
 * screen readers announce the selection. Roving tabindex keeps the group a single tab stop.
 *
 * Arrow keys choose as they move, which suits a choice that is cheap to change (a theme). Set
 * `manualActivation` for one that sets something in motion (automatic sorting starts a sort): the
 * arrows then only move focus, and Space or Enter chooses, so looking through the options does not
 * choose every one on the way.
 *
 * `disabled` refuses changes without the disabled attribute, so an option that has focus keeps it
 * while the group is busy saving, and a locked group can still be reached and its hint heard.
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
  manualActivation = false,
  describedBy,
  iconOnly = false,
  className = 'flex',
}) {
  const buttonRefs = useRef([]);
  const currentIndex = Math.max(0, options.findIndex((option) => option.value === value));
  // The option the arrows moved to without choosing it (manual activation), which holds the tab stop.
  const [focusIndex, setFocusIndex] = useState(null);
  const tabStop = focusIndex ?? currentIndex;

  const choose = (option) => {
    if (!disabled && option.value !== value) onChange(option.value);
  };

  const moveTo = (index) => {
    if (manualActivation) setFocusIndex(index);
    else choose(options[index]);
    // Focus has to follow. Without this the user is left on the option they moved away from, which
    // now reports aria-checked="false" — so a screen reader announces the deselected option and
    // never names the one that was actually chosen.
    buttonRefs.current[index]?.focus();
  };

  const handleKeyDown = (event) => {
    // A locked group still lets focus move (manual activation), but never changes the setting.
    if (disabled && !manualActivation) return;

    const { key } = event;

    if (key === 'ArrowRight' || key === 'ArrowDown') {
      event.preventDefault();
      moveTo((tabStop + 1) % options.length);
    } else if (key === 'ArrowLeft' || key === 'ArrowUp') {
      event.preventDefault();
      moveTo((tabStop - 1 + options.length) % options.length);
    } else if (key === 'Home') {
      event.preventDefault();
      moveTo(0);
    } else if (key === 'End') {
      event.preventDefault();
      moveTo(options.length - 1);
    }
  };

  // Leaving the group hands the tab stop back to the chosen option.
  const handleBlur = (event) => {
    if (!event.currentTarget.contains(event.relatedTarget)) setFocusIndex(null);
  };

  return (
    <div
      role="radiogroup"
      aria-label={label}
      aria-describedby={describedBy}
      aria-disabled={disabled || undefined}
      onKeyDown={handleKeyDown}
      onBlur={handleBlur}
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
            tabIndex={index === tabStop ? 0 : -1}
            aria-disabled={disabled || undefined}
            onClick={() => choose(option)}
            className={[
              'inline-flex min-h-8 flex-1 items-center justify-center gap-2 whitespace-nowrap rounded-sm px-3',
              'text-sm transition-colors duration-150',
              'aria-disabled:cursor-not-allowed aria-disabled:opacity-45',
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
