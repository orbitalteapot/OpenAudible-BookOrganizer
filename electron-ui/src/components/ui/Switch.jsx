import { useId } from 'react';

/**
 * An on/off setting that takes effect at once. A real `role="switch"` button, so it is one tab
 * stop, toggles with Space or Enter, and is announced as "on" or "off" rather than "checked". The
 * thumb's position says the state as well as its colour.
 *
 * The button is larger than the track it draws, so the hit target is 32px tall; the negative
 * margin keeps the track centred on the label's first line all the same.
 *
 * `disabled` refuses changes without the disabled attribute, so the switch keeps keyboard focus while
 * a change is being saved instead of dropping it back to the top of the page.
 */
export default function Switch({ label, description, checked, onChange, disabled = false }) {
  const id = useId();
  const labelId = `${id}-label`;
  const descriptionId = description ? `${id}-description` : undefined;

  return (
    <div className="flex items-start justify-between gap-4">
      <div className="min-w-0">
        <span id={labelId} className="block text-sm text-fg">
          {label}
        </span>
        {description && (
          <span id={descriptionId} className="mt-1 block text-2xs text-fg-subtle">
            {description}
          </span>
        )}
      </div>

      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-labelledby={labelId}
        aria-describedby={descriptionId}
        aria-disabled={disabled || undefined}
        onClick={() => {
          if (!disabled) onChange(!checked);
        }}
        className="-my-1.5 inline-flex h-8 w-11 shrink-0 items-center justify-center rounded aria-disabled:cursor-not-allowed aria-disabled:opacity-45"
      >
        <span
          aria-hidden="true"
          className={[
            'inline-flex h-5 w-9 items-center rounded-full border transition-colors duration-150',
            checked ? 'border-accent bg-accent' : 'border-line-strong bg-raised',
          ].join(' ')}
        >
          {/* Forced colours drop fills, so the thumb is drawn in the system text colour to stay visible. */}
          <span
            className={[
              'inline-block h-3.5 w-3.5 rounded-full transition-transform duration-150',
              'forced-color-adjust-none forced-colors:bg-[CanvasText]',
              checked ? 'translate-x-[18px] bg-accent-fg' : 'translate-x-0.5 bg-fg-muted',
            ].join(' ')}
          />
        </span>
      </button>
    </div>
  );
}
